using System.Security.Cryptography;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<ResourceVersion>> BackupResourceAsync(string projectFilePath, string relativePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
            using var lease = await WorkspaceFileGate.AcquireAsync(projectFilePath, cancellationToken);
            WorkspaceWriteExpectation.Check(projectFilePath);
            var before = new WorkspaceFileSnapshot(projectFilePath);
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            var relative = NormalizeResource(relativePath);
            if (!document.Resources.Contains(relative, StringComparer.OrdinalIgnoreCase)) throw new IOException("Resource is not registered.");
            var target = ResourceTarget(folder, relative, gameDataPath);
            using var assetLease = await WorkspaceFileGate.AcquireAsync(target, cancellationToken);
            WorkspaceWriteExpectation.Check(target);
            var hash = await ResourceHashAsync(target, cancellationToken);
            var version = new ResourceVersion(Guid.NewGuid(), relative, hash, DateTime.UtcNow);
            var backup = VersionPath(folder, version.Id, gameDataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await CopyVersionFileAsync(target, backup, cancellationToken);
            if (await ResourceHashAsync(backup, cancellationToken) != hash || await ResourceHashAsync(target, cancellationToken) != hash)
                throw new IOException("Resource changed while backing up.");
            document.ResourceVersions.Add(version);
            var metadata = new AtomicJsonFile<ProjectDocument>(projectFilePath, () => new(), JsonOptions, logger);
            await metadata.WriteUnderLeaseAsync(document, cancellationToken, before.EnsureUnchanged);
            return version;
        }, cancellationToken);

    public Task<Result<string>> ImportModelVersionCopyAsync(string projectFilePath, Guid versionId, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
            using var lease = await WorkspaceFileGate.AcquireAsync(projectFilePath, cancellationToken);
            WorkspaceWriteExpectation.Check(projectFilePath);
            var before = new WorkspaceFileSnapshot(projectFilePath);
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            var version = document.ResourceVersions.SingleOrDefault(v => v.Id == versionId) ?? throw new IOException("Unknown resource version.");
            var original = NormalizeResource(version.RelativePath);
            if (!original.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) throw new IOException("Select a NIF version for a sword model.");
            var backup = VersionPath(folder, version.Id, gameDataPath);
            using var held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (await ResourceHashAsync(backup, cancellationToken) != version.Sha256) throw new IOException("Resource backup is missing or changed.");
            var relative = NormalizeResource("meshes/" + document.Name + "/Versions/" + Guid.NewGuid().ToString("N") + "/" + Path.GetFileName(original));
            await CopyResourcesAsync(projectFilePath, folder, document, [(backup, relative)], gameDataPath, before, cancellationToken);
            return relative[7..].Replace('/', '\\');
        }, cancellationToken);

    public Task<Result<IReadOnlyList<ResourceVersion>>> GetResourceVersionsAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<ResourceVersion>>(async () =>
        {
            var (_, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            return document.ResourceVersions.OrderByDescending(v => v.CreatedUtc).ToArray();
        }, cancellationToken);

    public Task<Result<string>> ReplaceResourceAsync(string projectFilePath, string relativePath, string sourcePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        ChangeResourceAsync(projectFilePath, relativePath, sourcePath, null, false, gameDataPath, cancellationToken);

    public Task<Result<string>> RestoreResourceVersionAsync(string projectFilePath, Guid versionId, string gameDataPath, CancellationToken cancellationToken = default) =>
        ChangeResourceAsync(projectFilePath, null, null, versionId, false, gameDataPath, cancellationToken);

    public Task<Result<string>> DeleteResourceAsync(string projectFilePath, string relativePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        ChangeResourceAsync(projectFilePath, relativePath, null, null, true, gameDataPath, cancellationToken);

    private Task<Result<string>> ChangeResourceAsync(string projectFilePath, string? relativePath, string? sourcePath, Guid? versionId,
        bool delete, string gameDataPath, CancellationToken token) => RunAsync(async () =>
    {
        PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
        // Same lock order as record writes and workspace history.
        var initial = await ReadResourceProjectAsync(projectFilePath, gameDataPath, token);
        var pluginPath = Path.Combine(initial.Folder, initial.Document.PluginFileName);
        using var pluginLease = await WorkspaceFileGate.AcquireAsync(pluginPath, token);
        using var metadataLease = await WorkspaceFileGate.AcquireAsync(projectFilePath, token);
        WorkspaceWriteExpectation.Check(pluginPath); WorkspaceWriteExpectation.Check(projectFilePath);
        var metadataBefore = new WorkspaceFileSnapshot(projectFilePath);
        var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, token);
        if (!Path.Combine(folder, document.PluginFileName).Equals(pluginPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Project plugin changed during the operation.");
        if (document.ResourceVersions == null) throw new IOException("Invalid resource version metadata.");
        if (versionId.HasValue)
        {
            var version = document.ResourceVersions.SingleOrDefault(v => v.Id == versionId.Value)
                ?? throw new IOException("Resource version is not registered in this project.");
            relativePath = version.RelativePath;
            sourcePath = VersionPath(folder, version.Id, gameDataPath);
            if (await ResourceHashAsync(sourcePath, token) != version.Sha256) throw new IOException("Resource backup is missing or changed.");
        }
        var relative = NormalizeResource(relativePath!);
        var target = ResourceTarget(folder, relative, gameDataPath);
        using var assetLease = await WorkspaceFileGate.AcquireAsync(target, token);
        WorkspaceWriteExpectation.Check(target);
        var exists = File.Exists(target);
        if (!versionId.HasValue && !document.Resources.Contains(relative, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Only registered project resources may be changed.");
        if (!exists && !versionId.HasValue) throw new IOException("Resource is missing.");
        var catalog = BuildResourceCatalog(folder, document, gameDataPath, token, inspectVanilla: false);
        if (catalog.Diagnostics.Count > 0) throw new IOException("Resolve record diagnostics before changing resources.");
        using (var plugin = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE))
            if (plugin.EnumerateMajorRecords().Any(r => r is not IWeaponGetter and not IStaticGetter and not IConstructibleObjectGetter and not IKeywordGetter and not IFormListGetter))
                throw new IOException("This plugin contains record types whose resource dependencies are not analyzed. In-place resource changes are blocked.");
        var entry = catalog.Entries.SingleOrDefault(e => e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (relative.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && entry?.Uses.Count > 0)
            throw new IOException("This NIF is used by saved records. Import a separate model copy for the selected sword instead.");
        // NIF texture paths are unknown. An in-place DDS replacement could affect any item.
        if (relative.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
            (catalog.Entries.Any(e => e.RelativePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) ||
             Directory.EnumerateFiles(folder, "*.nif", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Any()))
            throw new IOException("DDS usage inside NIF is unknown. In-place replacement or deletion is blocked while models are present.");
        if (entry?.Issues.Any(i => i != ResourceIssue.MissingOrEmpty && i != ResourceIssue.UnregisteredCustom) == true)
            throw new IOException("Resolve resource issues before changing this file.");
        if (!delete && !versionId.HasValue) RequireSourceFile(sourcePath!, Path.GetExtension(relative));
        var originalHash = exists ? await ResourceHashAsync(target, token) : null;
        ResourceVersion? saved = null;
        if (exists)
        {
            saved = new(Guid.NewGuid(), relative, originalHash!, DateTime.UtcNow);
            var backup = VersionPath(folder, saved.Id, gameDataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await CopyVersionFileAsync(target, backup, token);
            if (await ResourceHashAsync(backup, token) != originalHash) throw new IOException("Resource changed while backing up.");
        }
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".resource.tmp";
        var published = false;
        string? publishedHash = null;
        try
        {
            if (!delete)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyVersionFileAsync(sourcePath!, temp, token);
                if (new FileInfo(temp).Length == 0) throw new IOException("Replacement resource is empty.");
                publishedHash = await ResourceHashAsync(temp, token);
                if (versionId.HasValue && await ResourceHashAsync(temp, token) != document.ResourceVersions.Single(v => v.Id == versionId).Sha256)
                    throw new IOException("Resource backup changed while reading.");
            }
            token.ThrowIfCancellationRequested(); metadataBefore.EnsureUnchanged();
            WorkspaceWriteExpectation.Check(target);
            if ((File.Exists(target) ? await ResourceHashAsync(target, token) : null) != originalHash)
                throw new IOException("Resource changed outside the editor.");
            PluginWriteGuard.EnsureOutsideGame(target, gameDataPath);
            if (delete) File.Delete(target);
            else if (exists) File.Replace(temp, target, null);
            else File.Move(temp, target);
            published = true;
            WorkspaceWriteExpectation.Written(target);
            if (saved != null) document.ResourceVersions.Add(saved);
            if (delete) document.Resources.RemoveAll(p => p.Equals(relative, StringComparison.OrdinalIgnoreCase));
            else if (!document.Resources.Contains(relative, StringComparer.OrdinalIgnoreCase)) document.Resources.Add(relative);
            var metadata = new AtomicJsonFile<ProjectDocument>(projectFilePath, () => new(), JsonOptions, logger);
            await metadata.WriteUnderLeaseAsync(document, token, metadataBefore.EnsureUnchanged);
            return relative;
        }
        catch (Exception original)
        {
            if (published)
            {
                try
                {
                    PluginWriteGuard.EnsureOutsideGame(target, gameDataPath);
                    if ((File.Exists(target) ? await ResourceHashAsync(target, CancellationToken.None) : null) != publishedHash)
                        throw new IOException("Resource changed externally after publication; rollback refused to overwrite it.");
                    if (saved != null)
                    {
                        var backup = VersionPath(folder, saved.Id, gameDataPath);
                        if (await ResourceHashAsync(backup, CancellationToken.None) != saved.Sha256) throw new IOException("Rollback backup changed.");
                        File.Copy(backup, target, true);
                    }
                    else if (File.Exists(target)) File.Delete(target);
                    WorkspaceWriteExpectation.Written(target);
                }
                catch (Exception rollback) { original.Data["ResourceRollback"] = rollback.Message; }
            }
            throw;
        }
        finally
        {
            PluginWriteGuard.EnsureOutsideGame(temp, gameDataPath);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }, token);

    private static string VersionPath(string folder, Guid id, string gameDataPath)
    {
        var path = PackingPathGuard.ResolveUnderRoot(folder, ".resource-history/" + id.ToString("N") + ".bin");
        PluginWriteGuard.EnsureOutsideGame(path, gameDataPath);
        return path;
    }
    private static async Task<string> ResourceHashAsync(string path, CancellationToken token)
    {
        PackingPathGuard.EnsureNoLinks(path);
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private static async Task CopyVersionFileAsync(string source, string destination, CancellationToken token)
    {
        PackingPathGuard.EnsureNoLinks(source); PackingPathGuard.EnsureNoLinks(destination);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true);
    }
}
