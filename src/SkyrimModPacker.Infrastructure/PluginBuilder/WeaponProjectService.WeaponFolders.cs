using System.Text.Json;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<WeaponFolderScan>> ScanWeaponFolderAsync(string sourceFolder, CancellationToken cancellationToken = default) =>
        RunAsync(() => Task.Run(() => ScanWeaponFolder(sourceFolder, cancellationToken), cancellationToken), cancellationToken);
    private static WeaponFolderScan ScanWeaponFolder(string source, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source)) throw new IOException("Select an existing resource folder.");
        var files = new List<WeaponFolderSource>(); var skipped = 0;
        Visit(new DirectoryInfo(source), 0);
        return new(Path.GetFullPath(source), files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), skipped);
        void Visit(DirectoryInfo directory, int depth)
        {
            token.ThrowIfCancellationRequested(); if (depth > 64) throw new IOException("Resource folder nesting exceeds 64 levels."); PackingPathGuard.EnsureNoLinks(directory.FullName);
            foreach (var file in directory.EnumerateFiles())
            {
                token.ThrowIfCancellationRequested(); PackingPathGuard.EnsureNoLinks(file.FullName);
                var extension = file.Extension.ToLowerInvariant(); if (extension is not (".nif" or ".dds")) { skipped++; continue; }
                if (files.Count >= 10000) throw new IOException("Resource import exceeds 10000 files. Select a smaller folder.");
                string format = "?"; string? error = null;
                try
                {
                    RequireSourceFile(file.FullName, extension);
                    format = extension == ".dds" ? DdsResourceInspector.Inspect(file.FullName, token).Format : NifCompatibilityInspector.Read(file.FullName).Family.ToString();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
                files.Add(new(file.FullName, Path.GetRelativePath(source, file.FullName).Replace('\\', '/'), file.Length, format, error));
            }
            foreach (var child in directory.EnumerateDirectories()) Visit(child, depth + 1);
        }
    }
    public Task<Result<WeaponFolderPlan>> PreviewWeaponFolderAsync(WeaponFolderRequest request, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            using var lease = request.NewProjectPluginName != null ? null : await WorkspaceFileGate.AcquireAsync(request.ProjectPath, cancellationToken);
            return await Task.Run(() => BuildWeaponFolderPlanAsync(request, gameDataPath, cancellationToken), cancellationToken);
        }, cancellationToken);
    private async Task<WeaponFolderPlan> BuildWeaponFolderPlanAsync(WeaponFolderRequest request, string game, CancellationToken token)
    {
        if (request.Id == Guid.Empty) throw new InvalidDataException("Invalid folder plan identity.");
        string folder; ProjectDocument document;
        var newProject = request.NewProjectPluginName != null;
        if (newProject)
        {
            ValidatePluginName(request.NewProjectPluginName!);
            PluginWriteGuard.EnsureOutsideGame(request.ProjectPath, game);
            folder = Path.GetDirectoryName(Path.GetFullPath(request.ProjectPath))!;
            var name = Path.GetFileNameWithoutExtension(request.NewProjectPluginName);
            if (Path.GetFileName(request.ProjectPath) != ProjectFileName || !Path.GetFileName(folder).Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Некоректна папка призначення проєкту / Invalid project destination: " + folder);
            if (Directory.Exists(folder) || File.Exists(folder))
                throw new IOException("Ця папка вже існує. Зміни назву моду або папку збереження у цьому вікні / Destination already exists: " + folder);
            document = new ProjectDocument { Name = name, PluginFileName = request.NewProjectPluginName! };
        }
        else (folder, document) = await ReadResourceProjectAsync(request.ProjectPath, game, token);
        var inventory = ScanWeaponFolder(request.SourceFolder, token);
        var sources = inventory.Files.ToDictionary(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in request.TextureMappings) if (!sources.ContainsKey(Path.GetFullPath(mapping.SourcePath)) || !mapping.SourcePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Texture mapping source is outside this folder.");
        if (Path.IsPathRooted(request.TextureRoot)) throw new InvalidDataException("Texture destination must be relative.");
        if (request.TextureRoot.Length > 0) ValidateResourcePath("textures/" + request.TextureRoot.Replace('\\', '/') + "/check.dds");
        WeaponFolderSource Model(string path)
        {
            if (!sources.TryGetValue(Path.GetFullPath(path), out var file) || !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a NIF from the scanned folder.");
            if (file.Error != null) throw new IOException(file.SourcePath + ": " + file.Error); return file;
        }
        if (request.Items == null || request.Items.Count > 100 || request.Items.Any(i => i == null || i.Id == Guid.Empty || string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 100 || i.Name.Any(char.IsControl) || !Enum.IsDefined(i.Kind) ||
            !i.ShareModel && string.IsNullOrWhiteSpace(i.FirstPersonSource)) || request.Items.Select(i => i.Id).Distinct().Count() != request.Items.Count)
            throw new InvalidDataException("Перевір назви, типи та моделі всіх предметів (до 100) / Invalid import item queue.");
        var assignments = (request.Items.Count == 0
            ? new[] { (Item: (WeaponFolderItemRequest?)null, Third: Model(request.ThirdPersonSource), FirstSource: request.FirstPersonSource, Share: request.ShareModel) }
            : request.Items.Select(i => (Item: (WeaponFolderItemRequest?)i, Third: Model(i.ThirdPersonSource), FirstSource: i.FirstPersonSource, Share: i.ShareModel)).ToArray())
            .Select(a => (a.Item, a.Third, First: a.Share ? a.Third : string.IsNullOrWhiteSpace(a.FirstSource) ? null : Model(a.FirstSource))).ToArray();
        var third = assignments[0].Third; var first = assignments[0].First;
        var selectedModels = assignments.SelectMany(a => new[] { a.Third, a.First }).Where(f => f != null).Cast<WeaponFolderSource>().DistinctBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var expectedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var warnings = new List<string>(); var incomplete = false;
        foreach (var model in selectedModels)
        {
            try
            {
                if (model.Bytes > SseNifReader.MaxFileBytes) throw new InvalidDataException("NIF exceeds the preview inspection limit.");
                using var input = File.OpenRead(model.SourcePath);
                if (input.Length > SseNifReader.MaxFileBytes) throw new InvalidDataException("NIF exceeds the preview inspection limit.");
                var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token);
                var scene = SseNifReader.Read(bytes, token);
                incomplete |= !scene.TextureDependenciesComplete;
                if (!scene.TextureDependenciesComplete) warnings.Add(model.RelativePath + ": texture dependency completeness is unknown.");
                foreach (var texture in scene.TexturePaths)
                { var path = texture.Replace('\\', '/'); if (!path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) path = "textures/" + path; expectedTextures.Add(NormalizeResource(path)); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { incomplete = true; warnings.Add(model.RelativePath + ": " + ex.Message); }
        }
        var vanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in WeaponTypes.VanillaSources.Where(n => File.Exists(Path.Combine(game, n))))
        {
            using var master = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(Path.Combine(game, name), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
            foreach (var path in master.Weapons.Select(w => w.Model?.File.GivenPath).Concat(master.Statics.Select(s => s.Model?.File.GivenPath)).Where(p => !string.IsNullOrEmpty(p)))
                vanilla.Add("meshes/" + path!.Replace('\\', '/'));
        }
        string MeshDestination(WeaponFolderSource source)
        {
            var relative = RelativeToAssetRoot(source.SourcePath, "meshes"); var path = relative == null ? "" : NormalizeResource("meshes/" + relative.Replace('\\', '/'));
            return path.Length > 0 && !vanilla.Contains(path) ? path : NormalizeResource($"meshes/{document.Name}/Folder/{request.Id:N}/" + source.RelativePath);
        }
        var thirdPath = MeshDestination(third); var firstPath = first == null ? null : MeshDestination(first);
        var pending = selectedModels.Select(f => (File: f, Target: MeshDestination(f))).ToList();
        var textures = inventory.Files.Where(f => f.SourcePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).Select(f =>
        {
            var path = "textures/" +
                (RelativeToAssetRoot(f.SourcePath, "textures") ?? Path.Combine(request.TextureRoot, f.RelativePath)).Replace('\\', '/');
            path = NormalizeResource(path); if (!path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Texture mapping must target textures/.");
            return (File: f, Target: path);
        }).Concat(request.TextureMappings.Select(m => (File: sources[Path.GetFullPath(m.SourcePath)], Target: NormalizeResource(m.RelativePath))))
            .Select(t => t.Target.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ? t : throw new InvalidDataException("Texture mapping must target textures/.")).ToArray();
        var dependencies = new List<WeaponFolderDependency>();
        foreach (var expected in expectedTextures.Order(StringComparer.OrdinalIgnoreCase))
        {
            var matching = textures.Where(t => t.Target.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length > 0) { pending.AddRange(matching); dependencies.Add(new(expected, "Import", matching.Select(m => m.File.SourcePath).ToArray())); }
            else
            {
                var found = ModelPreviewService.ProbeTexture(folder, expected, game, token);
                var candidates = textures.Where(t => Path.GetFileName(t.Target).Equals(Path.GetFileName(expected), StringComparison.OrdinalIgnoreCase)).Select(t => t.File.SourcePath).ToArray();
                dependencies.Add(new(expected, found.Available == true ? "Existing" : candidates.Length > 0 ? "MappingRequired" : found.Available == false ? "Missing" : "Unknown", candidates));
                if (found.Available != true) incomplete = true;
            }
        }
        if (request.IncludeUnusedTextures) pending.AddRange(textures);
        var files = new List<WeaponFolderFile>();
        foreach (var entry in pending.DistinctBy(p => (p.File.SourcePath, p.Target)))
        {
            token.ThrowIfCancellationRequested(); if (entry.File.Error != null) throw new InvalidDataException(entry.File.RelativePath + ": " + entry.File.Error);
            var target = ResourceTarget(folder, entry.Target, game); var hash = await BatchHashAsync(entry.File.SourcePath, token);
            var identical = File.Exists(target) && await BatchHashAsync(target, token) == hash;
            files.Add(new(entry.File.SourcePath, entry.Target, hash, entry.File.Bytes, identical,
                (File.Exists(target) && !identical) || Directory.Exists(target) ? "Different resource already exists. Replace explicitly with backup." : null));
        }
        var canonical = files.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Select(g => g.Select(f => f.Sha256).Distinct().Count() > 1 ? g.First() with { Conflict = "Multiple different sources target this path." } : g.First()).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var skipped = inventory.SkippedFiles + inventory.Files.Count(f => !pending.Any(p => p.File.SourcePath.Equals(f.SourcePath, StringComparison.OrdinalIgnoreCase)));
        return new(request, newProject ? "NEW" : await BatchHashAsync(request.ProjectPath, token), canonical, thirdPath[7..].Replace('/', '\\'), firstPath?[7..].Replace('/', '\\'), dependencies, warnings, skipped, incomplete)
        { Items = assignments.Where(a => a.Item != null).Select(a => new WeaponFolderItemPlan(a.Item!.Id, a.Item.Name, a.Item.Kind, MeshDestination(a.Third)[7..].Replace('/', '\\'), a.First == null ? null : MeshDestination(a.First)[7..].Replace('/', '\\'))).ToArray() };
    }
    public Task<Result<WeaponFolderImport>> ImportWeaponFolderAsync(WeaponFolderPlan plan, string gameDataPath, Func<WeaponFolderImport, CancellationToken, Task>? saveDraft = null, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var project = plan.Request.ProjectPath; using var lease = await WorkspaceFileGate.AcquireAsync(project, cancellationToken);
            if (plan.Request.NewProjectPluginName != null) throw new IOException("Create the reviewed project before committing its resource plan.");
            WorkspaceWriteExpectation.Check(project); var before = new WorkspaceFileSnapshot(project);
            var current = await Task.Run(() => BuildWeaponFolderPlanAsync(plan.Request, gameDataPath, cancellationToken), cancellationToken);
            if (!current.CanImport || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(plan)) throw new IOException("The folder or project changed after preview, or conflicts are unresolved. Preview again.");
            var (folder, document) = await ReadResourceProjectAsync(project, gameDataPath, cancellationToken);
            var originalBytes = await File.ReadAllBytesAsync(project, cancellationToken);
            var backupPath = project + ".bak";
            var originalBackup = File.Exists(backupPath) ? await File.ReadAllBytesAsync(backupPath, cancellationToken) : null;
            var originalPaths = current.Files.Where(f => File.Exists(ResourceTarget(folder, f.RelativePath, gameDataPath))).Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var originalDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folder };
            foreach (var file in current.Files)
                for (var directory = Path.GetDirectoryName(ResourceTarget(folder, file.RelativePath, gameDataPath)); directory != null && directory.StartsWith(folder, StringComparison.OrdinalIgnoreCase); directory = Path.GetDirectoryName(directory))
                    if (Directory.Exists(directory)) originalDirectories.Add(directory);
            await CopyResourcesAsync(project, folder, document, current.Files.Select(f => (f.Identical ? ResourceTarget(folder, f.RelativePath, gameDataPath) : f.SourcePath, f.RelativePath)).ToArray(), gameDataPath, before, cancellationToken,
                current.Files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.OrdinalIgnoreCase));
            var committed = new WorkspaceFileSnapshot(project);
            var committedBackup = new WorkspaceFileSnapshot(backupPath);
            var result = new WeaponFolderImport(plan.Request.Id, current.ThirdPersonPath, current.FirstPersonPath, current.Incomplete) { Items = current.Items };
            try { if (saveDraft != null) await saveDraft(result, cancellationToken); }
            catch (Exception failure)
            {
                try
                {
                    committed.EnsureUnchanged();
                    foreach (var file in current.Files.Where(f => !originalPaths.Contains(f.RelativePath)))
                        if (await BatchHashAsync(ResourceTarget(folder, file.RelativePath, gameDataPath), CancellationToken.None) != file.Sha256) throw new IOException("An imported file was changed externally; automatic rollback stopped.");
                    committedBackup.EnsureUnchanged();
                    async Task RestoreBytes(string target, byte[] bytes, Action unchanged)
                    {
                        PluginWriteGuard.EnsureOutsideGame(target, gameDataPath);
                        var temporary = target + ".folder-rollback-" + Guid.NewGuid().ToString("N");
                        try { await File.WriteAllBytesAsync(temporary, bytes, CancellationToken.None); unchanged(); if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target); WorkspaceWriteExpectation.Written(target); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    await RestoreBytes(project, originalBytes, committed.EnsureUnchanged);
                    if (originalBackup == null) { committedBackup.EnsureUnchanged(); if (File.Exists(backupPath)) File.Delete(backupPath); }
                    else await RestoreBytes(backupPath, originalBackup, committedBackup.EnsureUnchanged);
                    foreach (var file in current.Files.Where(f => !originalPaths.Contains(f.RelativePath))) File.Delete(ResourceTarget(folder, file.RelativePath, gameDataPath));
                    foreach (var file in current.Files.Where(f => !originalPaths.Contains(f.RelativePath)))
                    {
                        for (var directory = Path.GetDirectoryName(ResourceTarget(folder, file.RelativePath, gameDataPath)); directory != null && !originalDirectories.Contains(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any(); directory = Path.GetDirectoryName(directory)) Directory.Delete(directory);
                    }
                }
                catch (Exception rollback) { failure.Data["FolderRollbackFailure"] = rollback.Message; }
                throw;
            }
            return result;
        }, cancellationToken);
    public Task<Result<IReadOnlyList<string>>> ValidateWeaponFolderAssetsAsync(string projectFilePath, IReadOnlyList<string> models, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<string>>(() => Task.Run<IReadOnlyList<string>>(() =>
        {
            var issues = new List<string>();
            foreach (var model in models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var relative = model.Replace('\\', '/'); if (!relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) relative = "meshes/" + relative;
                    if (!File.Exists(ResourceTarget(Path.GetDirectoryName(projectFilePath)!, relative, gameDataPath))) continue;
                    var scene = ModelPreviewService.Inspect(Path.GetDirectoryName(projectFilePath)!, model, gameDataPath, cancellationToken);
                    if (!scene.TextureDependenciesComplete) issues.Add(model + ": texture references are not completely verified.");
                    issues.AddRange(scene.TextureDependencies.Where(d => d.Available != true).Select(d => d.Path + ": " + d.Detail));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { issues.Add(model + ": " + ex.Message); }
            }
            return issues;
        }, cancellationToken), cancellationToken);
}
