using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty] private bool _isBaseConnected;
    [ObservableProperty] private string _templateSearch = string.Empty;
    [ObservableProperty] private ObservableCollection<SwordTemplate> _filteredTemplates = [];
    [ObservableProperty] private WeaponKind _selectedWeaponKind = WeaponKind.OneHandSword;
    [ObservableProperty] private string? _templateAvailability;
    public IReadOnlyList<WeaponTypeChoice> WeaponTypes => Enum.GetValues<WeaponKind>().Select(k => new WeaponTypeChoice(k, Text("WeaponType_" + k, k.ToString()))).ToArray();
    public WeaponKind WeaponTypeSelection { get => SelectedWeaponKind; set { if (value != SelectedWeaponKind) PendingSelectionChange = ChangeSelectionAsync(() => { SelectedWeaponKind = value; SelectedTemplate = FilteredTemplates.FirstOrDefault(); }); } }
    partial void OnSelectedWeaponKindChanged(WeaponKind value) { FilterTemplates(); OnPropertyChanged(nameof(WeaponTypeSelection)); }

    partial void OnTemplateSearchChanged(string value) => FilterTemplates();
    partial void OnTemplatesChanged(ObservableCollection<SwordTemplate> value) => FilterTemplates();

    private void FilterTemplates()
    {
        var query = TemplateSearch.Trim();
        FilteredTemplates = new ObservableCollection<SwordTemplate>(Templates.Where(t => t.Kind == SelectedWeaponKind &&
            (t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.EditorId.Contains(query, StringComparison.OrdinalIgnoreCase))));
        TemplateAvailability = FilteredTemplates.Count == 0 ? Text("Stage4_NoTemplates", "No templates match this type/search. Crossbows require installed Dawnguard.esm.") : null;
    }

    private async Task LoadTemplatesCoreAsync(CancellationToken token, bool selectFirst)
    {
        IsBaseConnected = false;
        SelectedTemplate = null;
        Templates = [];
        if (string.IsNullOrWhiteSpace(GameDataPath) || !File.Exists(Path.Combine(GameDataPath, "Skyrim.esm")))
            throw new InvalidOperationException(Text("Weapons_BaseMissing", "Skyrim.esm was not found. Choose the game's Data folder below."));

        var result = await _builder.GetWeaponTemplatesAsync(GameDataPath, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await LoadOptionsCoreAsync(token);
        Templates = new ObservableCollection<SwordTemplate>(result.Value);
        _templateGameDataPath = GameDataPath;
        IsBaseConnected = true;
        if (selectFirst) SelectedTemplate = FilteredTemplates.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ChooseGameDataAsync(string? path)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFolderDialog { Title = Text("Weapons_ChooseData", "Choose Data folder") };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FolderName;
        }
        var selectedPath = path;
        await GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
        {
            if (!File.Exists(Path.Combine(selectedPath, "Skyrim.esm")))
                throw new InvalidOperationException(Text("Weapons_BaseMissing", "Skyrim.esm was not found. Choose the game's Data folder below."));
            var settings = await _settings.LoadAsync(token);
            settings.SkyrimDataPath = Path.GetFullPath(selectedPath);
            await _settings.SaveAsync(settings, token);
            GameDataPath = settings.SkyrimDataPath;
            await LoadTemplatesCoreAsync(token, selectFirst: string.IsNullOrWhiteSpace(EditorId));
            if (SelectedWeapon != null) OnSelectedWeaponChanged(SelectedWeapon);
            else { SelectedTemplate = FilteredTemplates.FirstOrDefault(); if (SelectedTemplate == null) ResetDraft(); }
        }));
    }
}
