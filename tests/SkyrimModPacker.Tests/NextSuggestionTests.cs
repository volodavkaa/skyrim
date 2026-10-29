using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task SuggestionTracksSelectedItemAndOpensRelevantStep()
    {
        await using var vm = await PrepareSmartAsync();
        var item = vm.SelectedItem!;
        item.Name = "";
        Assert.Equal("Smart_NextSuggestionIdentity", vm.NextSuggestionText);
        vm.OpenNextSuggestionCommand.Execute(null); Assert.Equal(SmartModItemStep.NameAndTemplate, item.Step);
        item.Name = "Sword"; item.ModelPath = "";
        Assert.Equal("Smart_NextSuggestionModel", vm.NextSuggestionText);
        vm.OpenNextSuggestionCommand.Execute(null); Assert.Equal(SmartModItemStep.Appearance, item.Step);
        item.ModelPath = "sword.nif"; item.Damage = "bad";
        Assert.Equal("Smart_NextSuggestionStats", vm.NextSuggestionText);
        vm.OpenNextSuggestionCommand.Execute(null); Assert.Equal(SmartModItemStep.Properties, item.Step);
        item.Damage = "7";
        vm.AddRecipeCommand.Execute("crafting");
        var recipe = vm.SelectedRecipe!;
        Assert.Equal("Smart_NextSuggestionRecipe", vm.NextSuggestionText);
        vm.SelectedRecipe = null; vm.OpenNextSuggestionCommand.Execute(null);
        Assert.Same(recipe, vm.SelectedRecipe); Assert.Equal(SmartModItemStep.Acquisition, item.Step);
        item.Recipes.Clear();
        Assert.Equal("Smart_NextSuggestionSave", vm.NextSuggestionText);
        vm.OpenNextSuggestionCommand.Execute(null); Assert.Equal(SmartModItemStep.Summary, item.Step);
        item.SetState(SmartModItemState.Ready);
        item.Replace(item.Capture() with { ConfirmedFormId = 0x800, State = SmartModItemState.Ready });
        Assert.Equal("Smart_NextSuggestionChecks", vm.NextSuggestionText);
        vm.OpenNextSuggestionCommand.Execute(null); Assert.Equal(SmartModSection.Checks, vm.Section);
        item.Damage = "8"; Assert.Equal("Smart_NextSuggestionSave", vm.NextSuggestionText);
    }
}
