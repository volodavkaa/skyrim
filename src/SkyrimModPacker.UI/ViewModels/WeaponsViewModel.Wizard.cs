using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModStep), nameof(IsTemplateStep), nameof(IsModelStep), nameof(IsPropertiesStep), nameof(IsSummaryStep), nameof(CanGoBack), nameof(CanGoNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousStepCommand), nameof(NextStepCommand))]
    private int _currentStep;
    public bool IsModStep => CurrentStep == 0;
    public bool IsTemplateStep => CurrentStep == 1;
    public bool IsModelStep => CurrentStep == 2;
    public bool IsPropertiesStep => CurrentStep == 3;
    public bool IsSummaryStep => CurrentStep == 4;
    public bool CanGoBack => CurrentStep > 0;
    public bool CanGoNext => CurrentStep < 4;
    public bool HasSelectedWeapon => SelectedWeapon != null;

    [ObservableProperty] private string? _projectError;
    [ObservableProperty] private string? _pluginNameError;
    [ObservableProperty] private string? _outputDirectoryError;
    [ObservableProperty] private string? _templateError;
    [ObservableProperty] private string? _modelError;
    [ObservableProperty] private string? _editorIdError;
    [ObservableProperty] private string? _weaponNameError;
    [ObservableProperty] private string? _damageError;
    [ObservableProperty] private string? _weightError;
    [ObservableProperty] private string? _valueError;
    [ObservableProperty] private string? _speedError;
    [ObservableProperty] private string? _reachError;
    [ObservableProperty] private string? _chargeError;
    [ObservableProperty] private bool _isWizardSaved;
    [ObservableProperty] private bool _showAdvancedProperties;
    [ObservableProperty] private ObservableCollection<WeaponSummaryRow> _weaponSummary = [];
    [ObservableProperty] private ObservableCollection<string> _summaryFiles = [];

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousStep()
    {
        if (IsLoading) return;
        ErrorMessage = null;
        CurrentStep--;
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextStepAsync() => RunOperationAsync(async token =>
    {
        switch (CurrentStep)
        {
            case 0:
                ProjectError = null;
                try { RequirePlugin(); PluginWriteGuard.EnsureOutsideGame(PluginPath, GameDataPath); }
                catch (Exception ex) { ProjectError = ex.Message; return; }
                break;
            case 1:
                TemplateError = null;
                if (SelectedWeapon == null && (!IsBaseConnected || SelectedTemplate == null))
                {
                    TemplateError = Text("Weapons_SelectTemplate", "Select a vanilla sword template first.");
                    return;
                }
                break;
            case 2:
                if (!await ValidateWizardModelAsync(token)) return;
                break;
            case 3:
                if (!ValidateWizardProperties()) return;
                BuildWeaponSummary();
                break;
        }
        CurrentStep++;
    });

    [RelayCommand]
    private async Task SaveWizardSwordAsync()
    {
        if (IsLoading || !IsSummaryStep) return;
        IsWizardSaved = false;
        if (!ValidateWizardProperties()) { CurrentStep = 3; return; }
        // Each existing command owns its cancellation/operation scope.
        if (SelectedWeapon == null) await CloneSwordCommand.ExecuteAsync(null);
        else await SaveSwordCommand.ExecuteAsync(null);
        if (ErrorMessage == null && SelectedWeapon != null && _operationCts?.IsCancellationRequested != true) IsWizardSaved = true;
    }

    private bool ValidateProjectFields()
    {
        PluginNameError = null;
        OutputDirectoryError = null;
        if (string.IsNullOrWhiteSpace(PluginName) || PluginName.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            PluginName.Trim().EndsWith('.') || new[] { ".esp", ".esl", ".esm" }.Contains(Path.GetExtension(PluginName.Trim()), StringComparer.OrdinalIgnoreCase))
            PluginNameError = Text("Wizard_InvalidPluginName", "Enter a filename without folders or an extension.");
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory.Trim()))
            OutputDirectoryError = Text("Wizard_InvalidOutput", "Choose an absolute workspace folder outside the game.");
        else
        {
            try { PluginWriteGuard.EnsureOutsideGame(OutputDirectory.Trim(), GameDataPath); }
            catch (Exception ex) { OutputDirectoryError = ex.Message; }
        }
        return PluginNameError == null && OutputDirectoryError == null;
    }

    private async Task<bool> ValidateWizardModelAsync(CancellationToken token)
    {
        ModelError = null;
        var relative = ModelPath.Replace('\\', '/');
        if (relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) relative = relative[7..];
        if (string.IsNullOrWhiteSpace(relative) || !relative.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
            relative.Split('/').Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            ModelError = Text("Wizard_InvalidModel", "Import a NIF or enter a relative path inside meshes without '..'.");
            return false;
        }
        var inherited = SelectedWeapon?.Details.ModelPath ?? SelectedTemplate?.Details.ModelPath;
        if (!string.IsNullOrWhiteSpace(ProjectFilePath))
        {
            try
            {
                await RefreshProjectResourcesAsync(token);
                await EnsureImportedModelAvailableAsync(token);
                if (!string.Equals(relative, inherited?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    var result = await _projects.ValidateModelAsync(ProjectFilePath, ModelPath, GameDataPath, token);
                    if (result.IsFailure) { ModelError = result.Error; return false; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ModelError = ex.Message; return false; }
        }
        else if (!string.Equals(relative, inherited?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            ModelError = Text("Weapons_RequireProject", "Open a workspace mod to import your model.");
            return false;
        }
        return true;
    }

    private static bool TryNumber(string value, out float number) =>
        (float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number) || float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && float.IsFinite(number);

    private bool ValidateWizardProperties()
    {
        var id = EditorId.Trim();
        EditorIdError = string.IsNullOrWhiteSpace(id) || id.Length > 128 || !char.IsAsciiLetter(id[0]) || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
            ? Text("Wizard_InvalidEditorId", "Use a Latin letter first, then letters, digits or '_', up to 128 characters.") : null;
        if (EditorIdError == null && Weapons.Any(w => w.FormId != SelectedWeapon?.FormId && string.Equals(w.EditorId, id, StringComparison.OrdinalIgnoreCase)))
            EditorIdError = Text("Wizard_DuplicateEditorId", "This EditorID already exists. Enter a unique one.");
        WeaponNameError = string.IsNullOrWhiteSpace(WeaponName) || WeaponName.Contains('\0') ? Text("Wizard_InvalidName", "Enter the weapon's name.") : null;
        DamageError = ushort.TryParse(Damage, out _) ? null : Text("Wizard_InvalidDamage", "Enter an integer from 0 to 65535.");
        WeightError = TryNumber(Weight, out var weight) && weight >= 0 ? null : Text("Wizard_InvalidWeight", "Enter a finite weight of 0 or more.");
        ValueError = uint.TryParse(Value, out _) ? null : Text("Wizard_InvalidValue", "Enter an integer from 0 to 4294967295.");
        SpeedError = TryNumber(Speed, out var speed) && speed > 0 ? null : Text("Wizard_InvalidPositive", "Enter a finite number greater than zero.");
        ReachError = TryNumber(Reach, out var reach) && reach > 0 ? null : Text("Wizard_InvalidPositive", "Enter a finite number greater than zero.");
        ChargeError = ushort.TryParse(EnchantmentCharge, out var charge) && (SelectedEnchantment == null || charge > 0) ? null : Text("Wizard_InvalidCharge", "Use 0–65535; an enchanted weapon needs a positive charge.");
        if (SpeedError != null || ReachError != null || ChargeError != null) ShowAdvancedProperties = true;
        return new[] { EditorIdError, WeaponNameError, DamageError, WeightError, ValueError, SpeedError, ReachError, ChargeError }.All(e => e == null);
    }

    private string SuggestEditorId(string templateId)
    {
        var stem = new string(templateId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(105).ToArray());
        if (stem.Length == 0 || !char.IsAsciiLetter(stem[0])) stem = "Sword";
        stem += "Custom";
        var candidate = stem;
        for (var suffix = 2; Weapons.Any(w => string.Equals(w.EditorId, candidate, StringComparison.OrdinalIgnoreCase)); suffix++) candidate = stem + suffix;
        return candidate;
    }

    private void BuildWeaponSummary()
    {
        var original = SelectedWeapon;
        var template = SelectedTemplate;
        var rows = new List<WeaponSummaryRow>();
        void Add(string key, string fallback, string value, string? before = null) => rows.Add(new WeaponSummaryRow
        {
            Label = Text(key, fallback), Value = before != null && before != value ? before + " → " + value : value, IsChanged = before != null && before != value
        });
        Add("Weapons_ActivePlugin", "Plugin", PluginPath);
        Add("Wizard_Source", "Template / existing record", original?.EditorId ?? template?.EditorId ?? "");
        Add("Wizard_EditorId", "EditorID", EditorId, original?.EditorId ?? template?.EditorId);
        Add("Weapons_Name", "Name", WeaponName, original?.Name ?? template?.Name);
        Add("Weapons_Damage", "Damage", Damage, (original?.Damage ?? template?.Damage)?.ToString());
        Add("Weapons_Weight", "Weight", Weight, (original?.Weight ?? template?.Weight)?.ToString(CultureInfo.CurrentCulture));
        Add("Weapons_Value", "Value", Value, (original?.Value ?? template?.Value)?.ToString());
        var details = original?.Details ?? template?.Details;
        Add("Weapons_Model", "Model", ModelPath, details?.ModelPath);
        Add("Weapons_Description", "Description", Description, details?.Description);
        Add("Weapons_Speed", "Speed", Speed, details?.Speed.ToString(CultureInfo.CurrentCulture));
        Add("Weapons_Reach", "Reach", Reach, details?.Reach.ToString(CultureInfo.CurrentCulture));
        Add("Weapons_Keywords", "Keywords", string.Join(", ", WeaponKeywords.Select(k => k.Reference)), details == null ? null : string.Join(", ", details.Keywords));
        Add("Weapons_Sound", "Attack sound", SelectedSound?.Reference.ToString() ?? "—", details?.AttackSound?.ToString() ?? "—");
        Add("Weapons_Enchantment", "Enchantment", SelectedEnchantment?.Reference.ToString() ?? "—", details?.Enchantment?.ToString() ?? "—");
        Add("Weapons_Charge", "Charge", EnchantmentCharge, details?.EnchantmentCharge.ToString());
        WeaponSummary = new ObservableCollection<WeaponSummaryRow>(rows);
        SummaryFiles = new ObservableCollection<string>(new[] { Path.GetFileName(PluginPath) }.Concat(ProjectResources));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(EditorId): EditorIdError = null; break;
            case nameof(WeaponName): WeaponNameError = null; break;
            case nameof(Damage): DamageError = null; break;
            case nameof(Weight): WeightError = null; break;
            case nameof(Value): ValueError = null; break;
            case nameof(Speed): SpeedError = null; break;
            case nameof(Reach): ReachError = null; break;
            case nameof(EnchantmentCharge): ChargeError = null; break;
            case nameof(ModelPath): ModelError = null; break;
            case nameof(Description):
            case nameof(SelectedSound):
            case nameof(SelectedEnchantment):
            case nameof(WeaponKeywords): break;
            case nameof(PluginName): PluginNameError = null; return;
            case nameof(OutputDirectory): OutputDirectoryError = null; return;
            case nameof(PluginPath): ProjectError = null; CurrentStep = 0; IsWizardSaved = false; LastProjectArchivePath = null; return;
            case nameof(SelectedTemplate): TemplateError = null; return;
            case nameof(SelectedWeapon): OnPropertyChanged(nameof(HasSelectedWeapon)); IsWizardSaved = false; return;
            default: return;
        }
        IsWizardSaved = false;
    }
}

public sealed class WeaponSummaryRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public bool IsChanged { get; init; }
}
