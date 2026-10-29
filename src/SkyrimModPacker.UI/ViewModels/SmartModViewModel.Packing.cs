using System.IO;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Archivers;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    private WeaponDetails Details(SmartModItemRow item)
    {
        SmartModItemRow.Number(item.Speed, out var speed); SmartModItemRow.Number(item.Reach, out var reach);
        var w = item.Fields;
        return new()
        {
            Description = w.Description, Speed = speed, Reach = reach, ModelPath = w.ModelPath, Keywords = w.Keywords,
            AttackSound = w.Sound, Sounds = w.Sounds, Enchantment = w.Enchantment, EnchantmentCharge = ushort.Parse(w.EnchantmentCharge),
            FirstPersonModelPath = w.FirstPersonEdited ? w.FirstPersonPath : null,
            FirstPersonModel = w.RestoreFirstPerson ? w.FirstPersonReference : null,
            ClearFirstPersonModel = w.RestoreFirstPerson && w.FirstPersonReference == null
        };
    }
    public string RecipeValidationText => SelectedRecipe == null || RecipeIsValid(SelectedRecipe.Capture()) ? "" : Label("Smart_InvalidRecipe");
    private bool RecipeIsValid(SmartModRecipeDraft data)
    {
        var bench = Options.Keywords.FirstOrDefault(k => k.Reference == data.Workbench);
        var tempering = bench?.EditorId == "CraftingSmithingSharpeningWheel";
        return data.Workbench != null && bench != null && tempering == data.IsTempering && ushort.TryParse(data.OutputCount, out var output) && output > 0 && (!tempering || output == 1) &&
            data.EditorId.Length is > 0 and <= 128 && char.IsAsciiLetter(data.EditorId[0]) && data.EditorId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
            data.Ingredients.Count > 0 && data.Ingredients.All(i => i.Reference != null && int.TryParse(i.Count, out var count) && count > 0) &&
            data.Ingredients.Select(i => i.Reference).Distinct().Count() == data.Ingredients.Count;
    }
    private bool ValidateRecipes(SmartModItemRow item)
    {
        var valid = true;
        if (item.Recipes.GroupBy(r => r.IsTempering).Any(g => g.Count() > 1))
        {
            Issues.Add(new(item.Name + ": " + Label("Smart_RecipeConflict"), item.Id, item.Recipes.First().Id)); valid = false;
        }
        foreach (var recipe in item.Recipes)
        {
            var data = recipe.Capture();
            var good = RecipeIsValid(data);
            if (!good) { Issues.Add(new(item.Name + ": " + Label("Smart_InvalidRecipe"), item.Id, recipe.Id)); valid = false; }
        }
        return valid;
    }
    private async Task ValidateModelsAsync(SmartModItemRow item, CancellationToken token)
    {
        foreach (var model in new[] { item.ModelPath, item.FirstPersonPath }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = model.Replace('\\', '/'); if (path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) path = path[7..];
            PackingPathGuard.ValidateEntries(["meshes/" + path]);
            if (!path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) throw new IOException(Label("Wizard_InvalidModel"));
            var info = Require(await _projects.InspectModelAsync(ProjectPath, model, _gameData, true, token));
            if (info.Availability != ModelAvailability.Found) throw new IOException(model + ": " + (info.Detail ?? Label("Smart_ModelMissing")));
            if (info.Origin == ModelOrigin.Project && !info.IncludedInZip) throw new IOException(model + ": " + Label("Smart_ModelUnregistered"));
        }
        if (item.Fields.FirstPersonEdited && string.IsNullOrWhiteSpace(item.FirstPersonPath)) throw new IOException(Label("Smart_FirstPersonRequired"));
        if (item.Capture().ValidateFolderDependencies)
        {
            var dependencies = Require(await _projects.ValidateWeaponFolderAssetsAsync(ProjectPath, [item.ModelPath, item.FirstPersonPath], _gameData, token));
            if (dependencies.Count > 0) throw new IOException(Label("Smart_FolderDependenciesPending") + Environment.NewLine + string.Join(Environment.NewLine, dependencies));
        }
    }

    [RelayCommand] private Task SaveItemAsync() => RunAsync(async token => { if (SelectedItem != null) await SaveItemCoreAsync(SelectedItem, token); });
    private async Task SaveItemCoreAsync(SmartModItemRow item, CancellationToken token)
    {
        Issues.Clear();
        if (IdentifierConflict(item) is { Length: > 0 } conflict)
        { SelectedItem = item; item.Step = SmartModItemStep.NameAndTemplate; throw new InvalidOperationException(conflict); }
        if (!item.Validate(Label) || !ValidateRecipes(item))
        {
            SelectedItem = item;
            item.Step = item.Errors.ContainsKey(nameof(item.Name)) || item.Errors.ContainsKey(nameof(item.Template)) ? SmartModItemStep.NameAndTemplate :
                item.Errors.Count > 0 ? SmartModItemStep.Properties : SmartModItemStep.Acquisition;
            throw new InvalidOperationException(item.Error + Environment.NewLine + Label("Smart_FixErrors"));
        }
        await ResolveFirstPersonAsync(item, token);
        try { await ValidateModelsAsync(item, token); }
        catch { SelectedItem = item; item.Step = SmartModItemStep.Appearance; throw; }
        // Persist identities before the first plugin write; retries can recognize each generated EditorID.
        await PersistAsync(token);
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        var checkpoint = _history == null ? null : await _history.CaptureAsync(Context, true, token: token);
        var before = Capture();
        try
        {
            var records = Require(await _builder.GetWeaponsAsync(PluginPath, token));
            var form = item.ConfirmedFormId;
            if (form == null && item.EditorId == "SMP_" + item.Id.ToString("N"))
                form = records.SingleOrDefault(r => r.EditorId.Equals(item.EditorId, StringComparison.OrdinalIgnoreCase))?.FormId;
            if (form != null && records.All(r => r.FormId != form)) throw new IOException(Label("Smart_RecordMissing"));
            SmartModItemRow.Number(item.Weight, out var weight);
            var weapon = form == null ? Require(await _builder.CloneSwordAsync(new()
            {
                PluginPath = PluginPath, GameDataPath = _gameData, ProjectFilePath = ProjectPath,
                TemplateFormId = item.Template!.FormId, TemplatePluginName = item.Template.PluginName,
                EditorId = item.EditorId, Name = item.Name, Damage = ushort.Parse(item.Damage), Weight = weight, Value = uint.Parse(item.Value), Details = Details(item)
            }, token)) : Require(await _builder.UpdateSwordAsync(new()
            {
                PluginPath = PluginPath, GameDataPath = _gameData, ProjectFilePath = ProjectPath,
                FormId = form.Value, EditorId = item.EditorId, Name = item.Name, Damage = ushort.Parse(item.Damage), Weight = weight, Value = uint.Parse(item.Value),
                Details = Details(item), VanillaTemplateFormId = item.Template?.FormId, VanillaTemplatePluginName = item.Template?.PluginName ?? "Skyrim.esm"
            }, token));
            item.Replace(item.Capture() with { ConfirmedFormId = weapon.FormId });
            await RecordSuccessfulWriteAsync(token);
            var savedRecipes = Require(await _builder.GetRecipesAsync(PluginPath, weapon.FormId, token));
            foreach (var removed in item.Tuning?.PendingRecipeRemovals ?? [])
                if (savedRecipes.Any(r => r.FormId == removed))
                {
                    var deleted = await _builder.DeleteRecipeAsync(PluginPath, _gameData, removed, token);
                    if (deleted.IsFailure) throw new IOException(deleted.Error);
                    await RecordSuccessfulWriteAsync(token);
                }
            foreach (var recipe in item.Recipes)
            {
                var data = recipe.Capture();
                var recipeForm = recipe.ConfirmedFormId;
                if (recipeForm == null && data.EditorId == "SMP_Recipe_" + data.Id.ToString("N"))
                    recipeForm = savedRecipes.SingleOrDefault(r => r.EditorId.Equals(data.EditorId, StringComparison.OrdinalIgnoreCase))?.FormId;
                var saved = Require(await _builder.SaveRecipeAsync(new()
                {
                    PluginPath = PluginPath, GameDataPath = _gameData, Recipe = new()
                    {
                        FormId = recipeForm, WeaponFormId = weapon.FormId, EditorId = data.EditorId, IsTempering = data.IsTempering,
                        OutputCount = ushort.Parse(data.OutputCount), Workbench = data.Workbench!, RequiredPerk = data.RequiredPerk,
                        TemperingPerk = data.TemperingPerk,
                        Ingredients = data.Ingredients.Select(i => new RecipeIngredient { Reference = i.Reference!, Count = int.Parse(i.Count) }).ToArray()
                    }
                }, token));
                recipe.ConfirmedFormId = saved.FormId;
                await RecordSuccessfulWriteAsync(token);
            }
            if (item.Tuning != null) item.Replace(item.Capture() with { Tuning = item.Tuning with { PendingRecipeRemovals = [] } });
            item.SetState(SmartModItemState.Ready); await PersistAsync(token); await RefreshResourcesAsync(token);
            Status = Label("Smart_ItemSaved");
        }
        catch (Exception failure)
        {
            if (checkpoint != null && expectation?.HasWrites == true)
            {
                try
                {
                    EnsureExpectedWorkspaceUnchanged();
                    var expectedRevision = await _history!.InspectAsync(Context, true, CancellationToken.None);
                    EnsureExpectedWorkspaceUnchanged();
                    var expected = await _history.CaptureAsync(Context, true, token: CancellationToken.None);
                    if (!expected.Revision.Matches(expectedRevision)) throw new IOException(Label("Smart_ExternalChanged"));
                    await _history.RestoreAsync(checkpoint, expected, token: CancellationToken.None);
                    var currentDraftRevision = _document.Revision;
                    ApplyDocument(before with { Revision = currentDraftRevision });
                    await RefreshBaselineAsync(CancellationToken.None); await PersistAsync(CancellationToken.None);
                    Status = Label("Smart_RolledBack");
                }
                catch (Exception rollback)
                {
                    _writeBlocked = true;
                    throw new IOException(failure.Message + Environment.NewLine + Label("Smart_RollbackFailed") + " " + rollback.Message, failure);
                }
            }
            throw;
        }
    }
    private void EnsureExpectedWorkspaceUnchanged()
    {
        foreach (var path in _document.SourceRevision?.Files.Keys ?? [])
            WorkspaceWriteExpectation.Check(PackingPathGuard.ResolveUnderRoot(ProjectDirectory, path));
    }
    private async Task RecordSuccessfulWriteAsync(CancellationToken token)
    {
        EnsureExpectedWorkspaceUnchanged();
        WorkspaceWriteExpectation.Check(PluginPath); WorkspaceWriteExpectation.Check(ProjectPath);
        await RefreshBaselineAsync(token); await PersistAsync(token);
    }

    [RelayCommand]
    private Task CheckAsync() => RunAsync(async token =>
    {
        Section = SmartModSection.Checks;
        await CheckCoreAsync(token);
    });
    private async Task<bool> CheckCoreAsync(CancellationToken token)
    {
        Issues.Clear();
        if (!HasProject || Items.Count == 0) { Issues.Add(new(Label("Smart_InvalidQuantity"))); return false; }
        foreach (var item in Items)
        {
            if (IdentifierConflict(item) is { Length: > 0 } idIssue) Issues.Add(new(item.Name + ": " + idIssue, item.Id, Step: SmartModItemStep.NameAndTemplate));
            if (!item.Validate(Label)) Issues.Add(new(item.Name + ": " + item.Error, item.Id));
            ValidateRecipes(item);
            try { await ValidateModelsAsync(item, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Issues.Add(new(item.Name + ": " + ex.Message, item.Id, Step: SmartModItemStep.Appearance)); }
        }
        if (Items.Select(i => i.EditorId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Items.Count)
            Issues.Add(new(Label("Smart_DuplicateIds")));
        await RefreshResourcesAsync(token);
        foreach (var row in Resources.Where(r => r.Entry.BlocksPacking)) Issues.Add(new(row.RelativePath + ": " + row.IssueText, ResourcePath: row.RelativePath));
        var diagnostics = await _validator.ValidateAsync(PluginPath, _gameData, cancellationToken: token);
        var confirmedRecords = Require(await _builder.GetWeaponsAsync(PluginPath, token));
        foreach (var item in Items.Where(i => i.ConfirmedFormId != null && confirmedRecords.All(r => r.FormId != i.ConfirmedFormId || r.Kind != i.Kind)))
            Issues.Add(new(item.Name + ": " + Label("Smart_RecordMissing"), item.Id));
        foreach (var diagnostic in diagnostics)
        {
            var item = Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(diagnostic.Record) &&
                (diagnostic.Record.Contains(i.EditorId, StringComparison.OrdinalIgnoreCase) || i.ConfirmedFormId != null && diagnostic.Record.Contains(i.ConfirmedFormId.Value.ToString("X6"), StringComparison.OrdinalIgnoreCase)));
            var recipe = Items.SelectMany(i => i.Recipes.Select(r => (Item: i, Recipe: r))).FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(diagnostic.Record) && (diagnostic.Record.Contains(p.Recipe.EditorId, StringComparison.OrdinalIgnoreCase) ||
                    p.Recipe.ConfirmedFormId != null && diagnostic.Record.Contains(p.Recipe.ConfirmedFormId.Value.ToString("X6"), StringComparison.OrdinalIgnoreCase)));
            Issues.Add(new(diagnostic.ToString(), recipe.Item?.Id ?? item?.Id, recipe.Recipe?.Id, IsError: diagnostic.Severity == DiagnosticSeverity.Error));
        }
        foreach (var item in Items.Where(i => Issues.Any(issue => issue.IsError && issue.ItemId == i.Id))) item.SetState(SmartModItemState.NeedsCorrection);
        Issues.Add(new(Label("Smart_PreviewLimits"), IsError: false));
        await PersistAsync(token);
        return Issues.All(i => !i.IsError);
    }

    [RelayCommand]
    private Task PackAsync() => RunAsync(async token =>
    {
        if (_pipeline == null) throw new InvalidOperationException(Label("Smart_PackerUnavailable"));
        if (CreateTextureVariant && !File.Exists(VariantTexconvPath)) throw new FileNotFoundException(Label("Smart_VariantTexconvMissing"), VariantTexconvPath);
        if (!await CheckCoreAsync(token)) { Section = SmartModSection.Checks; return; }
        foreach (var item in Items.ToArray()) await SaveItemCoreAsync(item, token);
        if (!await CheckCoreAsync(token)) { Section = SmartModSection.Checks; return; }
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        var result = await _pipeline.ExecuteAsync(new()
        {
            Name = Path.GetFileNameWithoutExtension(PluginPath), PluginFileName = Path.GetFileName(PluginPath),
            PluginSourcePath = PluginPath, WeaponProjectPath = ProjectPath, EnableEsl = EnableEsl, IncludeExportReport = true
        }, await _settings.LoadAsync(token), cancellationToken: token);
        if (!result.IsSuccess) throw new IOException(string.Join(Environment.NewLine, result.Errors));
        LastArchivePath = result.OutputArchivePath; LastVariantArchivePath = null; Section = SmartModSection.Finished;
        foreach (var warning in result.Warnings) Issues.Add(new(warning, IsError: false));
        await PersistAsync(token);
        if (CreateTextureVariant)
        {
            try { await CreateVariantCoreAsync(token); }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new IOException(Label("Smart_VariantFailed") + " " + error.Message, error); }
        }
        Status = Label("Smart_Packed");
    });
    [RelayCommand]
    private Task CreateVariantAsync() => RunAsync(async token =>
    {
        if (string.IsNullOrWhiteSpace(LastArchivePath)) throw new InvalidOperationException(Label("Smart_VariantNoArchive"));
        await CreateVariantCoreAsync(token);
        Status = Label("Smart_VariantReady");
    });
    private async Task CreateVariantCoreAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(LastArchivePath)) throw new InvalidOperationException(Label("Smart_VariantNoArchive"));
        LastVariantArchivePath = await TextureVariantArchiveService.CreateAsync(LastArchivePath, VariantMaxTextureSize,
            VariantTexconvPath, _gameData, token);
        await PersistAsync(token);
    }
    [RelayCommand] private void OpenArchiveFolder() { if (LastArchivePath != null) _opener?.Open(Path.GetDirectoryName(LastArchivePath)!); }
    [RelayCommand]
    private async Task ReadReportAsync()
    {
        if (LastArchivePath == null) return;
        await RunAsync(async token =>
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(LastArchivePath);
            var report = zip.GetEntry("mod-report.json") ?? throw new IOException(Label("Smart_ReportMissing"));
            using var reader = new StreamReader(report.Open());
            Status = await reader.ReadToEndAsync(token);
        });
    }

    private async Task RefreshResourcesAsync(CancellationToken token)
    {
        if (!HasProject) return;
        var catalog = Require(await _projects.GetResourceCatalogAsync(ProjectPath, _gameData, token));
        Dependencies = string.Join(", ", Require(await _builder.GetMastersAsync(PluginPath, token)));
        var selected = SelectedResource?.RelativePath ?? _document.SelectedResourcePath;
        Resources.Clear(); foreach (var entry in catalog.Entries) Resources.Add(new(entry, (key, fallback) => _localization?.GetString(key) ?? fallback));
        SelectedResource = Resources.FirstOrDefault(r => r.RelativePath == selected);
        foreach (var diagnostic in catalog.Diagnostics) Issues.Add(new(diagnostic.Record + ": " + diagnostic.Detail));
        OnPropertyChanged(nameof(Summary));
    }
    public Task ImportModelAsync(string source, bool firstPerson) => RunAsync(async token =>
    {
        if (SelectedItem == null || !HasProject) return;
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        var path = Require(await _projects.ImportModelAsync(ProjectPath, source, _gameData, token));
        if (firstPerson) SelectedItem.FirstPersonPath = path; else SelectedItem.ModelPath = path;
        await RecordSuccessfulWriteAsync(token); await RefreshResourcesAsync(token);
    });
    public async Task<TextureFolderPlan> PreviewTextureFolderAsync(string source, string mapping) =>
        Require(await _projects.PreviewTextureFolderAsync(ProjectPath, source, mapping, _gameData, _lifetime.Token));
    public Task ImportTextureFolderAsync(TextureFolderPlan plan) => RunAsync(async token =>
    {
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        Require(await _projects.ImportTextureFolderAsync(plan, _gameData, token));
        await RecordSuccessfulWriteAsync(token); await RefreshResourcesAsync(token); PreviewChanged?.Invoke();
    });
    public Task ImportTexturesAsync(IReadOnlyList<string> sources) => RunAsync(async token =>
    {
        if (!HasProject) return;
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        Require(await _projects.ImportTexturesAsync(ProjectPath, sources, _gameData, cancellationToken: token));
        await RecordSuccessfulWriteAsync(token); await RefreshResourcesAsync(token); PreviewChanged?.Invoke();
    });
    public Task ReplaceResourceAsync(string source) => RunAsync(async token =>
    {
        if (SelectedResource == null || SelectedResource.Entry.Origin != ModelOrigin.Project) return;
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        Require(await _projects.ReplaceResourceAsync(ProjectPath, SelectedResource.RelativePath, source, _gameData, token));
        await RecordSuccessfulWriteAsync(token); await RefreshResourcesAsync(token); PreviewChanged?.Invoke();
    });
    public Task DeleteResourceAsync() => RunAsync(async token =>
    {
        if (SelectedResource == null || SelectedResource.Entry.Origin != ModelOrigin.Project) return;
        var resource = SelectedResource.RelativePath;
        string Mesh(string path) => "meshes/" + path.Replace('\\', '/').Replace("meshes/", "", StringComparison.OrdinalIgnoreCase);
        if (Items.Any(i => new[] { i.ModelPath, i.FirstPersonPath }.Any(p => Mesh(p).Equals(resource, StringComparison.OrdinalIgnoreCase))) ||
            resource.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) && Items.Any(i => !string.IsNullOrWhiteSpace(i.ModelPath)))
            throw new IOException(Label("Smart_ResourceInUse"));
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        Require(await _projects.DeleteResourceAsync(ProjectPath, resource, _gameData, token));
        await RecordSuccessfulWriteAsync(token); await RefreshResourcesAsync(token);
    });
    [RelayCommand]
    private Task AddToListAsync() => RunAsync(async token =>
    {
        if (SelectedItem is not { } item || SelectedLeveledList == null || !short.TryParse(ListLevel, out var level) || level < 1 || !short.TryParse(ListCount, out var count) || count <= 0)
            throw new InvalidOperationException(Label("Smart_SaveBeforeList"));
        if (item.ConfirmedFormId == null || item.Capture().State != SmartModItemState.Ready) await SaveItemCoreAsync(item, token);
        var form = item.ConfirmedFormId ?? throw new InvalidOperationException(Label("Smart_SaveBeforeList"));
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        var result = await _builder.AddToLeveledListAsync(new()
        {
            PluginPath = PluginPath, GameDataPath = _gameData, WeaponFormId = form, List = SelectedLeveledList.Reference, Level = level, Count = count, UpdateExisting = true
        }, token);
        if (result.IsFailure) throw new IOException(result.Error);
        await RecordSuccessfulWriteAsync(token);
        await RefreshSelectedListAsync(); Status = Label("Smart_ListSaved");
    });
}
