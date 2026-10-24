using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.Application.Services;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>Workspace plugin creation and editing of cloned vanilla one-handed swords.</summary>
public sealed partial class WeaponsViewModel : ViewModelBase
{
    private readonly IPluginBuilderService _builder;
    private readonly ISettingsRepository _settings;
    private readonly IProfileRepository _profiles;
    private readonly IWeaponProjectService _projects;
    private readonly PackingPipeline? _packingPipeline;
    private readonly ILocalizationService? _localization;
    private readonly ILogger<WeaponsViewModel>? _logger;
    private CancellationTokenSource? _operationCts;
    private string _templateGameDataPath = string.Empty;
    private bool _synchronizingWeaponTemplate;

    public IReadOnlyList<string> PluginExtensions { get; } = [".esp", ".esl"];

    [ObservableProperty] private string _pluginName = "MySwords";
    [ObservableProperty] private string _pluginExtension = ".esp";
    [ObservableProperty] private string _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimModPacker", "Plugins");
    [ObservableProperty] private string _mastersText = "Skyrim.esm";
    [ObservableProperty] private string _pluginPath = string.Empty;
    [ObservableProperty] private bool _showCreationPanel = true;
    [ObservableProperty] private string _gameDataPath = string.Empty;
    [ObservableProperty] private ObservableCollection<SwordTemplate> _templates = [];
    [ObservableProperty] private SwordTemplate? _selectedTemplate;
    [ObservableProperty] private ObservableCollection<WeaponRecord> _weapons = [];
    [ObservableProperty] private WeaponRecord? _selectedWeapon;
    [ObservableProperty] private string _editorId = string.Empty;
    [ObservableProperty] private string _weaponName = string.Empty;
    [ObservableProperty] private string _damage = "0";
    [ObservableProperty] private string _weight = "0";
    [ObservableProperty] private string _value = "0";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private ObservableCollection<ModProfile> _packingProfiles = [];
    [ObservableProperty] private ModProfile? _selectedPackingProfile;

    public WeaponsViewModel(IPluginBuilderService pluginBuilderService, ISettingsRepository settingsRepository,
        IProfileRepository profileRepository, IWeaponProjectService projectService,
        ILocalizationService? localizationService = null, ILogger<WeaponsViewModel>? logger = null, PackingPipeline? packingPipeline = null,
        IUnsavedChangesPrompt? unsavedChangesPrompt = null, IWeaponDraftRepository? draftRepository = null,
        IDraftAutosaveScheduler? draftAutosaveScheduler = null, IWorkspaceHistoryService? workspaceHistory = null,
        IWorkspaceConflictPrompt? conflictPrompt = null, IWorkspaceFolderOpener? folderOpener = null, IWeaponPresetRepository? presetRepository = null,
        IPluginValidationService? pluginValidator = null, IModelPreviewPresenter? previewPresenter = null)
    {
        _builder = pluginBuilderService;
        _settings = settingsRepository;
        _profiles = profileRepository;
        _projects = projectService;
        _packingPipeline = packingPipeline;
        _unsavedChangesPrompt = unsavedChangesPrompt;
        _drafts = draftRepository;
        _draftScheduler = draftAutosaveScheduler;
        _workspaceHistory = workspaceHistory;
        _conflictPrompt = conflictPrompt;
        _folderOpener = folderOpener;
        _presets = presetRepository;
        _pluginValidator = pluginValidator;
        _previewPresenter = previewPresenter;
        _localization = localizationService;
        if (_localization != null) _localization.LanguageChanged += RenderResourceCatalog;
        if (_localization != null) _localization.LanguageChanged += RefreshStage4Texts;
        if (_localization != null) _localization.LanguageChanged += RefreshBatchLabels;
        _logger = logger;
        PageTitle = "Weapons";
        WeaponKeywords.CollectionChanged += OnDraftKeywordsChanged;
        RecipeIngredients.CollectionChanged += DraftCollectionChanged;
        AcceptWeaponDraft(); AcceptRecipeDraft();
        NotifyInheritance();
        OnPropertyChanged(nameof(TemplateSelection));
    }

