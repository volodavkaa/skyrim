using System.Text.Json;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<PluginRecordReference?>> GetTemplateReferenceAsync(string projectFilePath, string gameDataPath, uint weaponFormId, CancellationToken cancellationToken = default) =>
        RunAsync<PluginRecordReference?>(async () =>
        {
            var (_, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            return document.VanillaTemplateReferences?.GetValueOrDefault(weaponFormId) ??
                (document.VanillaTemplates?.TryGetValue(weaponFormId, out var id) == true ? new("Skyrim.esm", id) : null);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<WeaponVariant>>> GetVariantsAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<WeaponVariant>>(async () =>
        {
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            if (document.Variants == null) throw new IOException("Invalid variant metadata.");
            using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(folder, document.PluginFileName), SkyrimRelease.SkyrimSE);
            var ids = plugin.Weapons.Where(w => !w.IsDeleted && w.FormKey.ModKey == plugin.ModKey).Select(w => w.FormKey.ID).ToHashSet();
            if (document.Variants.Any(v => v == null || !ids.Contains(v.FormId) || !ids.Contains(v.ParentFormId) || v.FormId == v.ParentFormId) ||
                document.Variants.Select(v => v.FormId).Distinct().Count() != document.Variants.Count) throw new IOException("Variant links refer to missing, duplicate or identical records.");
            foreach (var variant in document.Variants)
            {
                var visited = new HashSet<uint> { variant.FormId }; var parent = variant.ParentFormId;
                while (true)
                {
                    if (!visited.Add(parent)) throw new IOException("Cyclic variant metadata.");
                    var next = document.Variants.FirstOrDefault(v => v.FormId == parent);
                    if (next == null) break; parent = next.ParentFormId;
                }
            }
            return document.Variants.ToArray();
        }, cancellationToken);

    internal static void WriteWithVariantDeletion(string? projectPath, string pluginPath, string gameDataPath, uint formId, Action writePlugin, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) { writePlugin(); return; }
        PluginWriteGuard.EnsureOutsideGame(projectPath, gameDataPath);
        using var lease = WorkspaceFileGate.AcquireAsync(projectPath, token).GetAwaiter().GetResult();
        WorkspaceWriteExpectation.Check(projectPath);
        var before = new WorkspaceFileSnapshot(projectPath);
        var original = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(projectPath)) ?? throw new IOException("Invalid project metadata.");
        ValidatePluginName(original.PluginFileName);
        if (original.Version != 1 || !Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, original.PluginFileName)).Equals(Path.GetFullPath(pluginPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Metadata belongs to a different plugin.");
        var updated = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(original))!;
        updated.Variants.RemoveAll(v => v.FormId == formId || v.ParentFormId == formId);
        updated.VanillaTemplates?.Remove(formId); updated.VanillaTemplateReferences?.Remove(formId); updated.VanillaModels?.Remove(formId);
        var json = new AtomicJsonFile<ProjectDocument>(projectPath, () => new(), JsonOptions);
        json.WriteUnderLeaseAsync(updated, token, before.EnsureUnchanged).GetAwaiter().GetResult();
        var published = new WorkspaceFileSnapshot(projectPath);
        try { writePlugin(); }
        catch (Exception originalError)
        {
            try { json.WriteUnderLeaseAsync(original, CancellationToken.None, published.EnsureUnchanged).GetAwaiter().GetResult(); }
            catch (Exception rollback) { originalError.Data["VariantMetadataRollback"] = rollback.Message; }
            throw;
        }
    }
}
