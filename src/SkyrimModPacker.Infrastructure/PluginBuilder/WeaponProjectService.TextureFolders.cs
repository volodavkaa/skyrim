using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<TextureFolderPlan>> PreviewTextureFolderAsync(string projectFilePath, string sourceFolder, string targetSubdirectory,
        string gameDataPath, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        using var lease = await WorkspaceFileGate.AcquireAsync(projectFilePath, cancellationToken);
        return await BuildTextureFolderPlanAsync(projectFilePath, sourceFolder, targetSubdirectory, gameDataPath, cancellationToken);
    }, cancellationToken);

    private static async Task<TextureFolderPlan> BuildTextureFolderPlanAsync(string project, string source, string mapping, string game, CancellationToken token)
    {
        var (folder, _) = await ReadResourceProjectAsync(project, game, token);
        if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source)) throw new IOException("Select an existing texture folder.");
        PackingPathGuard.EnsureNoLinks(source);
        if (Path.IsPathRooted(mapping) || mapping.StartsWith('/') || mapping.StartsWith('\\')) throw new IOException("Destination must be relative to textures.");
        mapping = mapping.Replace('\\', '/').TrimEnd('/');
        if (mapping.Length > 0) ValidateResourcePath("textures/" + mapping + "/check.dds");
        var items = new List<TextureFolderItem>(); var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var skipped = 0;
        await Visit(new DirectoryInfo(source), 0);
        return new(Path.GetFullPath(project), await BatchHashAsync(project, token), Path.GetFullPath(source), mapping,
            items.OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), skipped);

        async Task Visit(DirectoryInfo directory, int depth)
        {
            if (depth > 64) throw new IOException("Texture folder nesting exceeds 64 levels.");
            token.ThrowIfCancellationRequested(); PackingPathGuard.EnsureNoLinks(directory.FullName);
            foreach (var file in directory.EnumerateFiles())
            {
                token.ThrowIfCancellationRequested();
                if (!file.Extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (items.Count >= 10000) throw new IOException("Texture import exceeds 10000 files. Select a smaller folder.");
                RequireSourceFile(file.FullName, ".dds");
                SkyrimModPacker.Infrastructure.Preview.DdsResourceInspector.Inspect(file.FullName, token);
                // The selected folder's CONTENTS map to the explicit destination, without an extra folder name.
                var relative = NormalizeResource("textures/" + (mapping.Length == 0 ? "" : mapping + "/") + Path.GetRelativePath(source, file.FullName).Replace('\\', '/'));
                var target = ResourceTarget(folder, relative, game);
                var hash = await BatchHashAsync(file.FullName, token);
                var identical = File.Exists(target) && await BatchHashAsync(target, token) == hash;
                var conflict = Directory.Exists(target) || File.Exists(target) && !identical ? "Different destination exists. Use explicit resource replacement with backup." : null;
                if (!destinations.Add(relative)) conflict = "Duplicate destination.";
                items.Add(new(file.FullName, relative, file.Length, hash, identical, conflict));
            }
            foreach (var child in directory.EnumerateDirectories()) await Visit(child, depth + 1);
        }
    }

    public Task<Result<IReadOnlyList<string>>> ImportTextureFolderAsync(TextureFolderPlan plan, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<string>>(async () =>
        {
            using var lease = await WorkspaceFileGate.AcquireAsync(plan.ProjectPath, cancellationToken);
            WorkspaceWriteExpectation.Check(plan.ProjectPath);
            var original = new WorkspaceFileSnapshot(plan.ProjectPath);
            var current = await BuildTextureFolderPlanAsync(plan.ProjectPath, plan.SourceFolder, plan.TargetSubdirectory, gameDataPath, cancellationToken);
            if (!current.CanImport || current.ProjectSha256 != plan.ProjectSha256 || !current.Items.SequenceEqual(plan.Items) || current.SkippedFiles != plan.SkippedFiles)
                throw new IOException("Textures or project changed after preview. Preview again and resolve conflicts.");
            var (folder, document) = await ReadResourceProjectAsync(plan.ProjectPath, gameDataPath, cancellationToken);
            var files = current.Items.Select(i => (i.Identical ? ResourceTarget(folder, i.RelativePath, gameDataPath) : i.SourcePath, i.RelativePath)).ToArray();
            await CopyResourcesAsync(plan.ProjectPath, folder, document, files, gameDataPath, original, cancellationToken,
                current.Items.ToDictionary(i => i.RelativePath, i => i.Sha256, StringComparer.OrdinalIgnoreCase));
            return current.Items.Select(i => i.RelativePath).ToArray();
        }, cancellationToken);
}
