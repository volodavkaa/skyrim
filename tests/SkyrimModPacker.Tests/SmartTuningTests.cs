using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class SmartTuningCoreTests
{
    [Theory]
    [InlineData(SmartBalanceMode.Weaker, 9, 85)] [InlineData(SmartBalanceMode.Equal, 10, 100)] [InlineData(SmartBalanceMode.Stronger, 12, 115)]
    [InlineData(SmartBalanceMode.MuchWeaker, 7, 70)] [InlineData(SmartBalanceMode.MuchStronger, 13, 130)]
    public void BalanceUsesFixedReferenceAndAwayFromZeroRounding(SmartBalanceMode mode, int damage, int value)
    {
        var reference = new SmartWeaponStats(10, 9, 100, 1, 1);
        var result = SmartWeaponBalance.Calculate(reference, mode); Assert.Equal(damage, result.Damage); Assert.Equal((uint)value, result.Value); Assert.Equal(reference.Weight, result.Weight);
        Assert.Equal(result, SmartWeaponBalance.Calculate(reference, mode));
    }
    [Fact]
    public void BalanceRejectsOverflowInvalidFloatsAndKeepsSmallDamagePositive()
    {
        Assert.Equal((ushort)1, SmartWeaponBalance.Calculate(new(1, 0, 0, 1, 1), SmartBalanceMode.Weaker).Damage);
        Assert.Throws<InvalidDataException>(() => SmartWeaponBalance.Calculate(new(ushort.MaxValue, 1, 1, 1, 1), SmartBalanceMode.Stronger));
        Assert.Throws<InvalidDataException>(() => SmartWeaponBalance.Calculate(new(1, 1, uint.MaxValue, 1, 1), SmartBalanceMode.Stronger));
        Assert.Throws<InvalidDataException>(() => SmartWeaponBalance.Calculate(new(1, float.NaN, 1, 1, 1), SmartBalanceMode.Equal));
    }
}

public partial class WeaponsViewModelTests
{
    private SmartMaterialPreset TestMaterial(SkyrimModPacker.UI.ViewModels.SmartModViewModel vm, string id = "TestSteel:Sword")
    {
        vm.SelectedItem = vm.Items.First(i => i.Kind == WeaponKind.OneHandSword);
        var ingredient = vm.Options.Ingredients.First(o => o.EditorId == "IngotIron").Reference;
        var forge = vm.Options.Keywords.First(o => o.EditorId == "CraftingSmithingForge").Reference;
        var wheel = vm.Options.Keywords.First(o => o.EditorId == "CraftingSmithingSharpeningWheel").Reference;
        var tag = vm.Options.Keywords.First(o => o.EditorId == "WeapMaterialSteel").Reference;
        var source = new PluginRecordReference("Skyrim.esm", 0xFA10);
        var p = new SmartMaterialPreset(id, "Steel", SmartWeaponSubtype.Sword, WeaponKind.OneHandSword, vm.SelectedItem!.Template, "TestSteelSword", new(10, 9, 100, 1, 1), [tag],
            new(source, new() { EditorId = "SourceForge", Workbench = forge, Ingredients = [new() { Reference = ingredient, Count = 2 }] }),
            new(source with { FormId = 0xFA11 }, new() { EditorId = "SourceTemper", Workbench = wheel, IsTempering = true, Ingredients = [new() { Reference = ingredient, Count = 1 }] }), "", null);
        vm.MaterialPresets.Add(p); return p;
    }
    [Fact]
    public async Task SmartTuningAppliesRecipesBalancesWithoutAccumulationAndPreservesManualFields()
    {
        await using var vm = await PrepareSmartAsync(); var preset = TestMaterial(vm); var item = vm.SelectedItem!; var name = item.Name; var editorId = item.EditorId; var model = item.ModelPath;
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, true, true, true)); Assert.Equal("", vm.Error);
        Assert.Equal("10", item.Damage); Assert.Equal(2, item.Recipes.Count); var recipeIds = item.Recipes.Select(r => r.Id).Order().ToArray();
        await vm.ApplyTuningAsync(vm.PreviewBalance(SmartBalanceMode.Weaker, true)); Assert.Equal("9", item.Damage);
        await vm.ApplyTuningAsync(vm.PreviewBalance(SmartBalanceMode.Stronger, true)); Assert.Equal("12", item.Damage); Assert.Equal("115", item.Value);
        item.Weight = "2,5"; item.Damage = "17";
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, true, true, true)); Assert.Equal("17", item.Damage); Assert.Equal("2,5", item.Weight);
        Assert.Equal(recipeIds, item.Recipes.Select(r => r.Id).Order().ToArray()); Assert.Equal(name, item.Name); Assert.Equal(editorId, item.EditorId); Assert.Equal(model, item.ModelPath);
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true)); Assert.Equal("12", item.Damage); Assert.Equal("9", item.Weight);
        await vm.UndoTuningAsync(); Assert.Equal("", vm.Error); Assert.Equal("17", item.Damage); Assert.Equal("2,5", item.Weight);
        Assert.True(await vm.SaveDraftAsync()); await using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath);
        Assert.Equal("17", reopened.SelectedItem!.Damage); Assert.Contains("Damage", reopened.SelectedItem.Tuning!.ManualFields); Assert.Equal("TestSteel:Sword", reopened.SelectedItem.Tuning.PresetId);
        await reopened.ApplyTuningAsync(reopened.PreviewBalance(SmartBalanceMode.Equal, false)); Assert.Equal("", reopened.Error); Assert.Equal("10", reopened.SelectedItem.Damage);
    }
    [Fact]
    public async Task SmartTuningConfirmedRecipesUpdateInPlaceAndDeferredDisableSurvivesReopen()
    {
        await using var vm = await PrepareSmartAsync(); var preset = TestMaterial(vm);
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true)); await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        var form = vm.SelectedItem!.ConfirmedFormId!.Value; var ids = (await _builder.GetRecipesAsync(vm.PluginPath, form)).Value.Select(r => r.FormId).Order().ToArray();
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true)); await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        Assert.Equal(ids, (await _builder.GetRecipesAsync(vm.PluginPath, form)).Value.Select(r => r.FormId).Order().ToArray());
        var before = File.ReadAllBytes(vm.PluginPath); await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, false, true)); Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
        Assert.Single(vm.SelectedItem.Recipes); Assert.Single(vm.SelectedItem.Tuning!.PendingRecipeRemovals);
        await using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal("", reopened.Error); Assert.Single(reopened.SelectedItem!.Recipes); Assert.False(reopened.DefaultCrafting);
        await reopened.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", reopened.Error); Assert.True(Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, form)).Value).IsTempering);
    }
    [Fact]
    public async Task SmartTuningRejectsStalePreviewAndClonesWithoutPendingDeletes()
    {
        await using var vm = await PrepareSmartAsync(); var preset = TestMaterial(vm); var preview = vm.PreviewMaterial(preset, true, true, true);
        vm.SelectedItem!.Name = "Changed after preview"; var before = File.ReadAllBytes(vm.PluginPath); await vm.ApplyTuningAsync(preview); Assert.NotEmpty(vm.Error); Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true)); await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, false, true)); Assert.NotEmpty(vm.SelectedItem.Tuning!.PendingRecipeRemovals);
        await vm.DuplicateSelectedAsync(true); Assert.Equal("", vm.Error); Assert.Empty(vm.SelectedItem!.Tuning!.PendingRecipeRemovals); Assert.Null(vm.SelectedItem.ConfirmedFormId);
    }
}

