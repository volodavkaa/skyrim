using CommunityToolkit.Mvvm.Input;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    // Explicit properties also remain visible when an editor has not refreshed MVVM generators.
    private string? _recipeIdError, _recipeCountError, _recipeWorkbenchError, _recipeIngredientsError;
    public string? RecipeIdError { get => _recipeIdError; set => SetProperty(ref _recipeIdError, value); }
    public string? RecipeCountError { get => _recipeCountError; set => SetProperty(ref _recipeCountError, value); }
    public string? RecipeWorkbenchError { get => _recipeWorkbenchError; set => SetProperty(ref _recipeWorkbenchError, value); }
    public string? RecipeIngredientsError { get => _recipeIngredientsError; set => SetProperty(ref _recipeIngredientsError, value); }

    private bool ValidateRecipeFields()
    {
        var id = RecipeEditorId.Trim();
        RecipeIdError = id.Length == 0 || id.Length > 128 || !char.IsAsciiLetter(id[0]) || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
            ? Text("Wizard_InvalidEditorId", "Enter a valid EditorID.") : null;
        RecipeCountError = ushort.TryParse(RecipeOutputCount, out var count) && count > 0 ? null : Text("Stage5_RecipeCount", "Use an integer from 1 to 65535.");
        RecipeWorkbenchError = SelectedWorkbench == null ? Text("Stage5_RecipeWorkbench", "Select a workbench.") : null;
        RecipeIngredientsError = RecipeIngredients.Count == 0 || RecipeIngredients.Any(i => i.Count <= 0) ||
            RecipeIngredients.Select(i => i.Item.Reference).Distinct().Count() != RecipeIngredients.Count
            ? Text("Stage5_RecipeIngredients", "Add distinct ingredients with positive counts.") : null;
        return RecipeIdError == null && RecipeCountError == null && RecipeWorkbenchError == null && RecipeIngredientsError == null;
    }

    [RelayCommand]
    private Task DiscardRecipeDraftAsync() => RunOperationAsync(_ =>
    {
        _applyingEditorState = true;
        try
        {
            if (SelectedRecipe != null) OnSelectedRecipeChanged(SelectedRecipe); else ResetRecipeEditor();
            AcceptRecipeDraft();
            RecipeIdError = RecipeCountError = RecipeWorkbenchError = RecipeIngredientsError = null;
        }
        finally { _applyingEditorState = false; }
        return Task.CompletedTask;
    });
}
