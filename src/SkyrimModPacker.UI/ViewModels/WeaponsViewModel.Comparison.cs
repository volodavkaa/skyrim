using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed record WeaponBalanceRow(string Label, string Vanilla, string Custom, string Difference);

public sealed partial class WeaponsViewModel
{
    private void RefreshStage4Texts() { OnPropertyChanged(nameof(WeaponTypes)); FilterTemplates(); RefreshBalance(); OnPropertyChanged(nameof(CompatibilityWarnings)); }
    public string? CompatibilityWarnings
    {
        get
        {
            var warnings = new List<string>();
            if (WeaponKeywords.Select(k => k.Reference.PluginName.ToUpperInvariant() + ":" + k.Reference.FormId).Distinct().Count() != WeaponKeywords.Count)
                warnings.Add(Text("Stage4_DuplicateKeywords", "Duplicate keywords must be removed before saving."));
            if (!ushort.TryParse(EnchantmentCharge, out var charge) || (SelectedEnchantment == null ? charge != 0 : charge == 0))
                warnings.Add(Text("Stage4_ChargeWarning", "Use zero charge without enchantment and positive charge with a weapon enchantment."));
            if (SelectedEnchantment != null && Options.Enchantments.All(e => e.Reference != SelectedEnchantment.Reference))
                warnings.Add(Text("Stage4_EnchantWarning", "This enchantment is outside the known vanilla weapon list. References, effects and restrictions will be checked on save."));
            var known = new Dictionary<string, WeaponKind>(StringComparer.OrdinalIgnoreCase) {
                ["WeapTypeSword"] = WeaponKind.OneHandSword, ["WeapTypeDagger"] = WeaponKind.Dagger, ["WeapTypeWarAxe"] = WeaponKind.WarAxe,
                ["WeapTypeMace"] = WeaponKind.Mace, ["WeapTypeGreatsword"] = WeaponKind.Greatsword, ["WeapTypeBattleaxe"] = WeaponKind.BattleaxeWarhammer,
                ["WeapTypeWarhammer"] = WeaponKind.BattleaxeWarhammer, ["WeapTypeBow"] = WeaponKind.Bow, ["WeapTypeCrossbow"] = WeaponKind.Crossbow };
            if (WeaponKeywords.Any(k => known.TryGetValue(k.EditorId, out var kind) && kind != SelectedWeaponKind && !(SelectedWeaponKind == WeaponKind.Crossbow && kind == WeaponKind.Bow)))
                warnings.Add(Text("Stage4_TypeKeywordWarning", "A keyword conflicts with the inherited weapon animation type."));
            return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
        }
    }
    [ObservableProperty] private SwordTemplate? _balanceTemplate;
    [ObservableProperty] private ObservableCollection<WeaponBalanceRow> _balanceRows = [];
    [ObservableProperty] private string _keywordSearch = "";
    [ObservableProperty] private ObservableCollection<RecordOption> _filteredKeywords = [];
    partial void OnBalanceTemplateChanged(SwordTemplate? value) => RefreshBalance();
    partial void OnKeywordSearchChanged(string value) => FilterKeywords();
    private void FilterKeywords()
    {
        var words = KeywordSearch.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        FilteredKeywords = new(Options.Keywords.Where(k => words.All(w => (k.EditorId + " " + k.Reference).Contains(w, StringComparison.OrdinalIgnoreCase))));
    }
    private void RefreshBalance()
    {
        if (BalanceTemplate == null) { BalanceRows = []; return; }
        var t = BalanceTemplate;
        var rows = new List<WeaponBalanceRow>();
        void Add(string key, string fallback, double vanilla, string current)
        {
            var valid = (double.TryParse(current, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
            rows.Add(new(Text(key, fallback), vanilla.ToString("0.###", CultureInfo.CurrentCulture), current,
                valid ? (value - vanilla).ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture) : "—"));
        }
        Add("Weapons_Damage", "Damage", t.Damage, Damage); Add("Weapons_Weight", "Weight", t.Weight, Weight);
        Add("Weapons_Value", "Value", t.Value, Value); Add("Weapons_Speed", "Speed", t.Details.Speed, Speed); Add("Weapons_Reach", "Reach", t.Details.Reach, Reach);
        if (SelectedWeaponKind is WeaponKind.Bow or WeaponKind.Crossbow)
        {
            var actual = SelectedWeapon?.Details ?? SelectedTemplate?.Details ?? t.Details;
            Add("Stage4_RangeMin", "Minimum range", t.Details.RangeMin, actual.RangeMin.ToString(CultureInfo.CurrentCulture));
            Add("Stage4_RangeMax", "Maximum range", t.Details.RangeMax, actual.RangeMax.ToString(CultureInfo.CurrentCulture));
            Add("Stage4_Stagger", "Stagger", t.Details.Stagger, actual.Stagger.ToString(CultureInfo.CurrentCulture));
        }
        BalanceRows = new(rows);
    }

    [RelayCommand]
    private Task LoadBalanceOriginAsync() => RunOperationAsync(async token =>
    {
        if (SelectedWeapon == null) { BalanceTemplate = SelectedTemplate; return; }
        RequireProject();
        var origin = await _projects.GetTemplateReferenceAsync(ProjectFilePath, GameDataPath, SelectedWeapon.FormId, token);
        if (origin.IsFailure) throw new InvalidOperationException(origin.Error);
        if (origin.Value == null) throw new InvalidOperationException(Text("Stage4_ChooseComparison", "No stored origin: choose a comparison template."));
        BalanceTemplate = Templates.FirstOrDefault(t => t.FormId == origin.Value.FormId && t.PluginName.Equals(origin.Value.PluginName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(Text("Recovery_TemplateMissing", "The original template is missing."));
    });

    [RelayCommand]
    private Task NewTemperingRecipeAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(token =>
    {
        var bench = WorkbenchChoices.FirstOrDefault(k => k.EditorId == "CraftingSmithingSharpeningWheel")
            ?? throw new InvalidOperationException(Text("Stage4_NoGrindstone", "The grindstone workbench keyword is missing."));
        SelectedRecipe = null; ResetRecipeEditor(); SelectedWorkbench = bench; RecipeEditorId = "Temper" + (SelectedWeapon?.EditorId ?? EditorId);
        _newRecipePending = true; UpdateUnsavedChanges();
        return Task.CompletedTask;
    }), recipeOnly: true);
}
