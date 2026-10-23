using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed class PluginValidationService : IPluginValidationService
{
    public Task<IReadOnlyList<PluginDiagnostic>> ValidateAsync(string pluginPath, string gameDataPath,
        bool requireEsl = false, Version? targetRuntime = null, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<PluginDiagnostic>>(() =>
    {
        var diagnostics = new List<PluginDiagnostic>();
        void Add(string code, DiagnosticSeverity severity, string reason, string action, string? record = null) =>
            diagnostics.Add(new(code, severity, pluginPath, record, reason, action));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            PackingPathGuard.EnsureNoLinks(pluginPath);
            using var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Construct();
            var version = mod.ModHeader.Stats.Version;
            if (!float.IsFinite(version) || (version != 1.70f && version != 1.71f))
                Add("HEADER_VERSION", DiagnosticSeverity.Error, $"Unsupported header version {version}.", "Use a verified Skyrim SE plugin with header 1.70 or 1.71.");
            if (targetRuntime == null)
                Add("RUNTIME_UNKNOWN", DiagnosticSeverity.Warning, "The target game runtime is unknown.", "Verify the intended SkyrimSE.exe version before installing the archive.");
            else if (version >= 1.71f && targetRuntime < new Version(1, 6, 1130))
                Add("RUNTIME_HEADER", DiagnosticSeverity.Error, $"Header {version} is not natively supported by runtime {targetRuntime}.", "Use runtime 1.6.1130 or newer, or verify an explicitly installed compatibility solution.");
            var masters = mod.ModHeader.MasterReferences.Select(m => m.Master).ToList();
            var seenMasters = new HashSet<ModKey>();
            foreach (var master in masters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (master == mod.ModKey || !seenMasters.Add(master))
                    Add("MASTER_LIST", DiagnosticSeverity.Error, $"Duplicate or self master: {master}.", "Repair the master list in a plugin editor.");
                try
                {
                    var path = PackingPathGuard.ResolveUnderRoot(gameDataPath, master.FileName.String);
                    if (!File.Exists(path)) throw new FileNotFoundException("Master is missing.");
                    using var parent = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    if (parent.ModHeader.MasterReferences.Any(p => !seenMasters.Contains(p.Master) || p.Master == master))
                        Add("MASTER_ORDER", DiagnosticSeverity.Error, $"Dependencies of {master} are absent or ordered after it.", "Include all parent masters before their dependants.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { Add("MASTER_UNREADABLE", DiagnosticSeverity.Error, $"Cannot read {master}: {ex.Message}", "Restore the required master in Data; do not substitute another file."); }
            }
            var keys = new HashSet<FormKey>();
            var editorIds = new HashSet<(Type, string)>();
            var ownedCount = 0;
            foreach (var record in mod.EnumerateMajorRecords())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = $"{record.FormKey} ({record.EditorID})";
                if (!keys.Add(record.FormKey))
                    Add("FORMID_DUPLICATE", DiagnosticSeverity.Error, "Duplicate FormKey.", "Repair duplicate records without automatic renumbering.", identity);
                if (record.FormKey == FormKey.Null || record.FormKey.ID == 0 || record.FormKey.ID > 0xFFFFFF)
                    Add("FORMID_INVALID", DiagnosticSeverity.Error, "Invalid plugin-local FormID.", "Inspect the record in xEdit.", identity);
                if (record.FormKey.ModKey != mod.ModKey && record.FormKey != FormKey.Null && !masters.Contains(record.FormKey.ModKey))
                    Add("FORMID_MASTER", DiagnosticSeverity.Error, "Record owner is absent from the master list.", "Restore the owning master dependency.", identity);
                if (record.FormKey.ModKey == mod.ModKey || record.FormKey == FormKey.Null)
                {
                    ownedCount++;
                    if ((requireEsl || mod.IsSmallMaster) && (record.FormKey.ID < (version >= 1.71f ? 1u : 0x800u) || record.FormKey.ID > 0xFFF))
                        Add("ESL_RANGE", DiagnosticSeverity.Error, "Owned FormID is outside the header-specific ESL range.", "Disable ESL for an ordinary ESP, or repair the source in an external editor. No compaction is performed.", identity);
                }
                if (!string.IsNullOrWhiteSpace(record.EditorID) && !editorIds.Add((record.GetType(), record.EditorID.ToUpperInvariant())))
                    Add("EDITORID_DUPLICATE", DiagnosticSeverity.Error, "EditorID is duplicated within this record type and plugin.", "Assign distinct EditorIDs to these records.", identity);
            }
            if ((requireEsl || mod.IsSmallMaster) && ownedCount > 4096)
                Add("ESL_COUNT", DiagnosticSeverity.Error, $"The plugin contains {ownedCount} owned records.", "Use a non-light plugin or split the source with an external editor.");
            using var references = new PluginReferenceValidator(mod, pluginPath, gameDataPath, diagnostics, cancellationToken);
            references.Validate();
            Add("GAME_NOT_TESTED", DiagnosticSeverity.Unsupported, "This result does not confirm in-game behavior or all record types.", "Complete the xEdit and Skyrim acceptance checks.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Add("PLUGIN_UNREADABLE", DiagnosticSeverity.Error, ex.Message, "Restore a valid plugin or inspect it in xEdit before packing."); }
        return diagnostics;
    }, cancellationToken);
}
