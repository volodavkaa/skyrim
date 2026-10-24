using System.Text.Json;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;
namespace SkyrimModPacker.Tests;
public sealed class SmartRecipeCatalogTests
{
    [Fact]
    public async Task RealRecipeCatalogContainsBothRecipesForAllVerifiedMaterials()
    {
        var game = Environment.GetEnvironmentVariable("SKYRIM_SE_DATA"); if (string.IsNullOrWhiteSpace(game)) return;
        var builder = new PluginBuilderService(); var materials = (await builder.GetSmartMaterialPresetsAsync(game)).Value;
        var catalog = await builder.GetSmartRecipeSourcesAsync(game); Assert.True(catalog.IsSuccess, catalog.Error); Assert.True(catalog.Value.Count >= 126);
        foreach (var p in materials)
        foreach (var recipe in new[] { p.Crafting!, p.Tempering! })
        {
            var source = Assert.Single(catalog.Value, r => r.Source == recipe.Source); Assert.Equal(p.Kind, source.Kind); Assert.Equal(p.Subtype, source.Subtype);
            Assert.Equal(JsonSerializer.Serialize(recipe.Recipe), JsonSerializer.Serialize(source.Recipe));
        }
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/audit")); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "smart-game-recipe-catalog.json"), JsonSerializer.Serialize(catalog.Value, new JsonSerializerOptions { WriteIndented = true }));
    }
    [Fact]
    public async Task PersonalRecipesRejectDuplicateNamesInvalidDataAndCorruptStorageWithoutOverwriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartRecipePresetTests", Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new JsonSmartRecipePresetRepository(root);
            var recipe = new CraftingRecipe { EditorId = "Test", Workbench = new("Skyrim.esm", 1), Ingredients = [new() { Reference = new("Skyrim.esm", 2), Count = 10 }] };
            var preset = new SmartRecipePreset { Name = "My sword recipe", Kind = WeaponKind.OneHandSword, Subtype = SmartWeaponSubtype.Sword, Recipe = recipe };
            await repo.SaveAsync(preset); Assert.Equal(10, Assert.Single((await new JsonSmartRecipePresetRepository(root).LoadAsync()).Single().Recipe.Ingredients).Count);
            await Assert.ThrowsAsync<IOException>(() => repo.SaveAsync(preset with { Id = Guid.NewGuid(), Name = "MY SWORD RECIPE" }));
            await Assert.ThrowsAsync<InvalidDataException>(() => repo.SaveAsync(preset with { Name = "" })); Assert.Single(await repo.LoadAsync());
            var path = Path.Combine(root, "smart-recipe-presets.json"); File.WriteAllText(path, "broken JSON");
            await Assert.ThrowsAsync<JsonException>(() => repo.SaveAsync(preset)); Assert.Equal("broken JSON", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(SmartHandlingMode.Standard, 10, 1)] [InlineData(SmartHandlingMode.Light, 8, 1)] [InlineData(SmartHandlingMode.Heavy, 12, 1)]
    [InlineData(SmartHandlingMode.Fast, 10, 1.15)] [InlineData(SmartHandlingMode.Slow, 10, .85)]
    public void HandlingUsesFixedSnapshotAndNeverAltersDamageReachOrPrice(SmartHandlingMode handling, float weight, float speed)
    {
        var result = SmartWeaponBalance.Calculate(new(10, 10, 100, 1, 1), SmartBalanceMode.Equal, handling);
        Assert.Equal(weight, result.Weight); Assert.Equal(speed, result.Speed); Assert.Equal((ushort)10, result.Damage); Assert.Equal(100u, result.Value); Assert.Equal(1, result.Reach);
        Assert.Equal(result, SmartWeaponBalance.Calculate(new(10, 10, 100, 1, 1), SmartBalanceMode.Equal, handling));
    }
}
