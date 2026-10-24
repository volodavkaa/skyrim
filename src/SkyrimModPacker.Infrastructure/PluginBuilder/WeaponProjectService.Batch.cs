using System.Security.Cryptography;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<BatchResourcePlan>> PreviewBatchImportAsync(string projectFilePath, IReadOnlyList<string> sourcePaths, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            using var lease = await WorkspaceFileGate.AcquireAsync(projectFilePath, cancellationToken);
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            if (sourcePaths.Count == 0) throw new InvalidOperationException("Select at least one NIF or DDS file.");
            var id = Guid.NewGuid();
            var items = new List<BatchResourceItem>();
            foreach (var source in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(source).ToLowerInvariant();
                if (ext is not (".nif" or ".dds")) throw new InvalidOperationException("Batch import supports NIF and DDS files only.");
                RequireSourceFile(source, ext);
                var relative = BatchDestination(document.Name, id, source);
                var target = ResourceTarget(folder, relative, gameDataPath);
                items.Add(new(Path.GetFullPath(source), relative, await BatchHashAsync(source, cancellationToken), ModelAssignment.ResourceOnly,
                    File.Exists(target) || Directory.Exists(target) ? "Destination exists." : null));
            }
            var duplicates = items.GroupBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new BatchResourcePlan(id, Path.GetFullPath(projectFilePath), await BatchHashAsync(projectFilePath, cancellationToken),
                items.Select(i => duplicates.Contains(i.RelativePath) ? i with { Conflict = "Duplicate destination filename." } : i).ToArray());
        }, cancellationToken);

    public Task<Result<IReadOnlyList<string>>> ImportBatchAsync(BatchResourcePlan plan, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<string>>(async () =>
        {
            PluginWriteGuard.EnsureOutsideGame(plan.ProjectFilePath, gameDataPath);
            using var lease = await WorkspaceFileGate.AcquireAsync(plan.ProjectFilePath, cancellationToken);
            WorkspaceWriteExpectation.Check(plan.ProjectFilePath);
            var original = new WorkspaceFileSnapshot(plan.ProjectFilePath);
            var (folder, document) = await ReadResourceProjectAsync(plan.ProjectFilePath, gameDataPath, cancellationToken);
            if (await BatchHashAsync(plan.ProjectFilePath, cancellationToken) != plan.ProjectSha256)
                throw new IOException("The project changed after preview. Preview the batch again.");
            if (plan.Id == Guid.Empty || plan.Items.Count == 0 || plan.Items.Any(i => i.Conflict != null) ||
                plan.Items.Select(i => i.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Items.Count ||
                plan.Items.Count(i => i.Assignment == ModelAssignment.FirstPerson) > 1 || plan.Items.Count(i => i.Assignment == ModelAssignment.ThirdPerson) > 1)
                throw new InvalidOperationException("Resolve batch conflicts and select at most one model per view.");
            foreach (var item in plan.Items)
            {
                var ext = Path.GetExtension(item.SourcePath).ToLowerInvariant();
                if (ext is not (".nif" or ".dds") || !Enum.IsDefined(item.Assignment) || (ext != ".nif" && item.Assignment != ModelAssignment.ResourceOnly) ||
                    !item.RelativePath.Equals(BatchDestination(document.Name, plan.Id, item.SourcePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid batch destination or model assignment. Preview again.");
                RequireSourceFile(item.SourcePath, ext);
                ResourceTarget(folder, item.RelativePath, gameDataPath);
                if (await BatchHashAsync(item.SourcePath, cancellationToken) != item.Sha256) throw new IOException("A batch source changed after preview: " + item.SourcePath);
            }
            await CopyResourcesAsync(plan.ProjectFilePath, folder, document, plan.Items.Select(i => (i.SourcePath, i.RelativePath)).ToArray(), gameDataPath, original, cancellationToken,
                plan.Items.ToDictionary(i => i.RelativePath, i => i.Sha256, StringComparer.OrdinalIgnoreCase));
            return plan.Items.Select(i => i.RelativePath).ToArray();
        }, cancellationToken);

    private static async Task<string> BatchHashAsync(string path, CancellationToken token)
    {
        PackingPathGuard.EnsureNoLinks(path);
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static string BatchDestination(string name, Guid id, string source) => source.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
        ? NormalizeResource($"meshes/{name}/Batch/{id:N}/{Path.GetFileName(source)}")
        : NormalizeResource("textures/" + (RelativeToAssetRoot(source, "textures") ?? Path.Combine(name, Path.GetFileName(source))).Replace('\\', '/'));
}
