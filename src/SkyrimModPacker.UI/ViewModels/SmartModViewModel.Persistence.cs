using System.IO;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    public Task InitializeAsync(string? projectPath = null) => RunAsync(async token =>
    {
        _gameData = (await _settings.LoadAsync(token)).SkyrimDataPath;
        var templates = Require(await _builder.GetWeaponTemplatesAsync(_gameData, token));
        Templates.Clear(); foreach (var template in templates) Templates.Add(template);
        _options = Require(await _builder.GetWeaponOptionsAsync(_gameData, token));
        OnPropertyChanged(nameof(Options)); OnPropertyChanged(nameof(Workbenches));
        OnPropertyChanged(nameof(FilteredLeveledLists));
        Types.Clear();
        foreach (var kind in templates.Select(t => t.Kind).Distinct())
        {
            var choice = new SmartModTypeRow(kind, Label("WeaponType_" + kind));
            choice.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(CanCreateProject)); OnPropertyChanged(nameof(SetupCountText)); };
            Types.Add(choice);
        }
        if (!string.IsNullOrWhiteSpace(projectPath)) await OpenCoreAsync(projectPath, token);
        NotifyStep();
    });

    [RelayCommand]
    private Task CreateProjectAsync() => RunAsync(async token =>
    {
        if (HasProject) throw new InvalidOperationException(Label("Smart_ProjectAlreadyOpen"));
        var planned = new List<(WeaponKind Kind, int Count)>();
        foreach (var row in Types)
        {
            if (!int.TryParse(row.Count, out var count) || count < 0 || count > 100)
                throw new InvalidOperationException(Label("Smart_InvalidQuantity"));
            planned.Add((row.Kind, count));
        }
        if (planned.Sum(p => p.Count) is 0 or > 100) throw new InvalidOperationException(Label("Smart_InvalidQuantity"));
        var project = Require(await _projects.CreateAsync(new()
        {
            PluginFileName = Name.Trim() + ".esp", OutputDirectory = OutputDirectory.Trim(), GameDataPath = _gameData
        }, token));
        _document = new() { ProjectFilePath = project.ProjectFilePath, PluginPath = project.PluginPath, Name = Name, EnableEsl = EnableEsl };
        HasProject = true;
        _applying = true;
        try
        {
            foreach (var (kind, count) in planned)
                for (var i = 0; i < count; i++) await ResolveFirstPersonAsync(AddItemCore(kind), token);
            SelectedItem = Items.First(); Section = SmartModSection.Weapons;
        }
        finally { _applying = false; }
        await RefreshBaselineAsync(token);
        await PersistAsync(token);
        OnPropertyChanged(nameof(ProjectPath)); OnPropertyChanged(nameof(PluginPath)); OnPropertyChanged(nameof(ProjectDirectory));
        NotifyStep(); PreviewChanged?.Invoke();
    });

    public Task OpenAsync(string path) => RunAsync(token => OpenCoreAsync(path, token));
    private async Task OpenCoreAsync(string path, CancellationToken token)
    {
        if (HasProject) await PersistAsync(token);
        var project = Require(await _projects.OpenAsync(path, _gameData, token));
        var loaded = await _drafts.LoadAsync(project.ProjectFilePath, _gameData, token);
        // Keep enough context for the explicit backup recovery action after a corrupt primary.
        _document = new() { ProjectFilePath = project.ProjectFilePath, PluginPath = project.PluginPath, Name = project.Name };
        HasProject = true;
        if (loaded.IsFailure) throw new InvalidOperationException(loaded.Error + Environment.NewLine + Label("Smart_UseBackup"));
        var document = loaded.Value;
        if (document == null) document = _document;
        document = await MergeConfirmedItemsAsync(document, token);
        ApplyDocument(document);
        if (_document.SourceRevision == null) await RefreshBaselineAsync(token);
        await RefreshResourcesAsync(token);
        SaveStatus = Label("Smart_Restored");
        OnPropertyChanged(nameof(ProjectPath)); OnPropertyChanged(nameof(PluginPath)); OnPropertyChanged(nameof(ProjectDirectory));
        PreviewChanged?.Invoke();
    }

    private async Task<SmartModDraft> MergeConfirmedItemsAsync(SmartModDraft document, CancellationToken token)
    {
        var items = document.Items.ToList();
        var weapons = Require(await _builder.GetWeaponsAsync(document.PluginPath, token));
        foreach (var weapon in weapons)
        {
            var recipes = Require(await _builder.GetRecipesAsync(document.PluginPath, weapon.FormId, token));
            var existing = items.FindIndex(i => i.ConfirmedFormId == weapon.FormId || i.ConfirmedFormId == null &&
                i.Weapon.EditorId == "SMP_" + i.Id.ToString("N") && i.Weapon.EditorId.Equals(weapon.EditorId, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var item = items[existing];
                var mergedRecipes = item.Recipes.ToList();
                foreach (var recipe in recipes)
                {
                    var index = mergedRecipes.FindIndex(r => r.ConfirmedFormId == recipe.FormId || r.ConfirmedFormId == null &&
                        r.EditorId == "SMP_Recipe_" + r.Id.ToString("N") && r.EditorId.Equals(recipe.EditorId, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) mergedRecipes[index] = mergedRecipes[index] with { ConfirmedFormId = recipe.FormId };
                    else mergedRecipes.Add(FromRecipe(recipe));
                }
                items[existing] = item with { ConfirmedFormId = weapon.FormId, Recipes = mergedRecipes };
                continue;
            }
            var origin = Require(await _projects.GetTemplateReferenceAsync(document.ProjectFilePath, _gameData, weapon.FormId, token));
            var models = Require(await _builder.GetWeaponModelsAsync(document.PluginPath, _gameData, weapon.FormId, token));
            items.Add(new()
            {
                Kind = weapon.Kind, ConfirmedFormId = weapon.FormId, Template = origin, State = SmartModItemState.Ready,
                Weapon = Fields(weapon.EditorId, weapon.Name, weapon.Damage, weapon.Weight, weapon.Value, weapon.Details) with
                { FirstPersonPath = models.FirstPersonPath ?? "", FirstPersonReference = models.FirstPersonReference },
                Recipes = recipes.Select(FromRecipe).ToArray()
            });
        }
        return document with { Items = items };
    }
    private static SmartModRecipeDraft FromRecipe(CraftingRecipe recipe) => new()
    {
        ConfirmedFormId = recipe.FormId, EditorId = recipe.EditorId, IsTempering = recipe.IsTempering, OutputCount = recipe.OutputCount.ToString(),
        Workbench = recipe.Workbench, RequiredPerk = recipe.RequiredPerk, TemperingPerk = recipe.TemperingPerk,
        Ingredients = recipe.Ingredients.Select(i => new SmartModIngredientDraft(i.Reference, i.Count.ToString())).ToArray()
    };

    private void ApplyDocument(SmartModDraft document)
    {
        _applying = true;
        try
        {
            _document = document; Items.Clear();
            foreach (var draft in document.Items) Attach(new(draft, Label));
            Name = document.Name; EnableEsl = document.EnableEsl; LastArchivePath = document.LastArchivePath;
            SelectedLeveledList = Options.LeveledLists.FirstOrDefault(l => l.Reference == document.PendingLeveledList);
            ListLevel = document.PendingListLevel; ListCount = document.PendingListCount;
            SelectedItem = Items.FirstOrDefault(i => i.Id == document.SelectedItemId) ?? Items.FirstOrDefault();
            SelectedRecipe = SelectedItem?.Recipes.FirstOrDefault(r => r.Id == document.SelectedRecipeId) ?? SelectedItem?.Recipes.FirstOrDefault();
            Section = document.Section;
        }
        finally { _applying = false; }
        NotifyStep();
    }

    private SmartModDraft Capture() => _document with
    {
        Name = Name, EnableEsl = EnableEsl, LastArchivePath = LastArchivePath, Section = Section, SelectedItemId = SelectedItem?.Id,
        PendingLeveledList = SelectedLeveledList?.Reference, PendingListLevel = ListLevel, PendingListCount = ListCount,
        SelectedRecipeId = SelectedRecipe?.Id, SelectedResourcePath = SelectedResource?.RelativePath,
        Items = Items.Select(i => i.Capture()).ToArray()
    };
    private async Task PersistAsync(CancellationToken token)
    {
        if (!HasProject) return;
        _autosave.Cancel();
        var generation = _editGeneration;
        var snapshot = Capture();
        var receipt = Require(await _drafts.SaveAsync(snapshot, _gameData, _document.Revision, token));
        _document = _document with { Revision = receipt.Revision, SavedAtUtc = receipt.SavedAtUtc };
        SaveStatus = Label("Smart_Saved") + " " + receipt.SavedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        if (generation != _editGeneration) _autosave.Schedule(async () => await SaveDraftAsync());
    }
    public async Task<bool> SaveDraftAsync()
    {
        if (_disposed) return false;
        if (IsBusy) { _autosave.Schedule(async () => await SaveDraftAsync()); return false; }
        var saved = false;
        await RunAsync(async token => { await PersistAsync(token); saved = true; });
        if (!saved) SaveStatus = Label("Smart_SaveFailed");
        return saved;
    }
    [RelayCommand] private async Task SaveDraftNowAsync() => await SaveDraftAsync();

    [RelayCommand]
    private Task RecoverBackupAsync() => RunAsync(async token =>
    {
        if (!HasProject) return;
        var recovered = Require(await _drafts.RestoreBackupAsync(ProjectPath, _gameData, token));
        ApplyDocument(await MergeConfirmedItemsAsync(recovered, token));
        SaveStatus = Label("Smart_Restored"); PreviewChanged?.Invoke();
    });

    private WorkspaceContext Context => new(PluginPath, ProjectPath, _gameData);
    private async Task RefreshBaselineAsync(CancellationToken token)
    {
        if (_history != null) _document = _document with { SourceRevision = await _history.InspectAsync(Context, true, token) };
    }
    private async Task<WorkspaceRevision?> CheckWorkspaceAsync(CancellationToken token)
    {
        if (_writeBlocked) throw new IOException(Label("Smart_RollbackFailed"));
        if (_history == null) return null;
        var current = await _history.InspectAsync(Context, true, token);
        if (_document.SourceRevision == null || !_document.SourceRevision.Matches(current))
            throw new IOException(Label("Smart_ExternalChanged"));
        return current;
    }
    [RelayCommand]
    private Task ReviewExternalChangesAsync() => RunAsync(async token =>
    {
        if (!HasProject) return;
        // This explicit action accepts the current disk generation while retaining all draft fields.
        var records = Require(await _builder.GetWeaponsAsync(PluginPath, token));
        if (Items.Any(i => i.ConfirmedFormId != null && records.All(r => r.FormId != i.ConfirmedFormId || r.Kind != i.Kind)))
            throw new IOException(Label("Smart_RecordMissing"));
        ApplyDocument(await MergeConfirmedItemsAsync(Capture(), token));
        await RefreshBaselineAsync(token); await PersistAsync(token);
        _writeBlocked = false;
        Status = Label("Smart_ExternalReviewed");
    });
}
