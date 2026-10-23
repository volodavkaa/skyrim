using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<string>> ImportModelAsync(string projectFilePath, string sourcePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
            using var lease = await WorkspaceFileGate.AcquireAsync(projectFilePath, cancellationToken);
            var original = new WorkspaceFileSnapshot(projectFilePath);
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            RequireSourceFile(sourcePath, ".nif");
            var relative = RelativeToAssetRoot(sourcePath, "meshes") ?? Path.Combine(document.Name, Path.GetFileName(sourcePath));
            // A custom file must not shadow a vanilla mesh shared by unrelated game objects.
            using (var master = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(
                Path.Combine(gameDataPath, "Skyrim.esm"), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE))
            {
                var normalized = relative.Replace('\\', '/');
                if (master.Weapons.Select(w => w.Model?.File.GivenPath).Concat(master.Statics.Select(s => s.Model?.File.GivenPath))
                    .Any(p => string.Equals(p?.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)))
                    relative = Path.Combine(document.Name, "Imported", Path.GetFileName(sourcePath));
            }
            var resource = NormalizeResource("meshes/" + relative.Replace('\\', '/'));
            await CopyResourcesAsync(projectFilePath, folder, document, [(sourcePath, resource)], gameDataPath, original, cancellationToken);
            return resource["meshes/".Length..].Replace('/', '\\');
        }, cancellationToken);

    public Task<Result<IReadOnlyList<string>>> ImportTexturesAsync(string projectFilePath, IReadOnlyList<string> sourcePaths,
        string gameDataPath, string targetSubdirectory = "", CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<string>>(async () =>
        {
            PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
            using var lease = await WorkspaceFileGate.AcquireAsync(projectFilePath, cancellationToken);
            var original = new WorkspaceFileSnapshot(projectFilePath);
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            // Validate even an unused destination, so absolute and traversing paths never pass silently.
            if (!string.IsNullOrWhiteSpace(targetSubdirectory)) ValidateResourcePath("textures/" + targetSubdirectory.Replace('\\', '/') + "/check.dds");
            var files = await Task.Run(() => CollectTextures(sourcePaths, targetSubdirectory, cancellationToken), cancellationToken);
            if (files.Count == 0) throw new InvalidOperationException("No DDS textures were selected.");
            await CopyResourcesAsync(projectFilePath, folder, document, files, gameDataPath, original, cancellationToken);
            return files.Select(f => f.Relative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }, cancellationToken);

    public Task<Result<IReadOnlyList<string>>> GetResourcesAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<string>>(async () =>
        {
            var (_, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            return document.Resources.ToArray();
        }, cancellationToken);

    public Task<Result<IReadOnlyList<string>>> ValidateResourcesAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<string>>(async () =>
        {
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            var missing = new List<string>();
            foreach (var relative in document.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResourceTarget(folder, relative, gameDataPath);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(relative);
            }
            if (missing.Count > 0) throw new IOException("Missing or empty project resources: " + string.Join(", ", missing));
            return document.Resources.ToArray();
        }, cancellationToken);

    public Task<Result<string>> ValidateModelAsync(string projectFilePath, string modelPath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var (folder, _) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            var relative = modelPath.Replace('\\', '/');
            if (relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) relative = relative[7..];
            var resource = NormalizeResource("meshes/" + relative);
            if (!resource.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Select a NIF model.");
            var target = ResourceTarget(folder, resource, gameDataPath);
            if (!File.Exists(target) || new FileInfo(target).Length == 0)
                throw new FileNotFoundException("Model not found in the project. Import your NIF first. Vanilla models may be stored in the game's BSA archives.");
            return target;
        }, cancellationToken);

    private static string NormalizeResource(string relative)
    {
        ValidateResourcePath(relative);
        return relative.Replace('\\', '/');
    }

    private static void ValidateResourcePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("An asset path is required.");
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Length < 2 || !(parts[0].Equals("meshes", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("textures", StringComparison.OrdinalIgnoreCase)) ||
            parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidOperationException("Asset paths must stay inside meshes or textures and cannot contain absolute paths or '..'.");
        var extension = Path.GetExtension(normalized);
        if (!(parts[0].Equals("meshes", StringComparison.OrdinalIgnoreCase) ? extension.Equals(".nif", StringComparison.OrdinalIgnoreCase) : extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Only NIF models and DDS textures are supported.");
    }

    private static string ResourceTarget(string folder, string relative, string gameDataPath)
    {
        ValidateResourcePath(relative);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Asset escapes the project folder.");
        PluginWriteGuard.EnsureOutsideGame(target, gameDataPath);
        return target;
    }

    private static void RequireSourceFile(string path, string extension)
    {
        PackingPathGuard.EnsureNoLinks(path);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"Select an existing non-empty {extension} file using an absolute path.");
    }

    // Keep paths already beneath meshes/textures; no parsing or rewriting of NIF contents.
    private static string? RelativeToAssetRoot(string path, string rootName)
    {
        for (var directory = Path.GetDirectoryName(path); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            if (Path.GetFileName(directory).Equals(rootName, StringComparison.OrdinalIgnoreCase)) return Path.GetRelativePath(directory, path);
        return null;
    }

    private static List<(string Source, string Relative)> CollectTextures(IReadOnlyList<string> paths, string targetSubdirectory, CancellationToken token)
    {
        var result = new List<(string, string)>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Select absolute texture paths.");
            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                var prefix = directory.Name.Equals("textures", StringComparison.OrdinalIgnoreCase) ? "" :
                    RelativeToAssetRoot(path, "textures") ?? Path.Combine(targetSubdirectory, directory.Name);
                Visit(directory, prefix);
            }
            else
            {
                RequireSourceFile(path, ".dds");
                var relative = RelativeToAssetRoot(path, "textures") ?? Path.Combine(targetSubdirectory, Path.GetFileName(path));
                result.Add((path, NormalizeResource("textures/" + relative.Replace('\\', '/'))));
            }
        }
        return result;

        void Visit(DirectoryInfo directory, string prefix)
        {
            token.ThrowIfCancellationRequested();
            if (directory.LinkTarget != null) throw new InvalidOperationException("Texture folders cannot contain symbolic links or junctions.");
            foreach (var file in directory.EnumerateFiles())
            {
                token.ThrowIfCancellationRequested();
                if (!file.Extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)) continue;
                RequireSourceFile(file.FullName, ".dds");
                result.Add((file.FullName, NormalizeResource("textures/" + Path.Combine(prefix, file.Name).Replace('\\', '/'))));
            }
            foreach (var child in directory.EnumerateDirectories()) Visit(child, Path.Combine(prefix, child.Name));
        }
    }

    private static async Task<(string Folder, ProjectDocument Document)> ReadResourceProjectAsync(string projectFilePath, string gameDataPath, CancellationToken token, bool validateResourcePaths = true)
    {
        if (!Path.IsPathFullyQualified(projectFilePath)) throw new InvalidOperationException("Create or open a workspace mod before importing resources.");
        PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
        await using var stream = File.OpenRead(projectFilePath);
        var document = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, cancellationToken: token);
        if (document == null || document.Version != 1 || string.IsNullOrWhiteSpace(document.Name) || document.Resources == null)
            throw new InvalidOperationException("Invalid or unsupported weapon project.");
        ValidatePluginName(document.PluginFileName);
        var folder = Path.GetDirectoryName(projectFilePath)!;
        PluginWriteGuard.EnsureOutsideGame(Path.Combine(folder, document.PluginFileName), gameDataPath);
        if (!File.Exists(Path.Combine(folder, document.PluginFileName))) throw new FileNotFoundException("Project plugin is missing.");
        if (validateResourcePaths)
            foreach (var relative in document.Resources) ResourceTarget(folder, relative, gameDataPath);
        return (folder, document);
    }

    private async Task CopyResourcesAsync(string projectFilePath, string folder, ProjectDocument document,
        IReadOnlyList<(string Source, string Relative)> files, string gameDataPath, WorkspaceFileSnapshot original, CancellationToken token)
    {
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, relative) in files)
        {
            var target = ResourceTarget(folder, relative, gameDataPath);
            if (pending.TryGetValue(relative, out var previous) && !Path.GetFullPath(source).Equals(previous, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Multiple files target the same resource: " + relative);
            if (!Path.GetFullPath(source).Equals(target, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
                throw new IOException("A resource already exists; it was not overwritten: " + relative);
            pending[relative] = Path.GetFullPath(source);
        }
        var stage = Path.Combine(folder, ".assets-" + Guid.NewGuid().ToString("N"));
        PluginWriteGuard.EnsureOutsideGame(stage, gameDataPath);
        var created = new List<string>();
        var directories = new List<string>();
        Exception? failure = null;
        try
        {
            Directory.CreateDirectory(stage);
            var staged = new List<(string Temp, string Target)>();
            foreach (var (relative, source) in pending)
            {
                token.ThrowIfCancellationRequested();
                var target = ResourceTarget(folder, relative, gameDataPath);
                if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                var temp = Path.Combine(stage, staged.Count.ToString());
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, token);
                    await output.FlushAsync(token);
                    output.Flush(flushToDisk: true);
                }
                if (new FileInfo(temp).Length == 0) throw new IOException("An imported resource is empty: " + relative);
                staged.Add((temp, target));
            }
            foreach (var (temp, target) in staged)
            {
                token.ThrowIfCancellationRequested();
                PluginWriteGuard.EnsureOutsideGame(target, gameDataPath);
                var missing = new Stack<string>();
                for (var current = Path.GetDirectoryName(target); !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current)) missing.Push(current);
                while (missing.TryPop(out var directory)) { Directory.CreateDirectory(directory); directories.Add(directory); }
                File.Move(temp, target, overwrite: false);
                created.Add(target);
            }
            document.Resources = document.Resources.Concat(pending.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            token.ThrowIfCancellationRequested();
            PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
            PluginWriteGuard.EnsureOutsideGame(projectFilePath + ".bak", gameDataPath);
            original.EnsureUnchanged();
            var metadata = new AtomicJsonFile<ProjectDocument>(projectFilePath, () => new ProjectDocument(), JsonOptions, logger);
            await metadata.WriteUnderLeaseAsync(document, token, original.EnsureUnchanged);
        }
        catch (Exception ex)
        {
            failure = ex;
            foreach (var path in created)
                Cleanup(() => { PluginWriteGuard.EnsureOutsideGame(path, gameDataPath); File.Delete(path); }, path);
            foreach (var directory in directories.AsEnumerable().Reverse())
                Cleanup(() =>
                {
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        PluginWriteGuard.EnsureOutsideGame(directory, gameDataPath);
                        Directory.Delete(directory);
                    }
                }, directory);
            throw;
        }
        finally
        {
            // Delete only direct temporary files in the unique directory created by this operation.
            Cleanup(() =>
            {
                if (!Directory.Exists(stage)) return;
                PluginWriteGuard.EnsureOutsideGame(stage, gameDataPath);
                foreach (var file in Directory.EnumerateFiles(stage)) File.Delete(file);
                Directory.Delete(stage);
            }, stage);
        }

        void Cleanup(Action action, string path)
        {
            try { action(); }
            catch (Exception cleanupError)
            {
                logger?.LogWarning(cleanupError, "Resource operation cleanup failed for {Path}", path);
                if (failure != null) failure.Data["ResourceCleanupFailure:" + path] = cleanupError.Message;
                else if (logger == null) throw;
            }
        }
    }
}