    [RelayCommand]
    private Task InitializeAsync() => RunOperationAsync(async token =>
    {
        var configuredData = (await _settings.LoadAsync(token)).SkyrimDataPath;
        if (HasUnsavedChanges && !string.Equals(configuredData, GameDataPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Text("Unsaved_DataChanged", "Save or discard your edits before changing the Data folder."));
        GameDataPath = configuredData;
        var selectedId = SelectedPackingProfile?.Id;
        PackingProfiles = new ObservableCollection<ModProfile>(await _profiles.LoadAllAsync(token));
        SelectedPackingProfile = PackingProfiles.FirstOrDefault(p => p.Id == selectedId);
        if (!IsBaseConnected || !string.Equals(GameDataPath, _templateGameDataPath, StringComparison.OrdinalIgnoreCase))
            await LoadTemplatesCoreAsync(token, selectFirst: string.IsNullOrWhiteSpace(EditorId));
    });

    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        if (IsLoading) return;
        var dialog = new OpenFolderDialog { Title = Text("Weapons_Workspace", "Plugin workspace") };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private Task LoadTemplatesAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
    {
        var settings = await _settings.LoadAsync(token);
        GameDataPath = settings.SkyrimDataPath;
        await LoadTemplatesCoreAsync(token, selectFirst: SelectedWeapon == null);
        if (SelectedWeapon != null) OnSelectedWeaponChanged(SelectedWeapon);
        StatusMessage = Text("Weapons_TemplatesLoaded", "Sword templates loaded.");
    }));

    [RelayCommand]
    private Task CreatePluginAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
    {
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        if (!ValidateProjectFields()) return;
        var result = await _projects.CreateAsync(new CreatePluginRequest
        {
            PluginFileName = PluginName.Trim() + PluginExtension,
            OutputDirectory = OutputDirectory.Trim(),
            GameDataPath = GameDataPath,
            Masters = MastersText.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        }, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        ProjectFilePath = result.Value.ProjectFilePath;
        PluginPath = result.Value.PluginPath;
        ShowCreationPanel = false;
        await RefreshWeaponsAsync(null, token);
        await RefreshProjectResourcesAsync(token);
        await ConfirmedWorkspaceLoadedAsync(token);
        StatusMessage = Text("Weapons_PluginCreated", "Plugin created.");
    }));

    [RelayCommand]
    private async Task OpenPluginAsync(string? path)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFileDialog { Title = Text("Weapons_Open", "Open plugin"), Filter = "Skyrim plugins (*.esp;*.esl)|*.esp;*.esl" };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        await GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
        {
            var selectedPath = Path.GetFullPath(path);
            GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
            try { PluginWriteGuard.EnsureOutsideGame(selectedPath, GameDataPath); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(Text("Weapons_UseWorkingCopy", "Open a plugin outside the game, or use Create working copy for a supported ESP/ESL. Skyrim.esm is loaded automatically as a template source."), ex);
            }
            var result = await _builder.GetWeaponsAsync(selectedPath, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
            await LoadOptionsCoreAsync(token);
            PluginPath = selectedPath;
            ProjectFilePath = string.Empty;
            ShowCreationPanel = false;
            Weapons = new ObservableCollection<WeaponRecord>(result.Value);
            await RefreshWeaponOriginsAsync(token);
            SelectedWeapon = Weapons.FirstOrDefault();
            if (SelectedWeapon == null) ResetDraft();
            await ConfirmedWorkspaceLoadedAsync(token);
            StatusMessage = Text("Weapons_PluginOpened", "Plugin opened.");
        }));
    }

    partial void OnSelectedTemplateChanged(SwordTemplate? value)
    {
        if (value == null || _synchronizingWeaponTemplate) return;
        SelectedWeaponKind = value.Kind;
        BalanceTemplate = value;
        SelectedWeapon = null;
        FillEditor(SuggestEditorId(value.EditorId), value.Name, value.Damage, value.Weight, value.Value);
        FillDetails(value.Details);
        ResetRecipeEditor();
        AcceptWeaponDraft(); AcceptRecipeDraft();
        OnPropertyChanged(nameof(TemplateSelection));
    }

    partial void OnSelectedWeaponChanged(WeaponRecord? value)
    {
        if (value != null)
        {
            SelectedWeaponKind = value.Kind;
            _synchronizingWeaponTemplate = true;
            try { SelectedTemplate = InheritanceTemplate; }
            finally { _synchronizingWeaponTemplate = false; }
            OnPropertyChanged(nameof(TemplateSelection));
            BalanceTemplate = InheritanceTemplate ?? FilteredTemplates.FirstOrDefault();
            FillEditor(value.EditorId, value.Name, value.Damage, value.Weight, value.Value);
            FillDetails(value.Details);
        }
        if (!_preserveRecipeOnRefresh)
        {
            Recipes.Clear();
            SelectedRecipe = null;
            ResetRecipeEditor();
            AcceptRecipeDraft();
        }
        AcceptWeaponDraft();
        NotifyInheritance();
        OnPropertyChanged(nameof(WeaponSelection));
    }

    private void FillEditor(string editorId, string name, ushort damage, float weight, uint value)
    {
        EditorId = editorId;
        WeaponName = name;
        Damage = damage.ToString(CultureInfo.CurrentCulture);
        Weight = weight.ToString(CultureInfo.CurrentCulture);
        Value = value.ToString(CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private async Task NewSwordAsync()
    {
        if (IsLoading) return;
        if (SelectedTemplate == null)
        {
            TemplateError = Text("Weapons_SelectTemplate", "Select a vanilla sword template first.");
            return;
        }
        if (!await RequestLeaveEditorAsync()) return;
        OnSelectedTemplateChanged(SelectedTemplate);
        _newWeaponPending = true; UpdateUnsavedChanges();
        ResetEditorHistory();
    }

    [RelayCommand]
    private Task CloneSwordAsync() => RunOperationAsync(async token =>
    {
        RequirePlugin();
        if (SelectedTemplate == null) throw new InvalidOperationException(Text("Weapons_SelectTemplate", "Select a vanilla sword template first."));
        var stats = ParseStatistics();
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        if (!string.Equals(GameDataPath, _templateGameDataPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Text("Weapons_SelectTemplate", "Reload the vanilla sword templates first."));
        await EnsureImportedModelAvailableAsync(token);
        var result = await _builder.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = PluginPath, GameDataPath = GameDataPath, TemplateFormId = SelectedTemplate.FormId,
            TemplatePluginName = SelectedTemplate.PluginName,
            ProjectFilePath = string.IsNullOrWhiteSpace(ProjectFilePath) ? null : ProjectFilePath,
            EditorId = EditorId.Trim(), Name = WeaponName.Trim(), Damage = stats.Damage, Weight = stats.Weight, Value = stats.Value,
            Details = ParseDetails()
        }, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshWeaponsAsync(result.Value.FormId, token, preserveRecipe: true);
        StatusMessage = Text("Weapons_Cloned", "Sword cloned. A .bak copy was saved.");
    }, write: true);

    [RelayCommand]
    private Task SaveSwordAsync() => RunOperationAsync(async token =>
    {
        RequirePlugin();
        if (SelectedWeapon == null) throw new InvalidOperationException(Text("Weapons_SelectRecord", "Select a sword in the plugin first."));
        var stats = ParseStatistics();
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        await EnsureImportedModelAvailableAsync(token);
        var result = await _builder.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = PluginPath, GameDataPath = GameDataPath, FormId = SelectedWeapon.FormId,
            ProjectFilePath = string.IsNullOrWhiteSpace(ProjectFilePath) ? null : ProjectFilePath, VanillaTemplateFormId = _restorationOrigin,
            VanillaTemplatePluginName = _restorationPluginName,
            EditorId = EditorId.Trim(), Name = WeaponName.Trim(), Damage = stats.Damage, Weight = stats.Weight, Value = stats.Value,
            Details = ParseDetails()
        }, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshWeaponsAsync(result.Value.FormId, token, preserveRecipe: true);
        StatusMessage = Text("Weapons_Saved", "Changes saved. A .bak copy was saved.");
    }, write: true);

    [RelayCommand]
    private Task DeleteSwordAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
    {
        RequirePlugin();
        if (SelectedWeapon == null) throw new InvalidOperationException(Text("Weapons_SelectRecord", "Select a sword in the plugin first."));
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        var result = await _builder.DeleteSwordAsync(PluginPath, GameDataPath, SelectedWeapon.FormId, token, string.IsNullOrWhiteSpace(ProjectFilePath) ? null : ProjectFilePath, deleteRecipes: true);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshWeaponsAsync(null, token);
        StatusMessage = Text("Weapons_Deleted", "Sword deleted. A .bak copy was saved.");
    }, write: true));

    private async Task RefreshWeaponsAsync(uint? selectedId, CancellationToken token, bool preserveRecipe = false)
    {
        var result = await _builder.GetWeaponsAsync(PluginPath, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        Weapons = new ObservableCollection<WeaponRecord>(result.Value);
        await RefreshWeaponOriginsAsync(token);
        _preserveRecipeOnRefresh = preserveRecipe;
        try { SelectedWeapon = Weapons.FirstOrDefault(w => w.FormId == selectedId); }
        finally { _preserveRecipeOnRefresh = false; }
        if (SelectedWeapon == null) ResetDraft();
        if (!string.IsNullOrWhiteSpace(ProjectFilePath)) await RefreshProjectResourcesAsync(token);
    }

    private void ResetDraft()
    {
        if (SelectedTemplate != null) OnSelectedTemplateChanged(SelectedTemplate);
        else
        {
            FillEditor(string.Empty, string.Empty, 0, 0, 0);
            FillDetails(new WeaponDetails());
            AcceptWeaponDraft(); AcceptRecipeDraft();
        }
    }

    [RelayCommand]
    private Task CreatePackingProfileAsync() => SavePackingProfileAsync(createNew: true);

    [RelayCommand]
    private Task LinkPackingProfileAsync() => SavePackingProfileAsync(createNew: false);

    private Task SavePackingProfileAsync(bool createNew) => RunOperationAsync(async token =>
    {
        RequirePlugin();
        var savedProfiles = await _profiles.LoadAllAsync(token);
        var existing = createNew ? null : savedProfiles.FirstOrDefault(p => p.Id == SelectedPackingProfile?.Id);
        if (!createNew && existing == null) throw new InvalidOperationException(Text("Weapons_SelectProfile", "Select a packing profile first."));
        var profile = new ModProfile
        {
            Id = existing?.Id ?? Guid.NewGuid(), Name = existing?.Name ?? Path.GetFileNameWithoutExtension(PluginPath),
            Version = existing?.Version ?? "1.0", Keywords = existing?.Keywords.ToList() ?? [], EnableEsl = existing?.EnableEsl ?? false, IncludeExportReport = existing?.IncludeExportReport ?? false,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
            PluginFileName = Path.GetFileName(PluginPath), PluginSourcePath = PluginPath, WeaponProjectPath = ProjectFilePath
        };
        var validation = ModProfileValidator.Validate(profile);
        if (validation.IsFailure) throw new InvalidOperationException(validation.Error);
        await _profiles.SaveAsync(profile, token);
        PackingProfiles = new ObservableCollection<ModProfile>(await _profiles.LoadAllAsync(token));
        SelectedPackingProfile = PackingProfiles.FirstOrDefault(p => p.Id == profile.Id);
        StatusMessage = Text("Weapons_ProfileSaved", "Packing profile saved. Open Build, select the profile and scan.");
    });

    private (ushort Damage, float Weight, uint Value) ParseStatistics()
    {
        if (!ushort.TryParse(Damage, out var damage) || !uint.TryParse(Value, out var value) ||
            !(float.TryParse(Weight, NumberStyles.Float, CultureInfo.CurrentCulture, out var weight) ||
              float.TryParse(Weight, NumberStyles.Float, CultureInfo.InvariantCulture, out weight)) || !float.IsFinite(weight) || weight < 0)
            throw new InvalidOperationException(Text("Weapons_InvalidStats", "Enter valid damage (0–65535), weight (0 or higher) and value (0–4294967295)."));
        return (damage, weight, value);
    }

    private void RequirePlugin()
    {
        if (string.IsNullOrWhiteSpace(PluginPath) || !File.Exists(PluginPath))
            throw new InvalidOperationException(Text("Weapons_SelectPlugin", "Create or open a workspace plugin first."));
    }

    [RelayCommand]
    private void CancelOperation() => _operationCts?.Cancel();

    private async Task RunOperationAsync(Func<CancellationToken, Task> action, bool write = false, bool resourceWrite = false)
    {
        if (IsLoading) return;
        _draftScheduler?.Cancel();
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var draftGateAcquired = false;
        WorkspaceWriteExpectation? expectation = null;
        WorkspaceCheckpoint? beforeFiles = null;
        EditorMemento? beforeEditor = null;
        var historyEpoch = _historyEpoch;
        var previousContext = Workspace;
        try
        {
            await _draftIoGate.WaitAsync(_operationCts.Token);
            draftGateAcquired = true;
            beforeEditor = CaptureMemento();
            if (write && _workspaceHistory != null && !string.IsNullOrEmpty(PluginPath))
            {
                await EnsureWorkspaceReviewedAsync(_operationCts.Token);
                beforeEditor = CaptureMemento();
                beforeFiles = await _workspaceHistory.CaptureAsync(Workspace, resourceWrite, EditorProgress, _operationCts.Token);
                expectation = new WorkspaceWriteExpectation(_confirmedRevision!);
            }
            await action(_operationCts.Token);
        }
        catch (OperationCanceledException) { StatusMessage = Text("Weapons_Cancelled", "Operation cancelled."); }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            foreach (var key in ex.Data.Keys.Cast<object>().Where(k => k.ToString()?.Contains("Rollback", StringComparison.OrdinalIgnoreCase) == true))
                ErrorMessage += Environment.NewLine + key + ": " + ex.Data[key];
            _logger?.LogError(ex, "Weapons editor operation failed");
        }
        finally
        {
            var wrote = expectation?.HasWrites == true;
            expectation?.Dispose();
            if (wrote && _workspaceHistory != null && beforeFiles != null && beforeEditor != null)
            {
                try
                {
                    var after = await _workspaceHistory.CaptureAsync(Workspace, resourceWrite, EditorProgress, CancellationToken.None);
                    _confirmedRevision = await _workspaceHistory.InspectAsync(Workspace, true, CancellationToken.None);
                    if (!beforeFiles.Revision.Matches(after.Revision)) PushEditorHistory(new(beforeEditor, CaptureMemento(), beforeFiles, after));
                }
                catch (Exception ex) { ErrorMessage = Text("History_BackupFailed", "The write completed, but its history backup failed: ") + ex.Message; }
            }
            else if (!_replayingHistory && beforeEditor != null && historyEpoch == _historyEpoch && previousContext == Workspace &&
                     MemoryKey(beforeEditor) != MemoryKey(CaptureMemento())) PushEditorHistory(new(beforeEditor, CaptureMemento()));
            if (draftGateAcquired)
            {
                try
                {
                    if (EnableDraftAutosave || _draftRevisions.ContainsKey(PluginPath)) await PersistDraftUnderGateAsync();
                }
                finally { _draftIoGate.Release(); }
            }
            IsLoading = false;
            _lastEditorMemento = CaptureMemento();
            _replayingHistory = false;
            OperationProgress = null;
            OnPropertyChanged(nameof(WeaponSelection)); OnPropertyChanged(nameof(TemplateSelection)); OnPropertyChanged(nameof(RecipeSelection));
        }
    }

    private string Text(string key, string fallback)
    {
        var value = _localization?.GetString(key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}
