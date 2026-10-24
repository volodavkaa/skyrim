using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SkyrimModPacker.Core.Models.PluginBuilder;
namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    public ObservableCollection<SmartRecipeSource> RecipeSources { get; } = [];
    public ObservableCollection<SmartRecipePreset> PersonalRecipePresets { get; } = [];
    [ObservableProperty] private SmartMaterialPreset? _selectedRecipeMaterial;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApplyRecipeSource))] private SmartRecipeSource? _selectedRecipeSource;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApplyRecipePreset))] private SmartRecipePreset? _selectedRecipePreset;
    [ObservableProperty] private string _recipeSourceSearch = "";
    [ObservableProperty] private string _recipePresetName = "";
    public IEnumerable<SmartRecipeSource> RecipeSourceChoices => RecipeSources.Where(s => s.Kind == SelectedItem?.Kind && s.Subtype == CurrentSubtype && s.Recipe.IsTempering == SelectedRecipe?.IsTempering &&
        (s.Name.Contains(RecipeSourceSearch, StringComparison.CurrentCultureIgnoreCase) || s.EditorId.Contains(RecipeSourceSearch, StringComparison.OrdinalIgnoreCase))).Take(100);
    public IEnumerable<SmartRecipePreset> PersonalRecipeChoices => PersonalRecipePresets.Where(p => p.Kind == SelectedItem?.Kind && p.Subtype == CurrentSubtype && p.Recipe.IsTempering == SelectedRecipe?.IsTempering);
    public bool CanSaveRecipePreset => _recipePresets != null && SelectedRecipe != null && RecipeIsValid(SelectedRecipe.Capture());
    public bool CanApplyRecipeSource => SelectedRecipeSource != null && RecipeSourceChoices.Contains(SelectedRecipeSource);
    public bool CanApplyRecipePreset => SelectedRecipePreset != null && PersonalRecipeChoices.Contains(SelectedRecipePreset);
    partial void OnRecipeSourceSearchChanged(string value) { SelectedRecipeSource = null; OnPropertyChanged(nameof(RecipeSourceChoices)); }
    public SmartTuningPreview PreviewRecipeTemplate(CraftingRecipe recipe, bool keepManual, string? fingerprint = null)
    {
        var item = SelectedItem?.Capture() ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (SelectedRecipe == null || recipe.IsTempering != SelectedRecipe.IsTempering) throw new InvalidOperationException(Label("Smart_InvalidRecipe"));
        if (recipe.IsTempering && recipe.TemperingPerk != null && Options.Perks.FirstOrDefault(p => p.Reference == recipe.TemperingPerk)?.EditorId != "ArcaneBlacksmith")
            throw new InvalidOperationException(Text("Цей рецепт має непідтримувану умову покращення. Її не буде замінено автоматично.", "This recipe has an unsupported tempering condition. It will not be replaced automatically."));
        return DescribeTuning(item, SmartRecipeApplication.Apply(item, recipe, keepManual)) with { ExpectedSourceFingerprint = fingerprint };
    }
    public SmartTuningPreview PreviewStandardRecipe(bool keepManual)
    {
        var preset = SelectedRecipeMaterial ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (preset.Subtype != CurrentSubtype || preset.Kind != SelectedItem?.Kind || !preset.Available) throw new InvalidOperationException(Label("Smart_TuningWrongSubtype"));
        var recipe = SelectedRecipe?.IsTempering == true ? preset.Tempering : preset.Crafting;
        return PreviewRecipeTemplate(recipe?.Recipe ?? throw new InvalidOperationException(Label("Smart_InvalidRecipe")), keepManual, preset.SourceFingerprint);
    }
    public SmartTuningPreview PreviewDonorRecipe(bool keepManual)
    {
        var source = SelectedRecipeSource ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (!RecipeSourceChoices.Contains(source)) throw new InvalidOperationException(Label("Smart_TuningWrongSubtype"));
        return PreviewRecipeTemplate(source.Recipe, keepManual, source.SourceFingerprint);
    }
    public SmartTuningPreview PreviewPersonalRecipe(bool keepManual)
    {
        var preset = SelectedRecipePreset ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        if (!PersonalRecipeChoices.Contains(preset)) throw new InvalidOperationException(Label("Smart_TuningWrongSubtype"));
        preset.Validate(); return PreviewRecipeTemplate(preset.Recipe, keepManual);
    }
    public SmartTuningPreview PreviewEmptyRecipe()
    {
        var before = SelectedItem?.Capture() ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        var old = SelectedRecipe?.Capture() ?? throw new InvalidOperationException(Label("Smart_InvalidRecipe"));
        var after = before with { State = SmartModItemState.InProgress, Recipes = before.Recipes.Select(r => r.Id == old.Id ? r with { Ingredients = [], OutputCount = "1", RequiredPerk = null } : r).ToArray() };
        var field = old.IsTempering ? "Tempering" : "Crafting";
        after = after with { Tuning = (before.Tuning ?? new SmartWeaponTuning { Mode = SmartBalanceMode.Custom, ManualFields = SmartWeaponBalance.Fields }) with {
            ManualFields = (before.Tuning?.ManualFields ?? SmartWeaponBalance.Fields).Append(field).Distinct().ToArray() } };
        return DescribeTuning(before, after);
    }
    public Task SaveRecipePresetAsync() => RunAsync(async token =>
    {
        if (_recipePresets == null || SelectedItem == null || SelectedRecipe == null || !RecipeIsValid(SelectedRecipe.Capture())) throw new InvalidOperationException(Label("Smart_InvalidRecipe"));
        if (string.IsNullOrWhiteSpace(RecipePresetName) || RecipePresetName.Trim().Length > 100 || RecipePresetName.Any(char.IsControl)) throw new InvalidOperationException(Text("Вкажи назву шаблону від 1 до 100 символів.", "Enter a template name between 1 and 100 characters."));
        var data = SelectedRecipe.Capture();
        var recipe = new CraftingRecipe { EditorId = "RecipePreset", Workbench = data.Workbench!, IsTempering = data.IsTempering, TemperingPerk = data.TemperingPerk,
            OutputCount = ushort.Parse(data.OutputCount), RequiredPerk = data.RequiredPerk, Ingredients = data.Ingredients.Select(i => new RecipeIngredient { Reference = i.Reference!, Count = int.Parse(i.Count) }).ToArray() };
        var preset = new SmartRecipePreset { Name = RecipePresetName.Trim(), Kind = SelectedItem.Kind, Subtype = CurrentSubtype, Recipe = recipe };
        await _recipePresets.SaveAsync(preset, token); PersonalRecipePresets.Add(preset); SelectedRecipePreset = preset; OnPropertyChanged(nameof(PersonalRecipeChoices)); Status = Label("Smart_RecipePresetSaved");
    });
    public Task DeleteRecipePresetAsync() => RunAsync(async token =>
    {
        if (_recipePresets == null || SelectedRecipePreset is not { } preset) return;
        await _recipePresets.DeleteAsync(preset.Id, token); PersonalRecipePresets.Remove(preset); SelectedRecipePreset = null; OnPropertyChanged(nameof(PersonalRecipeChoices));
    });
}
