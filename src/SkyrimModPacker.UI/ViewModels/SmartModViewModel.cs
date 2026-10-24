using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>Independent wizard session over the existing workspace, plugin and packing services.</summary>
public sealed partial class SmartModViewModel : ObservableObject, IDisposable
{
    private readonly IPluginBuilderService _builder;
    private readonly IWeaponProjectService _projects;
    private readonly ISettingsRepository _settings;
    private readonly ISmartModDraftRepository _drafts;
    private readonly IPluginValidationService _validator;
    private readonly PackingPipeline? _pipeline;
    private readonly IWorkspaceHistoryService? _history;
    private readonly ILocalizationService? _localization;
    private readonly IWorkspaceFolderOpener? _opener;
    private readonly IModelThumbnailService? _thumbnails;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DraftAutosaveScheduler _autosave = new();
    private SmartModDraft _document = new();
    private bool _applying, _disposed, _writeBlocked;
    private long _editGeneration;
    private string _gameData = "";
    private WeaponOptions _options = new();
    public WeaponOptions Options => _options;
    public string ProjectPath => _document.ProjectFilePath;
    public string PluginPath => _document.PluginPath;
    public string ProjectDirectory => string.IsNullOrEmpty(ProjectPath) ? "" : Path.GetDirectoryName(ProjectPath)!;
    public string GameDataPath => _gameData;
    public ObservableCollection<SmartModTypeRow> Types { get; } = [];
    public ObservableCollection<SmartModItemRow> Items { get; } = [];
    public ObservableCollection<SmartModIssue> Issues { get; } = [];
    public ObservableCollection<ProjectResourceRow> Resources { get; } = [];
    public ObservableCollection<SwordTemplate> Templates { get; } = [];
    public IEnumerable<SwordTemplate> ItemTemplates => Templates.Where(t => t.Kind == SelectedItem?.Kind);
    public IEnumerable<RecordOption> Workbenches => Options.Keywords.Where(k => k.EditorId.StartsWith("Crafting", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<SmartModRecipeLink> RecipeLinks => Items.SelectMany(i => i.Recipes.Select(r =>
        new SmartModRecipeLink(i, r, i.Name + " — " + Label(r.IsTempering ? "Smart_Tempering" : "Smart_Crafting") + " — " +
            Label(!RecipeIsValid(r.Capture()) ? "Smart_State_NeedsCorrection" : r.ConfirmedFormId == null ? "Smart_State_InProgress" : "Smart_State_Ready"))));
    [ObservableProperty] private string _name = "MyWeapons";
    [ObservableProperty] private string _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimModPacker", "Plugins");
    [ObservableProperty] private bool _enableEsl;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSetup), nameof(CanCreateProject))] private bool _hasProject;
    public bool ShowSetup => !HasProject;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private string _dependencies = "";
    [ObservableProperty] private string? _lastArchivePath;
    [ObservableProperty] private SmartModItemRow? _selectedItem;
    [ObservableProperty] private SmartModRecipeRow? _selectedRecipe;
    [ObservableProperty] private SwordTemplate? _selectedTemplate;
    [ObservableProperty] private SmartModSection _section;
    [ObservableProperty] private RecordOption? _keywordToAdd;
    [ObservableProperty] private PluginRecordReference? _selectedKeyword;
    [ObservableProperty] private ProjectResourceRow? _selectedResource;
    [ObservableProperty] private RecordOption? _selectedLeveledList;
    [ObservableProperty] private string _listLevel = "1";
    [ObservableProperty] private string _listCount = "1";
    public bool IsOverview => Section == SmartModSection.Overview;
    public bool IsEditing => Section is SmartModSection.Weapons or SmartModSection.Recipes;
    public bool IsResources => Section == SmartModSection.Resources;
    public bool IsChecks => Section == SmartModSection.Checks;
    public bool IsFinished => Section == SmartModSection.Finished;
    public bool IsNameStep => IsEditing && SelectedItem?.Step == SmartModItemStep.NameAndTemplate;
    public bool IsAppearanceStep => IsEditing && SelectedItem?.Step == SmartModItemStep.Appearance;
    public bool IsPropertiesStep => IsEditing && SelectedItem?.Step == SmartModItemStep.Properties;
    public bool IsAcquisitionStep => IsEditing && SelectedItem?.Step == SmartModItemStep.Acquisition;
    public bool IsSummaryStep => IsEditing && SelectedItem?.Step == SmartModItemStep.Summary;
    public string Summary => Name + Environment.NewLine + string.Join(Environment.NewLine,
        Items.Select(i => $"{i.Name} — {i.KindName}; {i.Damage} / {i.Weight} / {i.Value}; {i.ModelPath}; {i.Recipes.Count} " + Label("Smart_Recipes"))) +
        Environment.NewLine + Label("Smart_Resources") + ": " + Resources.Count + Environment.NewLine + "Masters: " + Dependencies;
    public string InheritanceText => DescribeInheritance();
    public event Action? PreviewChanged;

    public SmartModViewModel(IPluginBuilderService builder, IWeaponProjectService projects, ISettingsRepository settings,
        ISmartModDraftRepository drafts, IPluginValidationService validator, PackingPipeline? pipeline = null,
        IWorkspaceHistoryService? history = null, ILocalizationService? localization = null,
        IWorkspaceFolderOpener? opener = null, IModelThumbnailService? thumbnails = null)
    {
        _builder = builder; _projects = projects; _settings = settings; _drafts = drafts; _validator = validator;
        _pipeline = pipeline; _history = history; _localization = localization; _opener = opener; _thumbnails = thumbnails;
        if (_localization != null) _localization.LanguageChanged += RefreshTexts;
        Items.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(RecipeLinks)); OnPropertyChanged(nameof(Summary)); };
    }

    public string Label(string key) => _localization?.GetString(key) ?? key;
    private string Text(string uk, string en) => _localization?.CurrentLanguage == "en" ? en : uk;
    private static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error);
    private void NotifyStep()
    {
        foreach (var name in new[] { nameof(StepLinks), nameof(StepHelp), nameof(ProgressText), nameof(NextActionText), nameof(SelectedSummary) }) OnPropertyChanged(name);
        foreach (var name in new[] { nameof(IsOverview), nameof(IsEditing), nameof(IsResources), nameof(IsChecks), nameof(IsFinished), nameof(IsNameStep), nameof(IsAppearanceStep), nameof(IsPropertiesStep), nameof(IsAcquisitionStep), nameof(IsSummaryStep), nameof(InheritanceText), nameof(Summary), nameof(ItemTemplates), nameof(RecipeLinks), nameof(RecipeValidationText) }) OnPropertyChanged(name);
    }
    private void RefreshTexts() { NotifyStep(); foreach (var item in Items) item.Replace(item.Capture()); }
    partial void OnNameChanged(string value) => Changed();
    partial void OnEnableEslChanged(bool value) => Changed();
    partial void OnSectionChanged(SmartModSection value) { NotifyStep(); Changed(false); }
    partial void OnSelectedItemChanged(SmartModItemRow? value)
    {
        SelectedRecipe = value?.Recipes.FirstOrDefault();
        SelectedTemplate = Templates.FirstOrDefault(t => t.FormId == value?.Template?.FormId && t.PluginName == value.Template.PluginName);
        if (value != null && !_applying) Section = SmartModSection.Weapons;
        NotifyStep(); Changed(false); PreviewChanged?.Invoke();
    }
    partial void OnSelectedRecipeChanged(SmartModRecipeRow? value) => Changed(false);
    partial void OnSelectedResourceChanged(ProjectResourceRow? value) => Changed(false);
    partial void OnSelectedLeveledListChanged(RecordOption? value) => Changed(false);
    partial void OnListLevelChanged(string value) => Changed(false);
    partial void OnListCountChanged(string value) => Changed(false);
    private void Changed(bool invalidateArchive = true)
    {
        if (_applying || _disposed) return;
        _editGeneration++; if (invalidateArchive) LastArchivePath = null; SaveStatus = Label("Smart_SavePending");
        NotifyStep();
        if (HasProject) _autosave.Schedule(async () => await SaveDraftAsync());
    }
    private void Attach(SmartModItemRow row)
    {
        row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SmartModItemRow.StateText)) OnPropertyChanged(nameof(ProgressText)); };
        row.Changed += () => { Changed(); PreviewChanged?.Invoke(); };
        row.NavigationChanged += () => { Changed(false); NotifyStep(); };
        row.Recipes.CollectionChanged += (_, args) =>
        {
            if (args.NewItems != null) foreach (SmartModRecipeRow recipe in args.NewItems) AttachRecipe(row, recipe);
            Changed();
        };
        foreach (var recipe in row.Recipes) AttachRecipe(row, recipe);
        Items.Add(row);
    }
    private void AttachRecipe(SmartModItemRow owner, SmartModRecipeRow recipe)
    {
        recipe.PropertyChanged += (_, _) => { owner.SetState(SmartModItemState.InProgress); Changed(); };
        void AttachIngredient(SmartModIngredientRow ingredient) => ingredient.PropertyChanged += (_, _) => { owner.SetState(SmartModItemState.InProgress); Changed(); };
        foreach (var ingredient in recipe.Ingredients) AttachIngredient(ingredient);
        recipe.Ingredients.CollectionChanged += (_, args) =>
        {
            if (args.NewItems != null) foreach (SmartModIngredientRow ingredient in args.NewItems) AttachIngredient(ingredient);
            owner.SetState(SmartModItemState.InProgress); Changed();
        };
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        await _gate.WaitAsync();
        try { Error = ""; await action(_lifetime.Token); }
        catch (OperationCanceledException) { Status = Label("Preview_Cancelled"); }
        catch (Exception ex) { Error = ex.Message; }
        finally { _gate.Release(); IsBusy = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _autosave.Dispose(); _thumbnailCancellation?.Cancel(); _lifetime.Cancel(); _lifetime.Dispose();
        if (_localization != null) _localization.LanguageChanged -= RefreshTexts;
    }
}
