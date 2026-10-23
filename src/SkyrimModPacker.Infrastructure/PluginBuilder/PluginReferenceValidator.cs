using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

/// <summary>Resolves the supported editor fields against declared masters, then the plugin itself.</summary>
internal sealed class PluginReferenceValidator(ISkyrimModGetter mod, string path, string dataPath,
    List<PluginDiagnostic> diagnostics, CancellationToken token) : IDisposable
{
    private readonly List<ISkyrimModDisposableGetter> _sources = [];
    private readonly Dictionary<FormKey, IMajorRecordGetter> _effective = [];
    private bool _loaded;

    public void Validate()
    {
        foreach (var record in mod.EnumerateMajorRecords())
        {
            token.ThrowIfCancellationRequested();
            if (record.IsDeleted) continue;
            switch (record)
            {
                case IWeaponGetter weapon:
                    if (weapon.Keywords != null)
                        foreach (var keyword in weapon.Keywords) Check(record, "WEAP.Keywords", keyword, required: true);
                    Check(record, "WEAP.AttackSound", weapon.AttackSound);
                    Check(record, "WEAP.ObjectEffect", weapon.ObjectEffect);
                    break;
                case IConstructibleObjectGetter recipe:
                    Check(record, "COBJ.CreatedObject", recipe.CreatedObject, required: true);
                    Check(record, "COBJ.WorkbenchKeyword", recipe.WorkbenchKeyword);
                    if (recipe.Items != null)
                        foreach (var ingredient in recipe.Items) Check(record, "COBJ.Items", ingredient.Item.Item, required: true);
                    foreach (var condition in recipe.Conditions)
                        if (condition.Data is IHasPerkConditionDataGetter perk)
                            Check(record, "COBJ.HasPerk", perk.Perk.Link, required: true);
                        else Add("REFERENCE_CONDITION_UNSUPPORTED", DiagnosticSeverity.Unsupported, record, "COBJ.Conditions",
                            "This condition's references and execution context are not validated.");
                    break;
                case ILeveledItemGetter list:
                    if (list.Entries != null)
                        foreach (var entry in list.Entries)
                            if (entry.Data != null) Check(record, "LVLI.Entries", entry.Data.Reference, required: true);
                            else Add("REFERENCE_ENTRY_INVALID", DiagnosticSeverity.Error, record, "LVLI.Entries", "The list entry has no data.");
                    break;
            }
        }
        diagnostics.Add(new("REFERENCE_SCOPE", DiagnosticSeverity.Unsupported, path, null,
            "Only WEAP keywords/attack sound/enchantment, COBJ output/materials/workbench/HasPerk and LVLI entries are resolved against declared masters and this plugin. Other fields, condition semantics and the full installed load order are not checked.",
            "Review other fields and the final load order in xEdit before installing."));
    }

    private void Check<T>(IMajorRecordGetter owner, string field, IFormLinkGetter<T> link, bool required = false) where T : class, IMajorRecordGetter
    {
        token.ThrowIfCancellationRequested();
        if (link.FormKey == FormKey.Null)
        {
            if (required) Add("REFERENCE_NULL", DiagnosticSeverity.Error, owner, field, "A required reference is null.");
            return;
        }
        Load();
        if (!_effective.TryGetValue(link.FormKey, out var target))
            Add("REFERENCE_MISSING", DiagnosticSeverity.Error, owner, field, $"Target {link.FormKey} is missing from the declared dependency scope.");
        else if (target.IsDeleted)
            Add("REFERENCE_DELETED", DiagnosticSeverity.Error, owner, field, $"Winning target {link.FormKey} is deleted.");
        else if (target is not T)
            Add("REFERENCE_TYPE", DiagnosticSeverity.Error, owner, field, $"Target {link.FormKey} has type {target.GetType().Name}; expected {typeof(T).Name}.");
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        // First match wins: the plugin overrides all its masters; later declared masters override earlier ones.
        Index(mod);
        foreach (var master in mod.ModHeader.MasterReferences.Reverse())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var sourcePath = PackingPathGuard.ResolveUnderRoot(dataPath, master.Master.FileName.String);
                var source = SkyrimMod.CreateFromBinaryOverlay(sourcePath, SkyrimRelease.SkyrimSE);
                _sources.Add(source);
                Index(source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new("REFERENCE_SOURCE_UNREADABLE", DiagnosticSeverity.Error, path, null,
                    $"Cannot resolve records from {master.Master}: {ex.Message}", "Restore the dependency and repeat validation."));
            }
        }
    }

    private void Index(ISkyrimModGetter source)
    {
        foreach (var record in source.EnumerateMajorRecords())
        {
            token.ThrowIfCancellationRequested();
            _effective.TryAdd(record.FormKey, record);
        }
    }

    private void Add(string code, DiagnosticSeverity severity, IMajorRecordGetter owner, string field, string reason) =>
        diagnostics.Add(new(code, severity, path, $"{owner.FormKey} ({owner.EditorID}) / {field}", reason,
            "Repair the reference or restore the intended non-deleted dependency in a plugin editor; validate again."));

    public void Dispose()
    {
        foreach (var source in _sources) source.Dispose();
    }
}
