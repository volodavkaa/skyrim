using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class WeaponComparisonTests
{
    [Theory]
    [InlineData("14", "12", "+2", false)]
    [InlineData("0", "0", "0", true)]
    [InlineData("2", "0", "+2", false)]
    [InlineData("1,5", "1.5", "0", true)]
    [InlineData("NaN", "12", "—", false)]
    [InlineData(null, "12", "—", false)]
    [InlineData("4", null, "—", false)]
    public void NumericComparisonHandlesMissingValuesAndZero(string? mine, string? sample, string prefix, bool equal)
    {
        var row = WeaponComparison.Numeric("Damage", mine, sample); Assert.StartsWith(prefix, row.Difference); Assert.Equal(equal, row.Equal);
        if (sample == "0") Assert.DoesNotContain("%", row.Difference);
    }
    [Fact]
    public void IngredientMatchingUsesPluginAndFormIdAndHandlesDuplicateRows()
    {
        var iron = new PluginRecordReference("Skyrim.esm", 1); var unrelated = new PluginRecordReference("Other.esp", 1);
        var mine = new SmartModRecipeDraft { Ingredients = [new(iron, "3"), new(iron with { PluginName = "SKYRIM.ESM" }, "2"), new(unrelated, "1")] };
        var sample = new SmartModRecipeDraft { Ingredients = [new(iron, "5")] };
        var rows = WeaponComparison.Ingredients(mine, sample, _ => "Same name"); Assert.Equal(2, rows.Count);
        Assert.Equal("5", rows[0].Mine); Assert.True(rows[0].Equal); Assert.Equal("0", rows[1].Sample); Assert.False(rows[1].Equal);
        Assert.Equal("—", Assert.Single(WeaponComparison.Ingredients(mine with { Ingredients = [new(iron, "invalid")] }, null, _ => "Iron")).Mine);
    }
}

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task ComparisonCopiesOnlySelectedGroupsUndoesAndSurvivesPluginReopen()
    {
        await using var vm = await PrepareSmartAsync(); var recipient = vm.Items[0]; var donor = vm.Items[1];
        vm.SelectedItem = donor; await vm.ApplyTuningAsync(vm.PreviewMaterial(TestMaterial(vm), false, true, true)); Assert.Equal("", vm.Error);
        donor.Damage = "29"; donor.Weight = "2"; donor.Value = "900";
        var source = vm.CaptureComparisonItem(donor); vm.SelectedItem = recipient;
        var before = recipient.Capture(); var plugin = File.ReadAllBytes(vm.PluginPath);
        var selection = new WeaponComparisonSelection(false, true, false, source.Recipes.Single(r => !r.Data.IsTempering));
        var preview = vm.PreviewComparison(source, selection); Assert.Equal(before.Weapon.Damage, preview.After.Weapon.Damage); Assert.Equal(before.Weapon.EditorId, preview.After.Weapon.EditorId);
        Assert.Equal(before.Weapon.ModelPath, preview.After.Weapon.ModelPath); Assert.Equal(before.Weapon.Name, preview.After.Weapon.Name); Assert.Equal(before.Weapon.Enchantment, preview.After.Weapon.Enchantment);
        await vm.ApplyTuningAsync(preview); Assert.Equal("", vm.Error); Assert.Equal("2", recipient.Weight); Assert.Equal("900", recipient.Value); Assert.Single(recipient.Recipes);
        Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath)); Assert.True(vm.CanUndoTuning);
        await vm.UndoTuningAsync(); Assert.Equal("", vm.Error); Assert.Equal(before.Weapon, recipient.Fields); Assert.Empty(recipient.Recipes);
        await vm.ApplyTuningAsync(vm.PreviewComparison(source, selection)); Assert.Equal("", vm.Error);
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        await using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal("", reopened.Error);
        var restored = reopened.Items.Single(i => i.Id == recipient.Id); Assert.Equal("2", restored.Weight); Assert.Equal("900", restored.Value); Assert.Equal(before.Weapon.Damage, restored.Damage);
        Assert.Equal(source.Recipes.Single(r => !r.Data.IsTempering).Data.Ingredients.Select(i => (i.Reference, i.Count)), restored.Recipes.Single().Ingredients.Select(i => (i.Reference, i.Count)));
    }
    [Fact]
    public async Task ComparisonRejectsStaleDonorsAndUnsupportedRecipes()
    {
        await using var vm = await PrepareSmartAsync(); var recipient = vm.Items[0]; var donor = vm.Items[1]; var snapshot = vm.CaptureComparisonItem(donor);
        vm.SelectedItem = recipient; var preview = vm.PreviewComparison(snapshot, new(true, false, false)); donor.Damage = "99";
        var before = recipient.Fields; await vm.ApplyTuningAsync(preview); Assert.NotEmpty(vm.Error); Assert.Equal(before, recipient.Fields);
        var unsupported = new WeaponComparisonRecipe("Advanced", new() { Ingredients = [] }, "Unknown condition");
        Assert.Throws<IOException>(() => vm.PreviewComparison(snapshot with { Recipes = [unsupported] }, new(false, false, false, unsupported)));
        Assert.Throws<IOException>(() => vm.PreviewComparison(snapshot, new(false, false, false)));
        var saved = await vm.LoadComparisonAsync(vm.ComparisonChoices.Single(c => c.Saved), CancellationToken.None);
        Assert.Equal(recipient.EditorId, saved.Fields.EditorId);
    }
}
