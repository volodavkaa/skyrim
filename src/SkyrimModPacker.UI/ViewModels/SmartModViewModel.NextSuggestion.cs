using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    private sealed record Suggestion(string LabelKey, SmartModItemStep? Step = null, SmartModRecipeRow? Recipe = null, SmartModIssue? Issue = null);

    private Suggestion? CurrentSuggestion()
    {
        var item = SelectedItem;
        if (item == null || !HasProject) return null;
        if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Contains('\0') || item.EditorId.Length is 0 or > 128 ||
            !char.IsAsciiLetter(item.EditorId[0]) || item.EditorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
            item.Template == null && item.ConfirmedFormId == null || IdentifierConflict(item).Length > 0)
            return new("Smart_NextSuggestionIdentity", SmartModItemStep.NameAndTemplate);
        if (string.IsNullOrWhiteSpace(item.ModelPath) || !item.ModelPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
            item.Fields.FirstPersonEdited && string.IsNullOrWhiteSpace(item.FirstPersonPath))
            return new("Smart_NextSuggestionModel", SmartModItemStep.Appearance);
        if (!ushort.TryParse(item.Damage, out _) || !SmartModItemRow.Number(item.Weight, out var weight) || weight < 0 ||
            !uint.TryParse(item.Value, out _) || !SmartModItemRow.Number(item.Speed, out var speed) || speed <= 0 ||
            !SmartModItemRow.Number(item.Reach, out var reach) || reach <= 0 || !ushort.TryParse(item.EnchantmentCharge, out var charge) ||
            (item.Enchantment == null ? charge != 0 : charge == 0))
            return new("Smart_NextSuggestionStats", SmartModItemStep.Properties);
        var recipe = item.Recipes.FirstOrDefault(r => !RecipeIsValid(r.Capture())) ??
            item.Recipes.GroupBy(r => r.IsTempering).FirstOrDefault(g => g.Count() > 1)?.First();
        if (recipe != null) return new("Smart_NextSuggestionRecipe", SmartModItemStep.Acquisition, recipe);
        if (item.Capture().State == SmartModItemState.NeedsCorrection && Issues.FirstOrDefault(i => i.IsError && i.ItemId == item.Id) is { } issue)
            return new("Smart_NextSuggestionIssue", Issue: issue);
        if (item.Capture().State != SmartModItemState.Ready || item.ConfirmedFormId == null)
            return new("Smart_NextSuggestionSave", SmartModItemStep.Summary);
        return new("Smart_NextSuggestionChecks");
    }

    public string NextSuggestionText => CurrentSuggestion() is { } suggestion ? Label(suggestion.LabelKey) : "";
    public bool HasNextSuggestion => CurrentSuggestion() != null;

    [RelayCommand]
    private void OpenNextSuggestion()
    {
        if (IsBusy || CurrentSuggestion() is not { } suggestion) return;
        if (suggestion.Issue != null) { NavigateIssue(suggestion.Issue); return; }
        if (suggestion.Step is { } step && SelectedItem is { } item)
        {
            item.Step = step; Section = SmartModSection.Weapons;
            if (suggestion.Recipe != null) SelectedRecipe = suggestion.Recipe;
        }
        else Section = SmartModSection.Checks;
        NotifyStep();
    }
}
