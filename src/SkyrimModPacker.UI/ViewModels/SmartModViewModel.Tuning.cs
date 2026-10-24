using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed record SmartTuningPreview(SmartModItemDraft Before, SmartModItemDraft After, string Description, bool VerifyMaterialSource = false)
{ public string? ExpectedSourceFingerprint { get; init; } }

public sealed partial class SmartModViewModel
{
    private readonly Dictionary<Guid, SmartModItemDraft> _tuningUndo = [];
    public ObservableCollection<SmartMaterialPreset> MaterialPresets { get; } = [];
    public bool CanUseBalance => CurrentSubtype != null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApplyReference))] private SwordTemplate? _selectedReference;
    public bool CanApplyReference => CanUseBalance && SelectedReference != null && ReferenceChoices.Contains(SelectedReference);
    [ObservableProperty] private string _referenceSearch = "";
    public IEnumerable<SwordTemplate> ReferenceChoices => Templates.Where(t => t.Kind == SelectedItem?.Kind && (t.Kind != WeaponKind.BattleaxeWarhammer ||
        (t.Details.Keywords.Any(k => Options.Keywords.Any(o => o.Reference == k && o.EditorId == (CurrentSubtype == SmartWeaponSubtype.Warhammer ? "WeapTypeWarhammer" : "WeapTypeBattleaxe"))))) &&
        (t.Name.Contains(ReferenceSearch, StringComparison.CurrentCultureIgnoreCase) || t.EditorId.Contains(ReferenceSearch, StringComparison.OrdinalIgnoreCase))).Take(100);
    partial void OnReferenceSearchChanged(string value) { SelectedReference = null; OnPropertyChanged(nameof(ReferenceChoices)); }
    public string MaterialAvailabilityText => CurrentSubtype == null ? Label("Smart_MaterialNoSubtype") : MaterialPresets.Count == 0 ? Label("Smart_MaterialNoCatalog") : "";
    public Task RefreshMaterialPresetsAsync() => RunAsync(async token =>
    {
        var id = SelectedMaterialPreset?.Id;
        var presets = Require(await _builder.GetSmartMaterialPresetsAsync(_gameData, token));
        MaterialPresets.Clear(); foreach (var preset in presets) MaterialPresets.Add(preset);
        SelectedMaterialPreset = MaterialChoices.FirstOrDefault(p => p.Id == id) ?? MaterialChoices.FirstOrDefault(p => p.Available); NotifyStep();
    });
    [ObservableProperty] private SmartMaterialPreset? _selectedMaterialPreset;
    public IEnumerable<SmartMaterialPreset> MaterialChoices => MaterialPresets.Where(p => p.Kind == SelectedItem?.Kind && p.Subtype == CurrentSubtype);
    public SmartWeaponSubtype? CurrentSubtype => SelectedItem?.Kind switch
    {
        WeaponKind.OneHandSword => SmartWeaponSubtype.Sword, WeaponKind.Dagger => SmartWeaponSubtype.Dagger, WeaponKind.WarAxe => SmartWeaponSubtype.WarAxe,
        WeaponKind.Mace => SmartWeaponSubtype.Mace, WeaponKind.Greatsword => SmartWeaponSubtype.Greatsword,
        WeaponKind.BattleaxeWarhammer => TemplateKeywords().Contains("WeapTypeWarhammer") ? SmartWeaponSubtype.Warhammer : TemplateKeywords().Contains("WeapTypeBattleaxe") ? SmartWeaponSubtype.Battleaxe : null,
        _ => null
    };
    private IEnumerable<string> TemplateKeywords() => (Templates.FirstOrDefault(t => t.FormId == SelectedItem?.Template?.FormId && t.PluginName == SelectedItem.Template.PluginName)?.Details.Keywords ?? [])
        .Select(k => Options.Keywords.FirstOrDefault(o => o.Reference == k)?.EditorId ?? "");
    private static SmartWeaponTuning? TuningForTemplate(SwordTemplate template, bool manual = false)
    {
        var stats = new SmartWeaponStats(template.Damage, template.Weight, template.Value, template.Details.Speed, template.Details.Reach);
        try { stats.Validate(); } catch (InvalidDataException) { return null; }
        return new() { Reference = new(template.PluginName, template.FormId), ReferenceEditorId = template.EditorId, Base = stats,
            ManualFields = manual ? SmartWeaponBalance.Fields : [], Mode = manual ? SmartBalanceMode.Custom : SmartBalanceMode.Equal };
    }
    public bool CanUndoTuning => SelectedItem != null && _tuningUndo.ContainsKey(SelectedItem.Id);
    public bool DefaultCrafting => SelectedItem?.Tuning?.CraftingIntent == SmartRecipeIntent.Unspecified || HasCraftingRecipe;
    public bool DefaultTempering => SelectedItem?.Tuning?.TemperingIntent == SmartRecipeIntent.Unspecified || HasTemperingRecipe;
    public string TuningSummary
    {
        get
        {
            var state = SelectedItem?.Tuning;
            if (state?.Base == null) return Label("Smart_TuningManualLegacy");
            var manual = string.Join(", ", state.ManualFields.Select(f => Label("Smart_TuningField_" + f)));
            var changed = state.PresetId != null && MaterialPresets.FirstOrDefault(p => p.Id == state.PresetId) is { } preset && preset.SourceFingerprint != state.SourceFingerprint;
            var name = Templates.FirstOrDefault(t => t.FormId == state.Reference?.FormId && t.PluginName == state.Reference.PluginName)?.Name ?? state.ReferenceEditorId;
            var displayMode = state.ManualFields.Any(SmartWeaponBalance.Fields.Contains) ? SmartBalanceMode.Custom : state.Mode;
            return (state.PresetId == null ? "" : Label("Smart_Material" + state.PresetId.Split(':')[0]) + Environment.NewLine) + name + " [" + state.ReferenceEditorId + "; " + state.Reference + "] — " + Label("Smart_Balance_" + displayMode) +
                Environment.NewLine + Label("Smart_Handling_" + state.Handling) +
                (manual.Length == 0 ? "" : Environment.NewLine + Label("Smart_TuningManualFields") + " " + manual) + (changed ? Environment.NewLine + Label("Smart_TuningSourceChanged") : "");
        }
    }
    public IReadOnlyList<SmartBalanceComparison> BalanceComparison => SelectedItem?.Tuning?.Base is not { } b ? [] :
        new[] { Compare("Damage", b.Damage, SelectedItem.Damage), Compare("Weight", b.Weight, SelectedItem.Weight), Compare("Value", b.Value, SelectedItem.Value),
            Compare("Speed", b.Speed, SelectedItem.Speed), Compare("Reach", b.Reach, SelectedItem.Reach) };
    private SmartBalanceComparison Compare(string field, double baseline, string current)
    {
        var valid = double.TryParse(current.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value);
        var delta = valid ? value - baseline : 0;
        return new(Label("Smart_TuningField_" + field), baseline.ToString("0.###", CultureInfo.CurrentCulture), current,
            !valid ? "?" : delta.ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture) + (baseline == 0 ? "" : " (" + (delta * 100 / baseline).ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture) + "%)"));
    }
    private SmartWeaponTuning ReferenceFor(SmartModItemDraft item) => item.Tuning?.Base != null ? item.Tuning :
        (TuningForTemplate(Templates.FirstOrDefault(t => t.FormId == item.Template?.FormId && t.PluginName == item.Template.PluginName) ?? throw new InvalidOperationException(Label("Smart_UnknownTemplate")), true)
            ?? throw new InvalidOperationException(Label("Smart_TuningNoReference"))) with { ManualFields = SmartWeaponBalance.Fields.Concat(new[] { "Crafting", "Tempering" }).ToArray(),
                CraftingIntent = item.Tuning?.CraftingIntent ?? (item.Recipes.Any(r => !r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled),
                TemperingIntent = item.Tuning?.TemperingIntent ?? (item.Recipes.Any(r => r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled) };
    public SmartTuningPreview PreviewBalance(SmartBalanceMode mode, bool keepManual)
    {
        if (!CanUseBalance) throw new InvalidOperationException(Label("Smart_MaterialNoSubtype"));
        var before = SelectedItem?.Capture() ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        return DescribeTuning(before, SmartTuningApplication.Balance(before, ReferenceFor(before), mode, keepManual));
    }
    public SmartTuningPreview PreviewHandling(SmartHandlingMode handling, bool keepManual)
    {
        if (!CanUseBalance) throw new InvalidOperationException(Label("Smart_MaterialNoSubtype"));
        var before = SelectedItem!.Capture(); var reference = ReferenceFor(before) with { Handling = handling };
        var result = SmartTuningApplication.Balance(before, reference, reference.Mode == SmartBalanceMode.Custom ? SmartBalanceMode.Equal : reference.Mode, keepManual);
        // Handling changes weight/speed; custom damage, value and reach remain independent.
        result = result with { Weapon = result.Weapon with { Damage = before.Weapon.Damage, Value = before.Weapon.Value, Reach = before.Weapon.Reach },
            Tuning = result.Tuning! with { Mode = reference.Mode, ManualFields = keepManual ? reference.ManualFields : reference.ManualFields.Except(new[] { "Weight", "Speed" }).ToArray() } };
        return DescribeTuning(before, result);
    }
    public SmartTuningPreview PreviewReference(bool keepManual)
    {
        var before = SelectedItem?.Capture() ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        var source = SelectedReference ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (!CanUseBalance || !ReferenceChoices.Contains(source)) throw new InvalidOperationException(Label("Smart_TuningWrongSubtype"));
        var state = TuningForTemplate(source) ?? throw new InvalidOperationException(Label("Smart_TuningNoReference"));
        state = (before.Tuning ?? state with { ManualFields = SmartWeaponBalance.Fields.Concat(new[] { "Crafting", "Tempering" }).ToArray(),
            CraftingIntent = before.Recipes.Any(r => !r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled,
            TemperingIntent = before.Recipes.Any(r => r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled }) with { PresetId = null, SourceFingerprint = "", Reference = state.Reference, ReferenceEditorId = state.ReferenceEditorId, Base = state.Base };
        return DescribeTuning(before, SmartTuningApplication.Balance(before, state, state.Mode == SmartBalanceMode.Custom ? SmartBalanceMode.Equal : state.Mode, keepManual));
    }
    public SmartTuningPreview PreviewMaterialStats(SmartMaterialPreset preset, bool keepManual)
    {
        var before = SelectedItem?.Capture() ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (!preset.Available || preset.Kind != before.Kind || preset.Subtype != CurrentSubtype) throw new InvalidOperationException(Label("Smart_TuningWrongSubtype"));
        var state = before.Tuning ?? new SmartWeaponTuning { Mode = SmartBalanceMode.Custom, ManualFields = SmartWeaponBalance.Fields.Concat(new[] { "Crafting", "Tempering" }).ToArray(),
            CraftingIntent = before.Recipes.Any(r => !r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled,
            TemperingIntent = before.Recipes.Any(r => r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled };
        state = state with { PresetId = preset.Id, PresetVersion = preset.Version, Reference = preset.Reference, ReferenceEditorId = preset.EditorId, SourceFingerprint = preset.SourceFingerprint, Base = preset.Stats };
        var after = SmartTuningApplication.Balance(before, state, state.Mode == SmartBalanceMode.Custom ? SmartBalanceMode.Equal : state.Mode, keepManual);
        var family = Options.Keywords.Where(k => k.EditorId.StartsWith("WeapMaterial", StringComparison.Ordinal) || k.EditorId.StartsWith("DLC1WeapMaterial", StringComparison.Ordinal)).Select(k => k.Reference);
        after = after with { Weapon = after.Weapon with { Keywords = before.Weapon.Keywords.Except(family).Concat(preset.MaterialKeywords).Distinct().ToArray() } };
        return DescribeTuning(before, after) with { VerifyMaterialSource = true };
    }
    public SmartTuningPreview PreviewManualTuning()
    {
        var before = SelectedItem?.Capture() ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        var reference = before.Tuning ?? new SmartWeaponTuning {
            CraftingIntent = before.Recipes.Any(r => !r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled,
            TemperingIntent = before.Recipes.Any(r => r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled };
        return DescribeTuning(before, before with { Tuning = reference with { PresetId = null, SourceFingerprint = "", Mode = SmartBalanceMode.Custom,
            ManualFields = SmartWeaponBalance.Fields.Concat(new[] { "Crafting", "Tempering" }).ToArray() } });
    }
    public SmartTuningPreview PreviewMaterial(SmartMaterialPreset preset, bool keepManual, bool crafting, bool tempering)
    {
        var before = SelectedItem?.Capture() ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (preset.Subtype != CurrentSubtype) throw new InvalidOperationException(Label("Smart_TuningWrongSubtype"));
        var family = Options.Keywords.Where(k => k.EditorId.StartsWith("WeapMaterial", StringComparison.Ordinal) || k.EditorId.StartsWith("DLC1WeapMaterial", StringComparison.Ordinal)).Select(k => k.Reference).ToArray();
        return DescribeTuning(before, SmartTuningApplication.Material(before, preset, family, keepManual, crafting, tempering)) with { VerifyMaterialSource = true };
    }
    private SmartTuningPreview DescribeTuning(SmartModItemDraft before, SmartModItemDraft after)
    {
        string Field(WeaponDraftFields fields, string name) => (string)typeof(WeaponDraftFields).GetProperty(name)!.GetValue(fields)!;
        string Ref(PluginRecordReference? r) => r == null ? "—" : Options.Ingredients.Concat(Options.Perks).Concat(Options.Keywords).FirstOrDefault(o => o.Reference == r)?.DisplayName + " [" + r + "]";
        var lines = SmartWeaponBalance.Fields.Select(f => Label("Smart_TuningField_" + f) + ": " + Field(before.Weapon, f) + " → " + Field(after.Weapon, f)).ToList();
        lines.Add(Label("Weapons_Keywords") + ": " + string.Join(", ", before.Weapon.Keywords.Select(Ref)) + " → " + string.Join(", ", after.Weapon.Keywords.Select(Ref)));
        string RecipeDetails(SmartModRecipeDraft recipe)
        {
            var bench = Options.Keywords.FirstOrDefault(k => k.Reference == recipe.Workbench)?.EditorId;
            var workbench = bench switch { "CraftingSmithingForge" => Label("Smart_CraftingBench"), "CraftingSmithingSharpeningWheel" => Label("Smart_TemperingBench"), _ => Ref(recipe.Workbench) };
            return workbench + "; " + Label("Smart_RecipeOutputShort") + ": " + recipe.OutputCount + "; " +
                string.Join(", ", recipe.Ingredients.Select(i => Ref(i.Reference) + " × " + i.Count)) + "; " + Label("Smart_RequiredPerk") + ": " + Ref(recipe.RequiredPerk) +
                (recipe.TemperingPerk == null ? "" : "; " + Ref(recipe.TemperingPerk));
        }
        foreach (var recipe in after.Recipes)
        {
            var previous = before.Recipes.FirstOrDefault(r => r.Id == recipe.Id);
            lines.Add(Label(recipe.IsTempering ? "Smart_Tempering" : "Smart_Crafting") + ":" + Environment.NewLine +
                Label("Smart_RecipeBefore") + ": " + (previous == null ? "—" : RecipeDetails(previous)) + Environment.NewLine +
                Label("Smart_RecipeAfter") + ": " + RecipeDetails(recipe));
        }
        foreach (var recipe in before.Recipes.Where(r => after.Recipes.All(a => a.Id != r.Id))) lines.Add(Label("Smart_DisableRecipe") + ": " + recipe.EditorId);
        lines.Add(Environment.NewLine + Label("Smart_TuningSnapshotOnly"));
        return new(before, after, string.Join(Environment.NewLine, lines));
    }
    public Task ApplyTuningAsync(SmartTuningPreview preview) => RunAsync(async token =>
    {
        var item = SelectedItem ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (JsonSerializer.Serialize(item.Capture()) != JsonSerializer.Serialize(preview.Before)) throw new IOException(Label("Smart_TuningStale"));
        await CheckWorkspaceAsync(token);
        var fingerprint = preview.ExpectedSourceFingerprint;
        if (preview.VerifyMaterialSource && preview.After.Tuning?.PresetId != null)
        {
            var preset = MaterialPresets.FirstOrDefault(p => p.Id == preview.After.Tuning.PresetId && p.SourceFingerprint == preview.After.Tuning.SourceFingerprint)
                ?? throw new IOException(Label("Smart_TuningSourceChanged"));
            fingerprint = preset.SourceFingerprint;
        }
        if (!string.IsNullOrEmpty(fingerprint)) await VerifyPresetFingerprintAsync(fingerprint, token);
        var document = Capture(); var selectedRecipe = SelectedRecipe?.Id;
        try
        {
            _applying = true; item.ApplyAutomation(preview.After); SelectedRecipe = item.Recipes.FirstOrDefault(r => r.Id == selectedRecipe) ?? item.Recipes.FirstOrDefault();
            _applying = false; _tuningUndo[item.Id] = preview.Before; Changed(); await PersistAsync(token);
        }
        catch { _applying = false; ApplyDocument(document with { Revision = _document.Revision }); throw; }
        finally { _applying = false; NotifyStep(); }
    });
    private async Task VerifyPresetFingerprintAsync(string fingerprint, CancellationToken token)
    {
        var names = fingerprint.Split(';').Select(p => p.Split(':')[0]).ToArray();
        if (names.Any(n => !new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Contains(n, StringComparer.OrdinalIgnoreCase))) throw new IOException(Label("Smart_TuningSourceChanged"));
        var hashes = await Task.Run(() => string.Join(";", names.Select(n => { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(Path.Combine(_gameData, n)); return n + ":" + Convert.ToHexString(SHA256.HashData(input)); })), token);
        if (hashes != fingerprint) throw new IOException(Label("Smart_TuningSourceChanged"));
    }
    public Task UndoTuningAsync() => RunAsync(async token =>
    {
        if (SelectedItem is not { } item || !_tuningUndo.TryGetValue(item.Id, out var before)) return;
        var document = Capture();
        try { _applying = true; item.ApplyAutomation(before); SelectedRecipe = item.Recipes.FirstOrDefault(); _applying = false; Changed(); await PersistAsync(token); _tuningUndo.Remove(item.Id); }
        catch { _applying = false; ApplyDocument(document with { Revision = _document.Revision }); throw; }
        finally { _applying = false; NotifyStep(); }
    });
}
public sealed record SmartBalanceComparison(string Parameter, string Reference, string Current, string Difference);
