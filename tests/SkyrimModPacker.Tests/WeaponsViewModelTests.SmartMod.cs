using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private SmartModViewModel SmartEditor(PackingPipeline? pipeline = null) => new(_builder, new WeaponProjectService(_builder), _settings,
        new JsonSmartModDraftRepository(), new PluginValidationService(), pipeline,
        new WorkspaceHistoryService(Path.Combine(_root, "smart-history")));

    private async Task<SmartModViewModel> PrepareSmartAsync(PackingPipeline? pipeline = null)
    {
        AddModelTemplate(); AddStage4UiMaster(); AddInheritedUiProperties(); await PrepareGuardedEditorAsync();
        foreach (var path in new[] { @"Weapons\Iron\LongSword.nif", @"Weapons\Iron\First.nif" })
        {
            var destination = Path.Combine(DataPath, "meshes", path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllBytes(destination, PreviewFixtures.Nif(diffuse: ""));
        }
        var vm = SmartEditor(pipeline); vm.OutputDirectory = Path.Combine(_root, "smart"); vm.Name = "SmartWeapons";
        await vm.InitializeAsync(); Assert.Equal("", vm.Error);
        vm.Types.Single(t => t.Kind == WeaponKind.OneHandSword).Count = "2";
        vm.Types.Single(t => t.Kind == WeaponKind.Greatsword).Count = "1";
        await vm.CreateProjectCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        return vm;
    }

    [Fact]
    public async Task SmartMod_QueueAndRawIndependentDraftsResumeAtSelectedItemAndRecipe()
    {
        using var vm = await PrepareSmartAsync();
        Assert.Equal(3, vm.Items.Count); Assert.Equal(3, vm.Items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(2, vm.Items.Count(i => i.Kind == WeaponKind.OneHandSword));
        Assert.DoesNotContain(vm.Types, t => t.Kind == WeaponKind.Bow);
        var plugin = File.ReadAllBytes(vm.PluginPath);
        vm.SelectedItem = vm.Items[0]; vm.SelectedItem.Name = "Перший"; vm.SelectedItem.Damage = "незавершене";
        vm.SelectedItem = vm.Items[1]; vm.SelectedItem.Name = "Другий"; vm.SelectedItem.Weight = "2,";
        vm.AddRecipeCommand.Execute("crafting"); vm.AddIngredientCommand.Execute(null);
        vm.SelectedRecipe!.Ingredients[0].Count = "поки що";
        vm.ListLevel = "незавершений рівень"; vm.ListCount = "2,";
        vm.SelectedItem.Step = SmartModItemStep.Acquisition;
        Assert.True(await vm.SaveDraftAsync());
        using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath);
        Assert.Equal("", reopened.Error); Assert.Equal(vm.SelectedItem.Id, reopened.SelectedItem!.Id);
        Assert.Equal("незавершене", reopened.Items[0].Damage); Assert.Equal("2,", reopened.Items[1].Weight);
        Assert.Equal("поки що", reopened.SelectedRecipe!.Ingredients[0].Count);
        Assert.Equal("незавершений рівень", reopened.ListLevel); Assert.Equal("2,", reopened.ListCount);
        Assert.Equal(SmartModItemStep.Acquisition, reopened.SelectedItem.Step);
        Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath));
    }

    [Fact]
    public async Task SmartMod_DuplicateHasNewIdentities_OptionalRecipesAndSharedFiles()
    {
        using var vm = await PrepareSmartAsync();
        var source = vm.SelectedItem!;
        vm.AddRecipeCommand.Execute("crafting"); vm.AddIngredientCommand.Execute(null);
        vm.SelectedRecipe!.Ingredients[0].Reference = vm.Options.Ingredients.First().Reference;
        var recipeId = vm.SelectedRecipe.Id;
        await vm.DuplicateSelectedAsync(true); Assert.Equal("", vm.Error);
        var copy = vm.SelectedItem!;
        Assert.NotEqual(source.Id, copy.Id); Assert.NotEqual(source.EditorId, copy.EditorId);
        Assert.NotEqual(recipeId, Assert.Single(copy.Recipes).Id);
        Assert.Equal(source.ModelPath, copy.ModelPath); Assert.Null(copy.ConfirmedFormId);
        copy.Name = "Незалежна копія"; Assert.NotEqual(source.Name, copy.Name);
        await vm.DuplicateSelectedAsync(false); Assert.Empty(vm.SelectedItem!.Recipes);
        Assert.Equal(5, vm.Items.Count);
    }

    [Fact]
    public async Task SmartMod_SaveRenameDeleteRecords_KeepsInheritanceAndOtherItems()
    {
        using var vm = await PrepareSmartAsync();
        var first = vm.SelectedItem!; var inherited = first.Fields;
        var source = Path.Combine(_root, "shared.nif"); File.WriteAllBytes(source, PreviewFixtures.Nif(diffuse: ""));
        await vm.ImportModelAsync(source, false); Assert.Equal("", vm.Error);
        Assert.Equal(inherited.Sound, first.Sound); Assert.Equal(inherited.Keywords, first.Fields.Keywords);
        vm.AddRecipeCommand.Execute("crafting"); vm.AddIngredientCommand.Execute(null);
        vm.SelectedRecipe!.Ingredients[0].Reference = vm.Options.Ingredients.First().Reference;
        vm.SelectedRecipe.Ingredients[0].Count = "2";
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        Assert.NotNull(first.ConfirmedFormId); Assert.NotNull(vm.SelectedRecipe.ConfirmedFormId);
        first.Name = "Перейменований";
        Assert.Contains(vm.RecipeLinks, r => r.DisplayName.StartsWith("Перейменований"));
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        var record = Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value);
        Assert.Equal("Перейменований", record.Name);
        Assert.Equal(inherited.Keywords, record.Details.Keywords);
        Assert.Equal(inherited.Sound, record.Details.AttackSound);
        var modelPath = Path.Combine(vm.ProjectDirectory, "meshes", first.ModelPath.Replace('\\', Path.DirectorySeparatorChar));
        await vm.DeleteSelectedAsync(); Assert.Equal("", vm.Error);
        Assert.Equal(2, vm.Items.Count); Assert.Empty((await _builder.GetWeaponsAsync(vm.PluginPath)).Value);
        Assert.True(File.Exists(modelPath));
    }

    [Fact]
    public async Task SmartMod_InvalidFieldsAndRecipes_BlockWritesAndNavigateToItem()
    {
        using var vm = await PrepareSmartAsync(); var before = File.ReadAllBytes(vm.PluginPath);
        vm.SelectedItem!.Damage = "NaN";
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.NotEqual("", vm.Error);
        Assert.Equal(before, File.ReadAllBytes(vm.PluginPath)); Assert.Contains(nameof(SmartModItemRow.Damage), vm.SelectedItem.Errors.Keys);
        await vm.CheckCommand.ExecuteAsync(null);
        var issue = vm.Issues.First(i => i.ItemId == vm.SelectedItem.Id);
        vm.NavigateIssueCommand.Execute(issue); Assert.True(vm.IsPropertiesStep);
        vm.SelectedItem.Damage = "7"; vm.AddRecipeCommand.Execute("crafting"); vm.AddIngredientCommand.Execute(null);
        vm.SelectedRecipe!.Ingredients[0].Reference = vm.Options.Ingredients.First().Reference; vm.SelectedRecipe.Ingredients[0].Count = "0";
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.NotEqual("", vm.Error);
        Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
    }

    [Fact]
    public async Task SmartMod_ExternalWorkspaceChangeBlocksWrite_UntilExplicitReview()
    {
        using var vm = await PrepareSmartAsync();
        var changed = await _builder.CloneSwordAsync(new() { PluginPath = vm.PluginPath, GameDataPath = DataPath,
            ProjectFilePath = vm.ProjectPath, TemplateFormId = 0x12EB7, EditorId = "ExternalWeapon", Name = "External" });
        Assert.True(changed.IsSuccess, changed.Error); var before = File.ReadAllBytes(vm.PluginPath);
        vm.SelectedItem!.Name = "Pending";
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Contains("Smart_ExternalChanged", vm.Error);
        Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
        await vm.ReviewExternalChangesCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        Assert.Equal(2, (await _builder.GetWeaponsAsync(vm.PluginPath)).Value.Count);
    }

    [Fact]
    public async Task SmartMod_RestoreSoundsAndModels_IsDraftOnly_AndPreservesName()
    {
        using var vm = await PrepareSmartAsync(); var item = vm.SelectedItem!;
        var expectedSound = item.Sound; var expectedModel = item.ModelPath; var before = File.ReadAllBytes(vm.PluginPath);
        item.Name = "Own name"; item.Sound = null; item.SetKeywords([]); item.ModelPath = "custom.nif";
        await vm.RestoreTemplateCommand.ExecuteAsync("sounds"); await vm.RestoreTemplateCommand.ExecuteAsync("keywords");
        await vm.RestoreTemplateCommand.ExecuteAsync("models"); Assert.Equal("", vm.Error);
        Assert.Equal(expectedSound, item.Sound); Assert.NotEmpty(item.Fields.Keywords); Assert.Equal(expectedModel, item.ModelPath);
        Assert.False(string.IsNullOrEmpty(item.FirstPersonPath)); Assert.Equal("Own name", item.Name);
        Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmartMod_PacksEntireQueueWithRecipesAndSharedResources_AndRetainsFinalPath(bool esl)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplicationServices();
        services.AddInfrastructureServices(Path.Combine(_root, "pipeline"));
        using var provider = services.BuildServiceProvider();
        using var vm = await PrepareSmartAsync(provider.GetRequiredService<PackingPipeline>());
        var masterBytes = File.ReadAllBytes(Path.Combine(DataPath, "Skyrim.esm"));
        var source = Path.Combine(_root, "shared.nif"); File.WriteAllBytes(source, PreviewFixtures.Nif(diffuse: ""));
        await vm.ImportModelAsync(source, false); var shared = vm.SelectedItem!.ModelPath;
        foreach (var item in vm.Items.ToArray())
        {
            vm.SelectedItem = item; item.ModelPath = shared;
            vm.AddRecipeCommand.Execute("crafting"); vm.AddIngredientCommand.Execute(null);
            vm.SelectedRecipe!.Ingredients[0].Reference = vm.Options.Ingredients.First().Reference;
            vm.SelectedRecipe.Ingredients[0].Count = "2";
        }
        vm.EnableEsl = esl;
        await vm.PackCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        Assert.NotNull(vm.LastArchivePath); Assert.True(vm.IsFinished); Assert.True(File.Exists(vm.LastArchivePath));
        using var zip = ZipFile.OpenRead(vm.LastArchivePath);
        Assert.Single(zip.Entries, e => e.FullName == "meshes/" + shared.Replace('\\', '/'));
        Assert.Contains(zip.Entries, e => e.FullName == "mod-report.json");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("draft", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".bak"));
        Assert.Equal(3, (await _builder.GetWeaponsAsync(vm.PluginPath)).Value.Count);
        foreach (var item in vm.Items) Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, item.ConfirmedFormId!.Value)).Value);
        Assert.Equal(masterBytes, File.ReadAllBytes(Path.Combine(DataPath, "Skyrim.esm")));
        var archivePath = vm.LastArchivePath; vm.NavigateCommand.Execute("Overview"); Assert.Equal(archivePath, vm.LastArchivePath);
        Assert.True(await vm.SaveDraftAsync());
        using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath);
        Assert.Equal("", reopened.Error); Assert.Equal(archivePath, reopened.LastArchivePath);
    }

    [Fact]
    public async Task SmartMod_RecipeWriteFailureRollsBackWeaponAndMetadata_WithoutLosingDraft()
    {
        using var vm = await PrepareSmartAsync();
        var beforePlugin = File.ReadAllBytes(vm.PluginPath); var beforeProject = File.ReadAllBytes(vm.ProjectPath);
        vm.SelectedItem!.Name = "Збережена чернетка";
        vm.AddRecipeCommand.Execute("crafting"); vm.AddIngredientCommand.Execute(null);
        vm.SelectedRecipe!.Ingredients[0].Reference = new("Skyrim.esm", 0xDEAD);
        await vm.SaveItemCommand.ExecuteAsync(null);
        Assert.NotEqual("", vm.Error); Assert.Contains("Smart_RolledBack", vm.Status);
        Assert.Equal(beforePlugin, File.ReadAllBytes(vm.PluginPath)); Assert.Equal(beforeProject, File.ReadAllBytes(vm.ProjectPath));
        Assert.Equal("Збережена чернетка", vm.SelectedItem!.Name); Assert.Null(vm.SelectedItem.ConfirmedFormId);
        Assert.Null(vm.SelectedRecipe!.ConfirmedFormId);
        vm.SelectedRecipe.Ingredients[0].Reference = vm.Options.Ingredients.First().Reference;
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value);
        Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, vm.SelectedItem.ConfirmedFormId!.Value)).Value);
    }

    [Fact]
    public async Task SmartMod_CheckFindsExternallyRemovedConfirmedItem_AndMarksItForCorrection()
    {
        using var vm = await PrepareSmartAsync();
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        var item = vm.SelectedItem!;
        var deleted = await _builder.DeleteSwordAsync(vm.PluginPath, DataPath, item.ConfirmedFormId!.Value, projectFilePath: vm.ProjectPath, deleteRecipes: true);
        Assert.True(deleted.IsSuccess, deleted.Error);
        await vm.CheckCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        Assert.Contains(vm.Issues, i => i.ItemId == item.Id && i.IsError && i.Message.Contains("Smart_RecordMissing"));
        Assert.Equal("Smart_State_NeedsCorrection", item.StateText);
        await vm.DeleteSelectedAsync(); Assert.Equal("", vm.Error); Assert.DoesNotContain(vm.Items, i => i.Id == item.Id);
        await vm.ReviewExternalChangesCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
    }
}
