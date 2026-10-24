using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private readonly IWeaponDraftRepository? _drafts;
    private readonly IDraftAutosaveScheduler? _draftScheduler;
    private readonly Guid _draftWriterSession = Guid.NewGuid();
    private static readonly DateTimeOffset DraftProcessStartedUtc = ReadProcessStartedUtc();
    private static DateTimeOffset ReadProcessStartedUtc()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime());
    }
    private readonly SemaphoreSlim _draftIoGate = new(1, 1);
    private readonly Dictionary<string, long> _draftRevisions = new(StringComparer.OrdinalIgnoreCase);
    private long _draftEditGeneration;
    [ObservableProperty] private bool _enableDraftAutosave = true;
    [ObservableProperty] private string? _draftSaveStatus;
    [ObservableProperty] private string? _draftSaveError;
    [ObservableProperty] private string? _draftFilePath;

    partial void OnEnableDraftAutosaveChanged(bool value)
    {
        _draftScheduler?.Cancel();
        QueueDraftSave();
    }

    private bool HasDraftContext => _drafts != null && Path.IsPathFullyQualified(PluginPath) && Path.IsPathFullyQualified(GameDataPath);

    private void QueueDraftSave()
    {
        _draftEditGeneration++;
        if (IsEditorBusy || !HasDraftContext) return;
        SetDraftWaitingStatus();
        if (EnableDraftAutosave) _draftScheduler?.Schedule(() => SaveEditorDraftAsync());
    }

    private void SetDraftWaitingStatus() => DraftSaveStatus = Text(EnableDraftAutosave ? "Draft_Pending" : "Draft_Paused",
        EnableDraftAutosave ? "Draft save scheduled." : "Draft autosave is off; use Save draft to keep these edits.");

    private WeaponEditorDraft CaptureEditorDraft(bool discard = false, bool recipeOnly = false) => new()
    {
        WriterProcessId = Environment.ProcessId,
        WriterProcessStartedUtc = DraftProcessStartedUtc,
        SourceRevision = _confirmedRevision,
        WriterSessionId = _draftWriterSession, PluginPath = PluginPath, ProjectFilePath = ProjectFilePath, GameDataPath = GameDataPath,
        WeaponFormId = SelectedWeapon?.FormId, TemplateFormId = SelectedTemplate?.FormId, RecipeFormId = SelectedRecipe?.FormId,
        TemplatePluginName = SelectedTemplate?.PluginName ?? "Skyrim.esm", RestorationTemplatePluginName = RestorationTemplate?.PluginName ?? "Skyrim.esm",
        RestorationTemplateFormId = RestorationTemplate?.FormId, CurrentStep = CurrentStep, WizardSchemaVersion = 2,
        NewWeapon = _newWeaponPending, NewRecipe = _newRecipePending,
        WeaponIsDirty = HasUnsavedWeaponChanges && (!discard || recipeOnly), RecipeIsDirty = HasUnsavedRecipeChanges && !discard,
        WeaponDiscarded = discard && !recipeOnly && HasUnsavedWeaponChanges, RecipeDiscarded = discard && HasUnsavedRecipeChanges,
        State = discard ? (recipeOnly && HasUnsavedWeaponChanges ? EditorDraftState.Pending : EditorDraftState.Discarded)
            : HasUnsavedChanges ? EditorDraftState.Pending : EditorDraftState.Clean,
        Weapon = new()
        {
            EditorId = EditorId, Name = WeaponName, Damage = Damage, Weight = Weight, Value = Value, Description = Description,
            Speed = Speed, Reach = Reach, ModelPath = ModelPath, FirstPersonPath = FirstPersonModelPath,
            FirstPersonEdited = _firstPersonEdited, RestoreFirstPerson = _restoreFirstPerson, FirstPersonReference = _firstPersonReference,
            RestorationOrigin = _restorationOrigin, RestorationPluginName = _restorationPluginName, Sound = SelectedSound?.Reference, Sounds = _draftSounds, Enchantment = SelectedEnchantment?.Reference,
            EnchantmentCharge = EnchantmentCharge, Keywords = WeaponKeywords.Select(k => k.Reference).ToArray()
        },
        Recipe = new()
        {
            EditorId = RecipeEditorId, OutputCount = RecipeOutputCount, Workbench = SelectedWorkbench?.Reference, Perk = SelectedPerk?.Reference,
            Ingredients = RecipeIngredients.Select(i => new RecipeIngredient { Reference = i.Item.Reference, Count = i.Count }).ToArray()
        }
    };

    /// <summary>Caller holds the same gate as manual plugin operations. Storage errors never masquerade as plugin failures.</summary>
    private async Task<bool> PersistDraftUnderGateAsync(bool discard = false, bool recipeOnly = false)
    {
        if (!HasDraftContext) return true;
        try
        {
            var snapshot = CaptureEditorDraft(discard, recipeOnly);
            var generation = _draftEditGeneration;
            var context = Path.GetFullPath(snapshot.PluginPath);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await _drafts!.SaveAsync(snapshot, _draftRevisions.GetValueOrDefault(context), timeout.Token);
            if (result.IsFailure) throw new IOException(result.Error);
            _draftRevisions[context] = result.Value.Revision;
            DraftFilePath = result.Value.FilePath;
            DraftSaveError = null;
            DraftSaveStatus = Text(discard ? "Draft_Discarded" : snapshot.State == EditorDraftState.Clean ? "Draft_Synchronized" : "Draft_Saved",
                discard ? "Draft discard recorded." : snapshot.State == EditorDraftState.Clean ? "Draft synchronized; no pending edits." : "Draft saved separately from the plugin.");
            if (generation != _draftEditGeneration) SetDraftWaitingStatus();
            return true;
        }
        catch (Exception ex)
        {
            DraftSaveStatus = null;
            DraftSaveError = Text("Draft_Failed", "Could not save the draft: ") + ex.Message;
            return false;
        }
    }

    private async Task<bool> FlushEditorDraftAsync(bool discard = false, bool recipeOnly = false)
    {
        _draftScheduler?.Cancel();
        if (!HasDraftContext) return true;
        var acquired = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _draftIoGate.WaitAsync(timeout.Token);
            acquired = true;
            return await PersistDraftUnderGateAsync(discard, recipeOnly);
        }
        catch (Exception ex) { DraftSaveError = Text("Draft_Failed", "Could not save the draft: ") + ex.Message; return false; }
        finally { if (acquired) _draftIoGate.Release(); }
    }

    [RelayCommand]
    private async Task SaveEditorDraftAsync()
    {
        if (IsEditorBusy) return;
        if (!HasDraftContext)
        {
            DraftSaveError = Text("Draft_RequireContext", "Create or open a working plugin before saving a draft.");
            return;
        }
        await FlushEditorDraftAsync();
    }
}