public sealed class RealSmartMaterialTests
{
    [Fact]
    public async Task RealMaterialMatrixHasAll63VerifiedProfilesAndWritableRecipes()
    {
        var game = Environment.GetEnvironmentVariable("SKYRIM_SE_DATA"); if (string.IsNullOrEmpty(game)) return;
        var builder = new PluginBuilderService(); var result = await builder.GetSmartMaterialPresetsAsync(game); Assert.True(result.IsSuccess, result.Error); Assert.Equal(63, result.Value.Count);
        Assert.All(result.Value, p => Assert.True(p.Available, p.Id + ": " + p.Error));
        var root = Path.Combine(Path.GetTempPath(), "SmartMaterialMatrix", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var plugin = (await builder.CreateAsync(new() { PluginFileName = "MaterialMatrix.esp", OutputDirectory = root, GameDataPath = game })).Value;
            foreach (var preset in result.Value)
            {
                var weapon = await builder.CloneSwordAsync(new() { PluginPath = plugin, GameDataPath = game, TemplateFormId = preset.Reference!.FormId, TemplatePluginName = preset.Reference.PluginName, EditorId = "Test_" + preset.Material + "_" + preset.Subtype, Name = preset.Id });
                Assert.True(weapon.IsSuccess, weapon.Error);
                foreach (var source in new[] { preset.Crafting!, preset.Tempering! })
                {
                    var data = source.Recipe;
                    var saved = await builder.SaveRecipeAsync(new() { PluginPath = plugin, GameDataPath = game, Recipe = new()
                    { WeaponFormId = weapon.Value.FormId, EditorId = "Recipe_" + preset.Material + "_" + preset.Subtype + "_" + data.IsTempering, Workbench = data.Workbench, IsTempering = data.IsTempering,
                        OutputCount = data.OutputCount, Ingredients = data.Ingredients, RequiredPerk = data.RequiredPerk, TemperingPerk = data.TemperingPerk } });
                    Assert.True(saved.IsSuccess, preset.Id + ": " + saved.Error); Assert.Equal(data.Ingredients.Select(i => i.Count), saved.Value.Ingredients.Select(i => i.Count)); Assert.Equal(data.RequiredPerk, saved.Value.RequiredPerk);
                }
                Assert.Equal(2, (await builder.GetRecipesAsync(plugin, weapon.Value.FormId)).Value.Count);
            }
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/audit")); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "smart-material-verified-matrix.json"), JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(root, true); }
    }
}
