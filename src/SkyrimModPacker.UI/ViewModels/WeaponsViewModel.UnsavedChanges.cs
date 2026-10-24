using System.Collections.Specialized;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private readonly IUnsavedChangesPrompt? _unsavedChangesPrompt;
    private WeaponDraftState? _savedWeaponDraft;
    private string? _savedRecipeDraft;
    private bool _newWeaponPending, _newRecipePending, _preserveRecipeOnRefresh;
    [ObservableProperty] private bool _hasUnsavedWeaponChanges;
    [ObservableProperty] private bool _hasUnsavedRecipeChanges;
    [ObservableProperty] private bool _isResolvingNavigation;
    public bool HasUnsavedChanges => HasUnsavedWeaponChanges || HasUnsavedRecipeChanges;
    public bool IsEditorBusy => IsLoading || IsResolvingNavigation;

    // Selection bindings request a transition; committed selections are set only after the guard.
    public WeaponRecord? WeaponSelection { get => SelectedWeapon; set { if (value != null && !Equals(value, SelectedWeapon)) PendingSelectionChange = ChangeSelectionAsync(() => SelectedWeapon = value); } }
    public SwordTemplate? TemplateSelection { get => SelectedTemplate; set { if (value != null && !Equals(value, SelectedTemplate)) PendingSelectionChange = ChangeSelectionAsync(() => SelectedTemplate = value); } }
    public CraftingRecipe? RecipeSelection { get => SelectedRecipe; set { if (value != null && !Equals(value, SelectedRecipe)) PendingSelectionChange = ChangeSelectionAsync(() => SelectedRecipe = value, recipeOnly: true); } }
    public Task PendingSelectionChange { get; private set; } = Task.CompletedTask;

    private async Task ChangeSelectionAsync(Action change, bool recipeOnly = false)
    {
        // ItemsSource refreshes can send a transient null through a two-way selector binding.
        // Reverting it while the old item is absent would cause a binding feedback loop.
        if (IsEditorBusy) return;
        try
        {
            var untouchedNewItem = !recipeOnly && SelectedWeapon == null && _newWeaponPending && !HasUnsavedRecipeChanges && _savedWeaponDraft == CaptureWeaponDraft();
            if (untouchedNewItem || await RequestLeaveEditorAsync(recipeOnly))
            {
                _applyingEditorState = true;
                try { change(); }
                finally { _applyingEditorState = false; ResetEditorHistory(); }
            }
        }
        finally
        {
            OnPropertyChanged(nameof(WeaponSelection)); OnPropertyChanged(nameof(TemplateSelection)); OnPropertyChanged(nameof(RecipeSelection));
            OnPropertyChanged(nameof(WeaponTypeSelection));
        }
    }

    private sealed record WeaponDraftState(string Fields, string FirstPath, bool FirstEdited, bool FirstRestored,
        PluginRecordReference? FirstReference, uint? RestorationOrigin, string RestorationPlugin);

    private WeaponDraftState CaptureWeaponDraft() => new(JsonSerializer.Serialize(new
    {
        EditorId, WeaponName, Damage, Weight, Value, Description, Speed, Reach, ModelPath,
        Sound = SelectedSound?.Reference, Sounds = _draftSounds, Enchantment = SelectedEnchantment?.Reference, EnchantmentCharge,
        Keywords = WeaponKeywords.Select(k => k.Reference).ToArray()
    }), FirstPersonModelPath, _firstPersonEdited, _restoreFirstPerson,
        _firstPersonReference, _restorationOrigin, _restorationPluginName);

    private string CaptureRecipeDraft() => JsonSerializer.Serialize(new
    {
        RecipeEditorId, RecipeOutputCount, Workbench = SelectedWorkbench?.Reference, Perk = SelectedPerk?.Reference,
        Ingredients = RecipeIngredients.Select(i => new { i.Item.Reference, i.Count }).ToArray()
    });

    private void AcceptWeaponDraft()
    {
        _newWeaponPending = false;
        _recoveredWeaponPending = false;
        _savedWeaponDraft = CaptureWeaponDraft();
        UpdateUnsavedChanges();
    }

    private void AcceptRecipeDraft()
    {
        _newRecipePending = false;
        _recoveredRecipePending = false;
        _savedRecipeDraft = CaptureRecipeDraft();
        UpdateUnsavedChanges();
    }

    private void UpdateUnsavedChanges()
    {
        NotifyInheritance();
        OnPropertyChanged(nameof(CompatibilityWarnings));
        HasUnsavedWeaponChanges = _newWeaponPending || _recoveredWeaponPending || (_savedWeaponDraft != null && _savedWeaponDraft != CaptureWeaponDraft());
        HasUnsavedRecipeChanges = _newRecipePending || _recoveredRecipePending || (_savedRecipeDraft != null && _savedRecipeDraft != CaptureRecipeDraft());
        QueueDraftSave();
        TrackEditorHistory();
    }

    private void DraftCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { UpdateUnsavedChanges(); IsWizardSaved = false; }
    partial void OnRecipeIngredientsChanged(System.Collections.ObjectModel.ObservableCollection<RecipeIngredientRow>? oldValue,
        System.Collections.ObjectModel.ObservableCollection<RecipeIngredientRow> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= DraftCollectionChanged;
        newValue.CollectionChanged += DraftCollectionChanged; UpdateUnsavedChanges();
    }

    /// <summary>Does not discard data until the caller actually replaces the editor or closes.</summary>
    public async Task<bool> RequestLeaveEditorAsync(bool recipeOnly = false)
    {
        if (IsEditorBusy)
        {
            StatusMessage = Text("Unsaved_Busy", "Finish or cancel the current operation before leaving the editor.");
            return false;
        }
        if (!(recipeOnly ? HasUnsavedRecipeChanges : HasUnsavedChanges)) return await FlushEditorDraftAsync();
        IsResolvingNavigation = true;
        try
        {
            var choice = _unsavedChangesPrompt == null ? UnsavedChangesChoice.ContinueEditing : await _unsavedChangesPrompt.ShowAsync();
            if (choice == UnsavedChangesChoice.Discard) return await FlushEditorDraftAsync(discard: true, recipeOnly: recipeOnly);
            if (choice != UnsavedChangesChoice.Save) return false;
            if (!await SavePendingChangesAsync(recipeOnly)) return false;
            return await FlushEditorDraftAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; return false; }
        finally { IsResolvingNavigation = false; }
    }

    private async Task<bool> SavePendingChangesAsync(bool recipeOnly = false)
    {
        if ((!recipeOnly && HasUnsavedWeaponChanges) || (HasUnsavedRecipeChanges && SelectedWeapon == null))
        {
            if (SelectedWeapon == null) await CloneSwordCommand.ExecuteAsync(null);
            else await SaveSwordCommand.ExecuteAsync(null);
            if (ErrorMessage != null || _operationCts?.IsCancellationRequested == true || HasUnsavedWeaponChanges) return false;
        }
        if (HasUnsavedRecipeChanges)
        {
            await SaveRecipeCommand.ExecuteAsync(null);
            if (ErrorMessage != null || _operationCts?.IsCancellationRequested == true) return false;
        }
        return recipeOnly ? !HasUnsavedRecipeChanges : !HasUnsavedChanges;
    }

    [RelayCommand]
    private async Task SaveAllChangesAsync()
    {
        if (IsEditorBusy) return;
        IsResolvingNavigation = true;
        try { await SavePendingChangesAsync(); }
        finally { IsResolvingNavigation = false; }
    }

    private async Task GuardEditorReplacementAsync(Func<Task> operation, bool recipeOnly = false)
    {
        if (await RequestLeaveEditorAsync(recipeOnly)) await operation();
    }
}
