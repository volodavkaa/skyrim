using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private readonly IWorkspaceHistoryService? _workspaceHistory;
    private readonly IWorkspaceConflictPrompt? _conflictPrompt;
    private WorkspaceRevision? _confirmedRevision;
    private bool _requireExternalReview, _recoveredWeaponPending, _recoveredRecipePending, _applyingEditorState;
    [ObservableProperty] private ObservableCollection<DraftRecoveryCandidate> _recoveryCandidates = [];
    [ObservableProperty] private DraftRecoveryCandidate? _selectedRecoveryCandidate;
    [ObservableProperty] private string? _recoveryMessage;
    [ObservableProperty] private string? _operationProgress;

    private WorkspaceContext Workspace => new(PluginPath, ProjectFilePath, GameDataPath);
    private IProgress<EditorOperationProgress> EditorProgress => new Progress<EditorOperationProgress>(p => OperationProgress = $"{p.Completed}/{p.Total}: {p.Path}");

    private async Task ConfirmedWorkspaceLoadedAsync(CancellationToken token)
    {
        _confirmedRevision = _workspaceHistory == null ? null : await _workspaceHistory.InspectAsync(Workspace, true, token);
        _requireExternalReview = false;
        ResetEditorHistory();
        await FindRecoveryCoreAsync(token);
    }

    private async Task EnsureWorkspaceReviewedAsync(CancellationToken token)
    {
        if (_workspaceHistory == null) return;
        var current = await _workspaceHistory.InspectAsync(Workspace, true, token);
        if (_requireExternalReview || (_confirmedRevision != null && !_confirmedRevision.Matches(current)))
        {
            var differences = _confirmedRevision == null ? Text("Recovery_UnknownBaseline", "The draft has no verified source fingerprint.") :
                string.Join(Environment.NewLine, _confirmedRevision.Files.Keys.Union(current.Files.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(k => _confirmedRevision.Files.GetValueOrDefault(k) != current.Files.GetValueOrDefault(k)));
            if (_conflictPrompt == null || !await _conflictPrompt.ConfirmAsync(differences))
                throw new IOException(Text("Recovery_WriteBlocked", "Workspace changed externally. Writing was cancelled."));
            // Re-read after the user's decision; the writer checks this expectation again under its lock.
            var reviewed = await _workspaceHistory.InspectAsync(Workspace, true, token);
            if (!current.Matches(reviewed)) throw new IOException(Text("Recovery_WriteBlocked", "Workspace changed again while it was being reviewed."));
            _confirmedRevision = reviewed;
            _requireExternalReview = false;
        }
        _confirmedRevision ??= current;
    }

    private async Task FindRecoveryCoreAsync(CancellationToken token)
    {
        RecoveryCandidates = []; SelectedRecoveryCandidate = null; RecoveryMessage = null;
        if (_drafts == null || string.IsNullOrEmpty(PluginPath)) return;
        var result = await _drafts.FindPendingAsync(PluginPath, GameDataPath, _draftWriterSession, token);
        if (result == null) { RecoveryMessage = "Recovery scan returned no result."; return; }
        if (result.IsFailure) { RecoveryMessage = result.Error; return; }
        RecoveryCandidates = new(result.Value.Candidates);
        SelectedRecoveryCandidate = RecoveryCandidates.FirstOrDefault();
        if (result.Value.Errors.Count > 0) RecoveryMessage = string.Join(Environment.NewLine, result.Value.Errors);
        else if (RecoveryCandidates.Count > 0) RecoveryMessage = Text("Recovery_Found", "A draft is available. Restore only loads fields into the editor; it does not write the plugin.");
    }

    [RelayCommand]
    private Task FindRecoveryAsync() => RunOperationAsync(FindRecoveryCoreAsync);

    [RelayCommand]
    private Task RestoreDraftAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
    {
        var candidate = SelectedRecoveryCandidate ?? throw new InvalidOperationException(Text("Recovery_Select", "Select a draft first."));
        var scan = await _drafts!.FindPendingAsync(PluginPath, GameDataPath, _draftWriterSession, token);
        if (scan.IsFailure || !scan.Value.Candidates.Any(c => c.FilePath == candidate.FilePath && c.Draft.Revision == candidate.Draft.Revision))
            throw new IOException(Text("Recovery_Changed", "The selected draft changed or its writer is active."));
        var snapshot = candidate.Draft;
        if (snapshot.Weapon.Keywords == null || snapshot.Weapon.Keywords.Any(k => k == null) || snapshot.Recipe.Ingredients == null || snapshot.Recipe.Ingredients.Any(i => i == null || i.Reference == null) ||
            new[] { snapshot.Weapon.EditorId, snapshot.Weapon.Name, snapshot.Weapon.Damage, snapshot.Weapon.Weight, snapshot.Weapon.Value,
                snapshot.Weapon.Description, snapshot.Weapon.Speed, snapshot.Weapon.Reach, snapshot.Weapon.ModelPath, snapshot.Weapon.FirstPersonPath,
                snapshot.Weapon.EnchantmentCharge, snapshot.Recipe.EditorId, snapshot.Recipe.OutputCount }.Any(s => s == null))
            throw new IOException(Text("Recovery_Changed", "Invalid draft fields."));
        var previous = CaptureMemento();
        _applyingEditorState = true;
        try
        {
            await ApplyEditorSnapshotAsync(snapshot, token, dirtyOnly: true);
            _confirmedRevision = snapshot.SourceRevision?.Context == Workspace ? snapshot.SourceRevision : null;
            _requireExternalReview = _confirmedRevision == null;
            _recoveredWeaponPending = snapshot.WeaponIsDirty;
            _recoveredRecipePending = snapshot.RecipeIsDirty;
            UpdateUnsavedChanges();
        }
        catch { await ApplyMementoAsync(previous, CancellationToken.None); throw; }
        finally { _applyingEditorState = false; ResetEditorHistory(); }
        if (!await PersistDraftUnderGateAsync()) throw new IOException(DraftSaveError);
        var dismissed = await _drafts.SaveAsync(snapshot with { State = EditorDraftState.Discarded, WeaponIsDirty = false, RecipeIsDirty = false }, snapshot.Revision, token);
        if (dismissed.IsFailure) throw new IOException(dismissed.Error);
        await FindRecoveryCoreAsync(token);
        RecoveryMessage = Text("Recovery_Restored", "Draft loaded into memory. Review models and external-file warnings before saving.");
    }));

    [RelayCommand]
    private Task DiscardRecoveryAsync() => RunOperationAsync(async token =>
    {
        var candidate = SelectedRecoveryCandidate ?? throw new InvalidOperationException(Text("Recovery_Select", "Select a draft first."));
        var scan = await _drafts!.FindPendingAsync(PluginPath, GameDataPath, _draftWriterSession, token);
        if (scan.IsFailure || !scan.Value.Candidates.Any(c => c.FilePath == candidate.FilePath && c.Draft.Revision == candidate.Draft.Revision))
            throw new IOException(Text("Recovery_Changed", "The selected draft changed or its writer is active."));
        var result = await _drafts.SaveAsync(candidate.Draft with { State = EditorDraftState.Discarded, WeaponIsDirty = false, RecipeIsDirty = false }, candidate.Draft.Revision, token);
        if (result.IsFailure) throw new IOException(result.Error);
        await FindRecoveryCoreAsync(token);
    });

    private async Task ApplyEditorSnapshotAsync(WeaponEditorDraft snapshot, CancellationToken token, bool dirtyOnly = false, bool useCachedRecipe = false)
    {
        if (snapshot.WeaponFormId != null)
            SelectedWeapon = Weapons.FirstOrDefault(w => w.FormId == snapshot.WeaponFormId) ??
                throw new IOException(Text("Recovery_RecordMissing", "The draft's weapon no longer exists. No other record was selected for it."));
        else
        {
            SelectedWeapon = null;
            SelectedTemplate = Templates.FirstOrDefault(t => t.FormId == snapshot.TemplateFormId && t.PluginName.Equals(snapshot.TemplatePluginName, StringComparison.OrdinalIgnoreCase));
            if (SelectedTemplate == null && (snapshot.WeaponIsDirty || snapshot.RecipeIsDirty))
                throw new IOException(Text("Recovery_TemplateMissing", "The draft's template is not available."));
        }
        if (SelectedWeapon != null && (!dirtyOnly || snapshot.RecipeIsDirty))
        {
            if (snapshot.RecipeFormId != null)
            {
                if (!useCachedRecipe || Recipes.All(r => r.FormId != snapshot.RecipeFormId))
                    await RefreshRecipesCoreAsync(snapshot.RecipeFormId, token);
                if (Recipes.All(r => r.FormId != snapshot.RecipeFormId))
                    throw new IOException(Text("Recovery_RecipeMissing", "The draft's recipe no longer exists."));
                SelectedRecipe = Recipes.First(r => r.FormId == snapshot.RecipeFormId);
            }
            else SelectedRecipe = null;
        }
        if (!dirtyOnly || snapshot.WeaponIsDirty)
        {
            var w = snapshot.Weapon;
            EditorId = w.EditorId; WeaponName = w.Name; Damage = w.Damage; Weight = w.Weight; Value = w.Value;
            Description = w.Description; Speed = w.Speed; Reach = w.Reach; ModelPath = w.ModelPath;
            _fillingModel = true; FirstPersonModelPath = w.FirstPersonPath; _fillingModel = false;
            _firstPersonEdited = w.FirstPersonEdited; _restoreFirstPerson = w.RestoreFirstPerson;
            _firstPersonReference = w.FirstPersonReference; _restorationOrigin = w.RestorationOrigin; _restorationPluginName = w.RestorationPluginName;
            WeaponKeywords = new(w.Keywords.Select(k => Option(Options.Keywords, k)));
            SelectedSound = SelectOption(SoundChoices, w.Sound); SelectedEnchantment = SelectOption(EnchantmentChoices, w.Enchantment);
            EnchantmentCharge = w.EnchantmentCharge; _newWeaponPending = snapshot.NewWeapon;
        }
        if (!dirtyOnly || snapshot.RecipeIsDirty)
        {
            var r = snapshot.Recipe;
            RecipeEditorId = r.EditorId; RecipeOutputCount = r.OutputCount;
            SelectedWorkbench = SelectOption(WorkbenchChoices, r.Workbench); SelectedPerk = SelectOption(PerkChoices, r.Perk);
            RecipeIngredients = new(r.Ingredients.Select(i => new RecipeIngredientRow(Option(Options.Ingredients, i.Reference), i.Count)));
            _newRecipePending = snapshot.NewRecipe;
        }
        RestorationTemplate = Templates.FirstOrDefault(t => t.FormId == snapshot.RestorationTemplateFormId && t.PluginName.Equals(snapshot.RestorationTemplatePluginName, StringComparison.OrdinalIgnoreCase));
        CurrentStep = Math.Clamp(snapshot.CurrentStep, 0, 4);
        IsWizardSaved = false;
    }
}
