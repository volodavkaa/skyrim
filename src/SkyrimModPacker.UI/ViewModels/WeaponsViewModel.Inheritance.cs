using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private readonly Dictionary<uint, PluginRecordReference> _weaponOrigins = [];
    private readonly Dictionary<uint, int> _weaponRecipeCounts = [];
    private IReadOnlyDictionary<string, PluginRecordReference?>? _draftSounds;
    private SwordTemplate? InheritanceTemplate => SelectedWeapon == null ? SelectedTemplate :
        _weaponOrigins.TryGetValue(SelectedWeapon.FormId, out var origin) ? Templates.FirstOrDefault(t =>
            t.FormId == origin.FormId && t.PluginName.Equals(origin.PluginName, StringComparison.OrdinalIgnoreCase)) : null;

    public string TemplateOriginText => InheritanceTemplate is { } template ?
        $"{template.Name} — {template.EditorId} [{template.PluginName}:{template.FormId:X6}]" :
        Text("Items_UnknownTemplate", "Original template is unknown. Choose a template below only if you want to restore its values.");
    public string KeywordsInheritanceText
    {
        get
        {
            var baseline = InheritanceTemplate?.Details.Keywords;
            var current = WeaponKeywords.Select(k => k.Reference).ToHashSet();
            var lines = new List<string>();
            if (baseline != null)
                lines.AddRange(baseline.Select(k => DescribeReference(Options.Keywords, k) + " — " +
                    Text(current.Contains(k) ? "Items_Inherited" : "Items_Cleared", current.Contains(k) ? "Inherited" : "Removed")));
            lines.AddRange(current.Where(k => baseline == null || !baseline.Contains(k)).Select(k => DescribeReference(Options.Keywords, k) + " — " +
                Text(baseline == null ? "Items_Current" : "Items_Changed", baseline == null ? "Current; origin unknown" : "Added / changed")));
            return lines.Count == 0 ? Text("Items_NoKeywords", "No keywords assigned.") : string.Join(Environment.NewLine, lines);
        }
    }
    public string TemplateChangesText
    {
        get
        {
            if (InheritanceTemplate is not { } template) return "";
            var changes = new List<string>();
            void Compare(string key, string original, string current)
            {
                if (original == current) return;
                changes.Add(Text(key, key) + ": " + original + " → " + (string.IsNullOrWhiteSpace(current)
                    ? Text("Items_Cleared", "Cleared") : current));
            }
            Compare("Weapons_Name", template.Name, WeaponName);
            Compare("Weapons_Damage", template.Damage.ToString(), Damage);
            Compare("Weapons_Weight", template.Weight.ToString(CultureInfo.CurrentCulture), Weight);
            Compare("Weapons_Value", template.Value.ToString(), Value);
            Compare("Weapons_Speed", template.Details.Speed.ToString(CultureInfo.CurrentCulture), Speed);
            Compare("Weapons_Reach", template.Details.Reach.ToString(CultureInfo.CurrentCulture), Reach);
            Compare("Weapons_Description", template.Details.Description, Description);
            Compare("Models_Third", template.Details.ModelPath, ModelPath);
            Compare("Weapons_Enchantment", DescribeReference(Options.Enchantments, template.Details.Enchantment), DescribeReference(Options.Enchantments, SelectedEnchantment?.Reference));
            Compare("Weapons_Charge", template.Details.EnchantmentCharge.ToString(), EnchantmentCharge);
            return changes.Count == 0 ? Text("Items_ParametersUnchanged", "Characteristics match the template.") : string.Join(Environment.NewLine, changes);
        }
    }
    public string SoundsInheritanceText
    {
        get
        {
            var baseline = InheritanceTemplate?.Details;
            var sounds = _draftSounds ?? SelectedWeapon?.Details.Sounds ?? InheritanceTemplate?.Details.Sounds ?? new Dictionary<string, PluginRecordReference?>();
            var keys = sounds.Keys.Concat(baseline?.Sounds?.Keys ?? []).Append("AttackSound").Distinct();
            return string.Join(Environment.NewLine, keys.Select(slot =>
            {
                var current = slot == "AttackSound" ? SelectedSound?.Reference : sounds.GetValueOrDefault(slot);
                var original = slot == "AttackSound" ? baseline?.AttackSound : baseline?.Sounds?.GetValueOrDefault(slot);
                var status = baseline == null ? "Items_Current" : current == original ? "Items_Inherited" : current == null ? "Items_Cleared" : "Items_Changed";
                return Text("Items_Sound_" + slot, slot) + ": " + DescribeReference(Options.Sounds, current) + " — " + Text(status, status) +
                    (baseline != null && current != original ? " (" + Text("Items_TemplateValue", "Template") + ": " + DescribeReference(Options.Sounds, original) + ")" : "");
            }));
        }
    }
    private static string DescribeReference(IEnumerable<RecordOption> choices, PluginRecordReference? reference) =>
        reference == null ? "—" : Option(choices, reference) is { } option && !string.IsNullOrWhiteSpace(option.EditorId)
            ? option.EditorId + " [" + reference + "]" : reference.ToString();

    private void NotifyInheritance()
    {
        OnPropertyChanged(nameof(TemplateOriginText));
        OnPropertyChanged(nameof(KeywordsInheritanceText));
        OnPropertyChanged(nameof(SoundsInheritanceText));
        OnPropertyChanged(nameof(TemplateChangesText));
    }

    private async Task RefreshWeaponOriginsAsync(CancellationToken token)
    {
        _weaponOrigins.Clear();
        _weaponRecipeCounts.Clear();
        foreach (var weapon in Weapons)
        {
            var recipes = await _builder.GetRecipesAsync(PluginPath, weapon.FormId, token);
            if (recipes?.IsSuccess == true) _weaponRecipeCounts[weapon.FormId] = recipes.Value.Count;
        }
        if (!string.IsNullOrWhiteSpace(ProjectFilePath))
            foreach (var weapon in Weapons)
            {
                var result = await _projects.GetTemplateReferenceAsync(ProjectFilePath, GameDataPath, weapon.FormId, token);
                if (result?.IsSuccess == true && result.Value != null) _weaponOrigins[weapon.FormId] = result.Value;
            }
        NotifyInheritance();
        OnPropertyChanged(nameof(ProjectItemsSummary));
    }

    [RelayCommand]
    private void RestoreTemplateParameters()
    {
        if (IsEditorBusy) return;
        var template = InheritanceTemplate ?? RestorationTemplate;
        if (template == null) { ErrorMessage = Text("Models_SelectOriginal", "Choose a vanilla template first."); return; }
        ApplyTemplateRestoration(() =>
        {
            Damage = template.Damage.ToString(); Weight = template.Weight.ToString(CultureInfo.CurrentCulture); Value = template.Value.ToString();
            Speed = template.Details.Speed.ToString(CultureInfo.CurrentCulture); Reach = template.Details.Reach.ToString(CultureInfo.CurrentCulture);
            Description = template.Details.Description;
            SelectedEnchantment = SelectOption(EnchantmentChoices, template.Details.Enchantment);
            EnchantmentCharge = template.Details.EnchantmentCharge.ToString();
        });
    }

    [RelayCommand]
    private void RestoreTemplateKeywords()
    {
        if (IsEditorBusy) return;
        var template = InheritanceTemplate ?? RestorationTemplate;
        if (template == null) { ErrorMessage = Text("Models_SelectOriginal", "Choose a vanilla template first."); return; }
        ApplyTemplateRestoration(() => WeaponKeywords = new ObservableCollection<RecordOption>(template.Details.Keywords.Select(k => Option(Options.Keywords, k))));
    }

    [RelayCommand]
    private void RestoreTemplateSounds()
    {
        if (IsEditorBusy) return;
        var template = InheritanceTemplate ?? RestorationTemplate;
        if (template == null) { ErrorMessage = Text("Models_SelectOriginal", "Choose a vanilla template first."); return; }
        ApplyTemplateRestoration(() =>
        {
            _draftSounds = template.Details.Sounds;
            SelectedSound = SelectOption(SoundChoices, template.Details.AttackSound);
        });
    }

    private void ApplyTemplateRestoration(Action apply)
    {
        ErrorMessage = null;
        _applyingEditorState = true;
        try { apply(); }
        finally { _applyingEditorState = false; IsWizardSaved = false; UpdateUnsavedChanges(); }
    }
}
