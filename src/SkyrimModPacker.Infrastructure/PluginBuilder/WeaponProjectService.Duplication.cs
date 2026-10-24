using System.Text.Json;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    internal static void WriteWithWeaponDuplication(string? projectPath, string pluginPath, string gameDataPath,
        uint sourceId, uint copyId, Action writePlugin, CancellationToken token)
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
        if (original.VanillaTemplates?.TryGetValue(sourceId, out var origin) == true)
        { updated.VanillaTemplates ??= []; updated.VanillaTemplates.Add(copyId, origin); }
        if (original.VanillaTemplateReferences?.TryGetValue(sourceId, out var reference) == true)
        { updated.VanillaTemplateReferences ??= []; updated.VanillaTemplateReferences.Add(copyId, reference); }
        if (original.VanillaModels?.TryGetValue(sourceId, out var models) == true)
        { updated.VanillaModels ??= []; updated.VanillaModels.Add(copyId, models); }
        var json = new AtomicJsonFile<ProjectDocument>(projectPath, () => new(), JsonOptions);
        json.WriteUnderLeaseAsync(updated, token, before.EnsureUnchanged).GetAwaiter().GetResult();
        var published = new WorkspaceFileSnapshot(projectPath);
        try { writePlugin(); }
        catch (Exception error)
        {
            try { json.WriteUnderLeaseAsync(original, CancellationToken.None, published.EnsureUnchanged).GetAwaiter().GetResult(); }
            catch (Exception rollback) { error.Data["DuplicateMetadataRollback"] = rollback.Message; }
            throw;
        }
    }
}
