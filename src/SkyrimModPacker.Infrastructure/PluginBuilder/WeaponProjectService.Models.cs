using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<uint?>> GetTemplateOriginAsync(string projectFilePath, string gameDataPath, uint weaponFormId, CancellationToken cancellationToken = default) =>
        RunAsync<uint?>(async () =>
        {
            var (_, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            return document.VanillaTemplates != null && document.VanillaTemplates.TryGetValue(weaponFormId, out var template) ? template : null;
        }, cancellationToken);

    // Metadata is published first, under its lease while the caller holds the plugin lease.
    // A failed plugin commit restores the prior metadata; no successful plugin lacks its origin.
    internal static void WriteWithTemplateOrigin(string? projectPath, string pluginPath, string gameDataPath,
        uint weaponId, uint templateId, Action writePlugin, CancellationToken token, string templatePlugin = "Skyrim.esm", WeaponVariant? variant = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) { writePlugin(); return; }
        PluginWriteGuard.EnsureOutsideGame(projectPath, gameDataPath);
        using var lease = WorkspaceFileGate.AcquireAsync(projectPath, token).GetAwaiter().GetResult();
        WorkspaceWriteExpectation.Check(projectPath);
        var snapshot = new WorkspaceFileSnapshot(projectPath);
        var original = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(projectPath))
            ?? throw new InvalidOperationException("Invalid project metadata.");
        ValidatePluginName(original.PluginFileName);
        var origin = original.VanillaTemplateReferences?.GetValueOrDefault(weaponId) ??
            (original.VanillaTemplates?.TryGetValue(weaponId, out var existingId) == true ? new PluginRecordReference("Skyrim.esm", existingId) : new PluginRecordReference(templatePlugin, templateId));
        var baseline = ReadOriginalModels(gameDataPath, origin.FormId, origin.PluginName);
        if (original.Version != 1 || !Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, original.PluginFileName))
                .Equals(Path.GetFullPath(pluginPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Template metadata must belong to the edited plugin.");
        var updated = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(original))!;
        updated.VanillaTemplates ??= [];
        // Once recorded, the original template must survive later template-list selections.
        updated.VanillaTemplates.TryAdd(weaponId, templateId);
        updated.VanillaTemplateReferences ??= [];
        updated.VanillaTemplateReferences.TryAdd(weaponId, origin);
        if (variant != null)
        {
            updated.Variants ??= [];
            if (updated.Variants.Any(v => v == null || v.FormId == variant.FormId)) throw new InvalidOperationException("Variant metadata already occupies the new record identity or is invalid.");
            updated.Variants.Add(variant);
        }
        updated.VanillaModels ??= [];
        if (updated.VanillaTemplates[weaponId] == templateId) updated.VanillaModels.TryAdd(weaponId, baseline);
        var json = new AtomicJsonFile<ProjectDocument>(projectPath, () => new ProjectDocument(), JsonOptions);
        json.WriteUnderLeaseAsync(updated, token, snapshot.EnsureUnchanged).GetAwaiter().GetResult();
        var published = new WorkspaceFileSnapshot(projectPath);
        try { writePlugin(); }
        catch (Exception ex)
        {
            try { json.WriteUnderLeaseAsync(original, CancellationToken.None, published.EnsureUnchanged).GetAwaiter().GetResult(); }
            catch (Exception rollback) { ex.Data["TemplateMetadataRollbackFailure"] = rollback.Message; }
            throw;
        }
    }

    public Task<Result<WeaponModels?>> GetOriginalModelsAsync(string projectFilePath, string gameDataPath, uint weaponFormId, CancellationToken cancellationToken = default) =>
        RunAsync<WeaponModels?>(async () =>
        {
            var (_, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            if (document.VanillaTemplates == null || !document.VanillaTemplates.TryGetValue(weaponFormId, out var templateId)) return null;
            var source = document.VanillaTemplateReferences?.GetValueOrDefault(weaponFormId)?.PluginName ?? "Skyrim.esm";
            var current = ReadOriginalModels(gameDataPath, templateId, source);
            if (document.VanillaModels != null && document.VanillaModels.TryGetValue(weaponFormId, out var original))
            {
                if (original != current) throw new InvalidOperationException("The vanilla template models changed externally. Restore the original master before restoring these models.");
                return original;
            }
            return current;
        }, cancellationToken);

    private static WeaponModels ReadOriginalModels(string gameDataPath, uint templateId, string source = "Skyrim.esm")
    {
        if (!WeaponTypes.IsVanillaSource(source)) throw new InvalidOperationException("Unsupported vanilla template source.");
        using var master = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(gameDataPath, source), SkyrimRelease.SkyrimSE);
        var weapon = master.Weapons.FirstOrDefault(w => w.FormKey.ModKey == master.ModKey && w.FormKey.ID == templateId && !w.IsDeleted && WeaponTypes.Supported(w.Data?.AnimationType))
            ?? throw new InvalidOperationException("Template origin must be a vanilla one-handed sword.");
        var key = weapon.FirstPersonModel.FormKey;
        if (key == Mutagen.Bethesda.Plugins.FormKey.Null) return new(weapon.Model?.File.GivenPath ?? "", null, null);
        var first = master.Statics.FirstOrDefault(s => s.FormKey == key && !s.IsDeleted);
        if (first != null) return new(weapon.Model?.File.GivenPath ?? "", new(key.ModKey.FileName.String, key.ID), first.Model?.File.GivenPath);
        if (!WeaponTypes.IsVanillaSource(key.ModKey.FileName.String)) throw new InvalidOperationException("Unsupported first-person template source.");
        using var dependency = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(gameDataPath, key.ModKey.FileName.String), SkyrimRelease.SkyrimSE);
        first = dependency.Statics.FirstOrDefault(s => s.FormKey == key && !s.IsDeleted) ?? throw new InvalidOperationException("The vanilla first-person STAT is missing.");
        return new(weapon.Model?.File.GivenPath ?? "", new(key.ModKey.FileName.String, key.ID), first.Model?.File.GivenPath);
    }

    public Task<Result<ModelResourceInfo>> InspectModelAsync(string projectFilePath, string modelPath, string gameDataPath,
        bool vanilla, CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            var relative = modelPath.Replace('\\', '/');
            if (relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) relative = relative[7..];
            var resource = NormalizeResource("meshes/" + relative);
            if (!string.IsNullOrWhiteSpace(projectFilePath))
            {
                var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
                var target = ResourceTarget(folder, resource, gameDataPath);
                var registered = document.Resources.Contains(resource, StringComparer.OrdinalIgnoreCase);
                if (registered || File.Exists(target))
                    return new ModelResourceInfo(resource, ModelOrigin.Project,
                        File.Exists(target) && new FileInfo(target).Length > 0 ? ModelAvailability.Found : ModelAvailability.Missing,
                        registered, registered ? null : "Project model is not registered for ZIP.");
            }
            if (!vanilla) return new ModelResourceInfo(resource, ModelOrigin.Unknown, ModelAvailability.Missing, false,
                "Import this custom model into the project.");
            return await Task.Run(() => InspectVanilla(resource, gameDataPath, cancellationToken), cancellationToken);
        }, cancellationToken);

    internal static ModelResourceInfo InspectVanilla(string resource, string gameDataPath, CancellationToken token)
    {
        var errors = new List<string>();
        try
        {
            var loose = PackingPathGuard.ResolveUnderRoot(gameDataPath, resource.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose) && new FileInfo(loose).Length > 0)
                return new(resource, ModelOrigin.Vanilla, ModelAvailability.Found, false, "Data loose file");
            foreach (var path in Directory.EnumerateFiles(gameDataPath, "*.bsa").Where(p => Path.GetFileName(p).StartsWith("Skyrim", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("Dawnguard", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    PackingPathGuard.EnsureNoLinks(path);
                    var archive = Archive.CreateReader(GameRelease.SkyrimSE, path);
                    try
                    {
                        var folder = Path.GetDirectoryName(resource.Replace('/', '\\'))!;
                        if (archive.TryGetFolder(folder, out var found))
                        {
                            var file = found.Files.FirstOrDefault(f => Path.GetFileName(f.Path.Replace('/', '\\'))
                                .Equals(Path.GetFileName(resource), StringComparison.OrdinalIgnoreCase));
                            if (file != null)
                            {
                                using var stream = file.AsStream();
                                if (stream.ReadByte() < 0) throw new IOException("The archived model is empty.");
                                return new(resource, ModelOrigin.Vanilla, ModelAvailability.Found, false, Path.GetFileName(path));
                            }
                        }
                    }
                    finally { (archive as IDisposable)?.Dispose(); }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); }
        return new(resource, ModelOrigin.Vanilla, errors.Count == 0 ? ModelAvailability.Missing : ModelAvailability.CheckFailed,
            false, errors.Count == 0 ? null : string.Join("; ", errors));
    }
}
