using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using Xunit;
namespace SkyrimModPacker.Tests;
public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task RecipeTemplateNeverSilentlyReplacesUnsupportedTemperingGuard()
    {
        await using var vm = await PrepareSmartAsync(); TestMaterial(vm); vm.AddRecipeCommand.Execute("tempering");
        var bytes = File.ReadAllBytes(vm.PluginPath); var before = JsonSerializer.Serialize(vm.SelectedItem!.Capture());
        var recipe = new CraftingRecipe { EditorId = "UnsupportedGuard", Workbench = vm.SelectedRecipe!.Workbench!, IsTempering = true, TemperingPerk = new("Skyrim.esm", 0xFAEE),
            Ingredients = [new() { Reference = vm.Options.Ingredients.First().Reference, Count = 1 }] };
        Assert.Throws<InvalidOperationException>(() => vm.PreviewRecipeTemplate(recipe, false));
        Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath)); Assert.Equal(before, JsonSerializer.Serialize(vm.SelectedItem.Capture()));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StartWithResourcesCreatesOnlyOneChosenItemWithoutCountSetup(bool zipSource)
    {
        await using var fixture = await PrepareSmartAsync();
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplicationServices(); services.AddInfrastructureServices(Path.Combine(_root, "start-pipeline")); using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<PackingPipeline>();
        await using var vm = SmartEditor(pipeline); await vm.InitializeAsync(); vm.Name = "NoPlaceholder"; vm.OutputDirectory = Path.Combine(_root, "start-resources");
        Assert.False(vm.HasProject); Assert.All(vm.Types, t => Assert.Equal("0", t.Count)); Assert.True(vm.CanStartFromResources); Assert.False(vm.CanCreateProject);
        var source = Path.Combine(_root, "start-source"); Directory.CreateDirectory(Path.Combine(source, "meshes")); Directory.CreateDirectory(Path.Combine(source, "textures"));
        File.WriteAllBytes(Path.Combine(source, "meshes/Only.nif"), PreviewFixtures.Nif(diffuse: "textures/Only.dds")); File.WriteAllBytes(Path.Combine(source, "textures/Only.dds"), PreviewFixtures.Bc7());
        var zip = Path.Combine(_root, "weapon.zip"); if (zipSource) ZipFile.CreateFromDirectory(source, zip);
        using var archive = zipSource ? await vm.ExtractWeaponArchiveAsync(zip) : null; Assert.Equal("", vm.Error);
        var root = archive?.Root ?? source;
        var plan = await vm.PreviewWeaponFolderAsync(new(Guid.NewGuid(), vm.ProposedProjectPath, root, Path.Combine(root, "meshes/Only.nif"), null, true) { NewProjectPluginName = vm.Name + ".esp" });
        Assert.True(plan.CanImport); Assert.False(Directory.Exists(Path.GetDirectoryName(vm.ProposedProjectPath))); Assert.Empty(vm.Items);
        await vm.CreateFromWeaponFolderAsync(plan, WeaponKind.OneHandSword); Assert.Equal("", vm.Error); Assert.True(vm.HasProject);
        var item = Assert.Single(vm.Items); Assert.Contains("Only.nif", item.ModelPath); Assert.True(item.UseSharedModel); Assert.Null(item.ConfirmedFormId);
        Assert.Empty((await _builder.GetWeaponsAsync(vm.PluginPath)).Value);
        await using var reopened = SmartEditor(pipeline); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal("", reopened.Error); Assert.Single(reopened.Items);
        await reopened.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", reopened.Error); Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value);
        reopened.EnableEsl = zipSource; await reopened.PackCommand.ExecuteAsync(null); Assert.Equal("", reopened.Error); Assert.NotNull(reopened.LastArchivePath);
        using var packed = ZipFile.OpenRead(reopened.LastArchivePath);
        Assert.Single(packed.Entries, e => e.FullName.EndsWith(".nif")); Assert.Single(packed.Entries, e => e.FullName.EndsWith(".dds"));
        foreach (var file in plan.Files)
        { using var input = packed.GetEntry(file.RelativePath)!.Open(); using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); Assert.Equal(File.ReadAllBytes(file.SourcePath), bytes.ToArray()); }
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/acceptance/smart-expanded")); Directory.CreateDirectory(output);
        File.Copy(reopened.LastArchivePath, Path.Combine(output, zipSource ? "StartFromZip-esl.zip" : "StartFromFolder-esp.zip"), true);
    }
    [Fact]
    public async Task ResourceStartRejectsStaleSourceWithoutCreatingProject()
    {
        await using var fixture = await PrepareSmartAsync(); await using var vm = SmartEditor(); await vm.InitializeAsync(); vm.Name = "StaleStart"; vm.OutputDirectory = Path.Combine(_root, "stale-start");
        var root = Path.Combine(_root, "stale-assets"); Directory.CreateDirectory(root); var model = Path.Combine(root, "blade.nif"); File.WriteAllBytes(model, PreviewFixtures.Nif(diffuse: ""));
        var plan = await vm.PreviewWeaponFolderAsync(new(Guid.NewGuid(), vm.ProposedProjectPath, root, model, null, true) { NewProjectPluginName = vm.Name + ".esp" });
        File.WriteAllBytes(model, PreviewFixtures.Nif(diffuse: "textures/changed.dds"));
        await vm.CreateFromWeaponFolderAsync(plan, WeaponKind.OneHandSword); Assert.NotEmpty(vm.Error); Assert.False(vm.HasProject); Assert.False(Directory.Exists(Path.GetDirectoryName(vm.ProposedProjectPath)));
    }
    [Fact]
    public async Task RecipesAndNumericPresetsAreIndependentAndPersonalRecipesReopen()
    {
        await using var original = await PrepareSmartAsync(); var repo = new JsonSmartRecipePresetRepository(Path.Combine(_root, "personal-recipes"));
        await using var vm = SmartEditor(recipePresets: repo); await vm.InitializeAsync(original.ProjectPath); var material = TestMaterial(vm);
        vm.AddRecipeCommand.Execute("crafting"); vm.SelectedRecipeMaterial = material;
        var weapon = vm.SelectedItem!; var stats = weapon.Fields; var identity = vm.SelectedRecipe!.Id;
        await vm.ApplyTuningAsync(vm.PreviewStandardRecipe(false)); Assert.Equal("", vm.Error); Assert.Equal(identity, vm.SelectedRecipe!.Id); Assert.Equal(stats, weapon.Fields);
        vm.SelectedRecipe.Ingredients[0].Count = "10"; vm.RecipePresetName = "My expensive sword"; await vm.SaveRecipePresetAsync(); Assert.Equal("", vm.Error);
        Assert.Single(await repo.LoadAsync()); var saved = vm.SelectedRecipePreset!;
        var recipes = JsonSerializer.Serialize(weapon.Capture().Recipes);
        await vm.ApplyTuningAsync(vm.PreviewMaterialStats(material, false)); Assert.Equal("", vm.Error); Assert.Equal(recipes, JsonSerializer.Serialize(weapon.Capture().Recipes)); Assert.Equal("10", weapon.Damage);
        await vm.ApplyTuningAsync(vm.PreviewHandling(SmartHandlingMode.Fast, false)); Assert.Equal("1.15", weapon.Speed);
        await vm.ApplyTuningAsync(vm.PreviewBalance(SmartBalanceMode.MuchStronger, false)); Assert.Equal("13", weapon.Damage); Assert.Equal("130", weapon.Value); Assert.Equal("1.15", weapon.Speed);
        await vm.ApplyTuningAsync(vm.PreviewHandling(SmartHandlingMode.Heavy, false)); Assert.Equal("10.8", weapon.Weight); Assert.Equal("1", weapon.Speed); Assert.Equal("13", weapon.Damage);
        vm.SelectedRecipe = weapon.Recipes.Single(r => !r.IsTempering); await vm.ApplyTuningAsync(vm.PreviewStandardRecipe(false)); Assert.Equal("2", vm.SelectedRecipe!.Ingredients[0].Count);
        vm.SelectedRecipePreset = saved; await vm.ApplyTuningAsync(vm.PreviewPersonalRecipe(false)); Assert.Equal("10", vm.SelectedRecipe!.Ingredients[0].Count); Assert.Equal("13", weapon.Damage); Assert.Single(weapon.Recipes);
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); var recipeId = vm.SelectedRecipe.ConfirmedFormId;
        vm.RecipeSources.Add(new(new("Skyrim.esm", 0xFA10), material.Reference!, "DonorSword", "Donor sword", WeaponKind.OneHandSword, SmartWeaponSubtype.Sword, material.Crafting!.Recipe, ""));
        vm.RecipeSourceSearch = "Donor"; vm.SelectedRecipeSource = Assert.Single(vm.RecipeSourceChoices); await vm.ApplyTuningAsync(vm.PreviewDonorRecipe(false)); await vm.SaveItemCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Error); Assert.Equal(recipeId, vm.SelectedRecipe!.ConfirmedFormId);
        await using var reopened = SmartEditor(recipePresets: new JsonSmartRecipePresetRepository(Path.Combine(_root, "personal-recipes"))); await reopened.InitializeAsync(vm.ProjectPath);
        Assert.Equal("", reopened.Error); Assert.Single(reopened.PersonalRecipeChoices); Assert.Equal(SmartHandlingMode.Heavy, reopened.SelectedItem!.Tuning!.Handling); Assert.Equal(SmartBalanceMode.MuchStronger, reopened.SelectedItem.Tuning.Mode);
        Assert.Equal("10.8", reopened.SelectedItem.Weight); Assert.Equal("13", reopened.SelectedItem.Damage);
        reopened.SelectedRecipePreset = reopened.PersonalRecipeChoices.Single(); await reopened.DeleteRecipePresetAsync(); Assert.Empty(await repo.LoadAsync()); Assert.Single(reopened.SelectedItem.Recipes);
    }
    [Fact]
    public async Task NumericReferenceChangesOnlyStatsAndUsesSameBalanceMode()
    {
        await using var vm = await PrepareSmartAsync(); var preset = TestMaterial(vm); await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true));
        var item = vm.SelectedItem!; var models = (item.ModelPath, item.FirstPersonPath, item.Name, item.EditorId); var recipes = JsonSerializer.Serialize(item.Capture().Recipes);
        await vm.ApplyTuningAsync(vm.PreviewBalance(SmartBalanceMode.MuchStronger, false));
        vm.SelectedReference = vm.ReferenceChoices.First(); var source = vm.SelectedReference; await vm.ApplyTuningAsync(vm.PreviewReference(false)); Assert.Equal("", vm.Error);
        Assert.Equal(SmartWeaponBalance.Calculate(new(source.Damage, source.Weight, source.Value, source.Details.Speed, source.Details.Reach), SmartBalanceMode.MuchStronger).Damage.ToString(), item.Damage);
        Assert.Equal(models, (item.ModelPath, item.FirstPersonPath, item.Name, item.EditorId)); Assert.Equal(recipes, JsonSerializer.Serialize(item.Capture().Recipes)); Assert.Null(item.Tuning!.PresetId);
    }
}
