using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private readonly IWeaponPresetRepository? _presets;
    [ObservableProperty] private ObservableCollection<WeaponPreset> _personalPresets = [];
    [ObservableProperty] private WeaponPreset? _selectedPreset;
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private string? _presetError;

    [RelayCommand]
    private Task LoadPresetsAsync() => RunOperationAsync(async token =>
    {
        PresetError = null;
        if (_presets == null) throw new InvalidOperationException("Preset storage is unavailable.");
        PersonalPresets = new(await _presets.LoadAsync(token));
    });

    [RelayCommand]
    private Task SavePresetAsync() => RunOperationAsync(async token =>
    {
        PresetError = null;
        if (_presets == null) throw new InvalidOperationException("Preset storage is unavailable.");
        if (!ValidateWizardProperties()) { PresetError = Text("Stage5_InvalidPreset", "Correct the fields before saving a preset."); return; }
        var preset = new WeaponPreset
        {
            Name = PresetName.Trim(), Kind = SelectedWeapon?.Kind ?? SelectedTemplate?.Kind ?? SelectedWeaponKind,
            Damage = ushort.Parse(Damage), Weight = ParsePresetFloat(Weight), Value = uint.Parse(Value),
            Speed = ParsePresetFloat(Speed), Reach = ParsePresetFloat(Reach), Description = Description,
            Keywords = WeaponKeywords.Select(k => k.Reference).ToArray(), Sound = SelectedSound?.Reference,
            Enchantment = SelectedEnchantment?.Reference, Charge = ushort.Parse(EnchantmentCharge)
        };
        try { preset.Validate(); await _presets.SaveAsync(preset, token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { PresetError = ex.Message; return; }
        PersonalPresets = new(await _presets.LoadAsync(token)); SelectedPreset = PersonalPresets.First(p => p.Id == preset.Id);
    });

    [RelayCommand]
    private Task ApplyPresetAsync() => RunOperationAsync(async token =>
    {
        PresetError = null;
        var preset = SelectedPreset;
        if (SelectedWeapon != null || preset == null || SelectedTemplate == null || preset.Kind != SelectedTemplate.Kind)
        { PresetError = Text("Stage5_PresetCompatibility", "Select a compatible preset and a template for a new item."); return; }
        try { preset.Validate(); }
        catch (Exception ex) { PresetError = ex.Message; return; }
        var fresh = await _builder.GetWeaponOptionsAsync(GameDataPath, token);
        if (fresh.IsFailure) throw new InvalidOperationException(fresh.Error);
        var options = fresh.Value;
        bool Contains(IEnumerable<RecordOption> values, PluginRecordReference? reference) => reference == null || values.Any(v =>
            v.Reference.FormId == reference.FormId && v.Reference.PluginName.Equals(reference.PluginName, StringComparison.OrdinalIgnoreCase));
        if (preset.Keywords.Any(k => !Contains(options.Keywords, k)) || !Contains(options.Sounds, preset.Sound) || !Contains(options.Enchantments, preset.Enchantment))
        { PresetError = Text("Stage5_StalePreset", "A preset reference is missing or no longer supported. No fields were changed."); return; }
        // One history operation; all compatibility checks precede changing any editor field.
        _applyingEditorState = true;
        try
        {
            Damage = preset.Damage.ToString(); Weight = preset.Weight.ToString(CultureInfo.CurrentCulture); Value = preset.Value.ToString();
            Speed = preset.Speed.ToString(CultureInfo.CurrentCulture); Reach = preset.Reach.ToString(CultureInfo.CurrentCulture);
            Options = options; SoundChoices = new(options.Sounds); EnchantmentChoices = new(options.Enchantments);
            Description = preset.Description; WeaponKeywords = new(preset.Keywords.Select(k => Option(options.Keywords, k)));
            SelectedSound = SelectOption(SoundChoices, preset.Sound); SelectedEnchantment = SelectOption(EnchantmentChoices, preset.Enchantment);
            EnchantmentCharge = preset.Charge.ToString();
        }
        finally { _applyingEditorState = false; UpdateUnsavedChanges(); }
    });

    [RelayCommand]
    private Task DeletePresetAsync() => RunOperationAsync(async token =>
    {
        if (_presets == null || SelectedPreset == null) return;
        await _presets.DeleteAsync(SelectedPreset.Id, token); SelectedPreset = null;
        PersonalPresets = new(await _presets.LoadAsync(token));
    });

    private static float ParsePresetFloat(string text) => float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
        ? value : float.Parse(text, CultureInfo.InvariantCulture);
}
