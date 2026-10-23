using System.Security.Cryptography;
using System.Text.Json;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.Persistence;

public sealed class WorkspaceHistoryService(string appDataDirectory) : IWorkspaceHistoryService
{
    private readonly string _root = Path.GetFullPath(Path.Combine(appDataDirectory, "EditorHistory"));
    private readonly Dictionary<Guid, WorkspaceCheckpoint> _owned = [];

    public async Task<WorkspaceRevision> InspectAsync(WorkspaceContext context, bool includeResources = true, CancellationToken token = default)
    {
        PluginWriteGuard.EnsureOutsideGame(context.PluginPath, context.GameDataPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(context.PluginPath))!;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(context.PluginPath) };
        if (!string.IsNullOrWhiteSpace(context.ProjectFilePath))
        {
            PluginWriteGuard.EnsureOutsideGame(context.ProjectFilePath, context.GameDataPath);
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(context.ProjectFilePath)), folder, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The project metadata must be beside its plugin.");
            paths.Add(Path.GetFileName(context.ProjectFilePath));
            if (includeResources && File.Exists(context.ProjectFilePath))
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(context.ProjectFilePath, token));
                foreach (var item in (json.RootElement.TryGetProperty("Resources", out var resources) ? resources.EnumerateArray().ToArray() : []))
                {
                    var relative = PackingPathGuard.NormalizeEntryPath(item.GetString()!);
                    if (!(relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) &&
                        !(relative.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("Unsupported registered resource: " + relative);
                    paths.Add(relative);
                }
            }
        }
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var path = PackingPathGuard.ResolveUnderRoot(folder, relative);
            PluginWriteGuard.EnsureOutsideGame(path, context.GameDataPath);
            hashes[relative] = await HashAsync(path, token);
        }
        return new(context, includeResources, hashes);
    }

    public async Task<WorkspaceCheckpoint> CaptureAsync(WorkspaceContext context, bool includeResources,
        IProgress<EditorOperationProgress>? progress = null, CancellationToken token = default)
    {
        PluginWriteGuard.EnsureOutsideGame(_root, context.GameDataPath);
        var revision = await InspectAsync(context, includeResources, token);
        var checkpoint = new WorkspaceCheckpoint(Guid.NewGuid(), revision);
        var directory = Path.Combine(_root, checkpoint.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        var index = 0;
        foreach (var (relative, hash) in revision.Files)
        {
            token.ThrowIfCancellationRequested();
            if (hash != null)
            {
                var source = PackingPathGuard.ResolveUnderRoot(Path.GetDirectoryName(context.PluginPath)!, relative);
                var copy = PackingPathGuard.ResolveUnderRoot(directory, index + ".bin");
                await CopyAsync(source, copy, token);
                if (await HashAsync(copy, token) != hash) throw new IOException("Workspace changed while creating a backup: " + relative);
            }
            index++;
            progress?.Report(new(index, revision.Files.Count, relative));
        }
        if (!revision.Matches(await InspectAsync(context, includeResources, token))) throw new IOException("Workspace changed while creating a backup.");
        lock (_owned) _owned[checkpoint.Id] = checkpoint;
        return checkpoint;
    }

    public async Task RestoreAsync(WorkspaceCheckpoint desired, WorkspaceCheckpoint expected,
        IProgress<EditorOperationProgress>? progress = null, CancellationToken token = default)
    {
        lock (_owned)
            if (!_owned.TryGetValue(desired.Id, out var d) || d != desired || !_owned.TryGetValue(expected.Id, out var e) || e != expected)
                throw new IOException("This history checkpoint is not owned by this session.");
        if (desired.Revision.Context != expected.Revision.Context || desired.Revision.IncludesResources != expected.Revision.IncludesResources)
            throw new IOException("History checkpoints belong to different workspaces.");
        var context = expected.Revision.Context;
        var folder = Path.GetDirectoryName(context.PluginPath)!;
        var names = desired.Revision.Files.Keys.Union(expected.Revision.Files.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var leases = new List<IDisposable>();
        var changed = new List<string>();
        try
        {
            // Matches the existing builder's lock order: plugin, metadata, then assets.
            foreach (var name in names.OrderBy(n => n.Equals(Path.GetFileName(context.PluginPath), StringComparison.OrdinalIgnoreCase) ? 0 :
                         n.Equals(Path.GetFileName(context.ProjectFilePath), StringComparison.OrdinalIgnoreCase) ? 1 : 2).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var path = PackingPathGuard.ResolveUnderRoot(folder, name);
                PluginWriteGuard.EnsureOutsideGame(path, context.GameDataPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                leases.Add(await WorkspaceFileGate.AcquireAsync(path, token));
            }
            if (!expected.Revision.Matches(await InspectAsync(context, expected.Revision.IncludesResources, token)))
                throw new IOException("Undo/Redo refused: workspace files changed outside this history.");
            foreach (var name in names)
                if (!expected.Revision.Files.ContainsKey(name) && File.Exists(PackingPathGuard.ResolveUnderRoot(folder, name)))
                    throw new IOException("Undo/Redo refused: an unrelated file now occupies " + name);
            var index = 0;
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                changed.Add(name);
                await PublishAsync(desired, name, token);
                index++;
                progress?.Report(new(index, names.Length, name));
            }
        }
        catch (Exception original)
        {
            foreach (var name in changed.AsEnumerable().Reverse())
            {
                try { await PublishAsync(expected, name, CancellationToken.None); }
                catch (Exception rollback) { original.Data["HistoryRollback:" + name] = rollback.Message; }
            }
            throw;
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    private async Task PublishAsync(WorkspaceCheckpoint checkpoint, string name, CancellationToken token)
    {
        var context = checkpoint.Revision.Context;
        var target = PackingPathGuard.ResolveUnderRoot(Path.GetDirectoryName(context.PluginPath)!, name);
        PluginWriteGuard.EnsureOutsideGame(target, context.GameDataPath);
        if (!checkpoint.Revision.Files.TryGetValue(name, out var hash) || hash == null)
        {
            if (File.Exists(target)) File.Delete(target);
            return;
        }
        var index = checkpoint.Revision.Files.Keys.ToList().FindIndex(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        var source = PackingPathGuard.ResolveUnderRoot(Path.Combine(_root, checkpoint.Id.ToString("N")), index + ".bin");
        if (await HashAsync(source, token) != hash) throw new IOException("History backup is missing or changed: " + name);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".restore.tmp";
        try
        {
            await CopyAsync(source, temporary, token);
            token.ThrowIfCancellationRequested();
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        PackingPathGuard.EnsureNoLinks(source); PackingPathGuard.EnsureNoLinks(target);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true);
    }
    private static async Task<string?> HashAsync(string path, CancellationToken token)
    {
        PackingPathGuard.EnsureNoLinks(path);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
