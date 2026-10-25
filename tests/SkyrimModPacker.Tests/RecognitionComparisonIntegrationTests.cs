using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class AdvancedPluginBuilderTests
{
    [Fact]
    public async Task ComparisonRecipeReaderRetainsUnsupportedRecipeAndDoesNotWritePlugin()
    {
        await PrepareAsync(); var saved = await _builder.SaveRecipeAsync(Recipe()); Assert.True(saved.IsSuccess, saved.Error);
        var mod = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
        ((ConditionFloat)mod.ConstructibleObjects.First().Conditions[0]).CompareOperator = CompareOperator.NotEqualTo; mod.WriteToBinary(Plugin);
        var original = File.ReadAllBytes(Plugin);
        var result = await _builder.GetComparisonRecipesAsync(Plugin, 0x800); Assert.True(result.IsSuccess, result.Error);
        var recipe = Assert.Single(result.Value); Assert.False(recipe.CanCopy); Assert.Contains("HasPerk", recipe.CopyBlock);
        Assert.NotEmpty(recipe.Data.Ingredients); Assert.Equal(original, File.ReadAllBytes(Plugin));
    }
}

public partial class WeaponProjectTests
{
    [Fact]
    public async Task ArchiveScanReadsMatchingPluginHintsAndReportsBrokenPlugin()
    {
        var root = Path.Combine(_root, "hint-assets"); Directory.CreateDirectory(Path.Combine(root, "meshes/Weapons/Iron"));
        File.WriteAllBytes(Path.Combine(root, "meshes/Weapons/Iron/Sword.nif"), PreviewFixtures.Nif(diffuse: ""));
        var plugin = new SkyrimMod(ModKey.FromNameAndExtension("Hints.esp"), SkyrimRelease.SkyrimSE);
        plugin.Weapons.Add(new Weapon(new FormKey(plugin.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "ConfirmedSword", Name = "Named sword", Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword }, Model = new Model { File = "Weapons/Iron/Sword.nif" } });
        plugin.WriteToBinary(Path.Combine(root, "Hints.esp")); File.WriteAllText(Path.Combine(root, "Broken.esp"), "broken");
        var result = await Service.ScanWeaponFolderAsync(root); Assert.True(result.IsSuccess, result.Error); Assert.Single(result.Value.RecordHints); Assert.Contains(result.Value.HintWarnings, w => w.Contains("Broken.esp"));
        var suggestion = Assert.Single(WeaponModelRecognition.Analyze(result.Value)); Assert.Equal(ModelRecognitionStatus.Record, suggestion.Status); Assert.Equal("Named sword", suggestion.Name);
    }
    [Theory]
    [InlineData("Bad ID", "Other")]
    [InlineData("Same", "sAME")]
    public async Task ImportPlanRejectsMalformedOrDuplicateEditorIds(string first, string second)
    {
        var (_, request) = await FolderFixtureAsync(diffuse: "");
        var result = await Service.PreviewWeaponFolderAsync(request with { Items = [new(Guid.NewGuid(), "A", WeaponKind.OneHandSword, request.ThirdPersonSource, null, true) { EditorId = first }, new(Guid.NewGuid(), "B", WeaponKind.OneHandSword, request.ThirdPersonSource, null, true) { EditorId = second }] }, Data);
        Assert.True(result.IsFailure); Assert.Contains("EditorID", result.Error);
    }
}

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task FailedRecognizedImportRemovesNewProjectAndCanRetry()
    {
        await using var fixture = await PrepareSmartAsync();
        var inner = new JsonSmartModDraftRepository(); var repository = Substitute.For<ISmartModDraftRepository>(); var fail = true;
        repository.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c => inner.LoadAsync(c.ArgAt<string>(0), c.ArgAt<string>(1), c.Arg<CancellationToken>()));
        repository.SaveAsync(Arg.Any<SmartModDraft>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            if (fail && c.Arg<SmartModDraft>().Items.Count > 0) { fail = false; return Task.FromResult(Result<DraftWriteReceipt>.Failure("Injected recognized import failure")); }
            return inner.SaveAsync(c.Arg<SmartModDraft>(), c.Arg<string>(), c.Arg<long>(), c.Arg<CancellationToken>());
        });
        await using var vm = new SmartModViewModel(_builder, new WeaponProjectService(_builder), _settings, repository, new PluginValidationService(), history: new WorkspaceHistoryService(Path.Combine(_root, "recognition-history")));
        await vm.InitializeAsync(); vm.Name = "Recognized"; vm.OutputDirectory = Path.Combine(_root, "recognition-start");
        var assets = Path.Combine(_root, "recognition-source"); Directory.CreateDirectory(assets); var nif = Path.Combine(assets, "Sword.nif"); File.WriteAllBytes(nif, PreviewFixtures.Nif(diffuse: ""));
        var request = new WeaponFolderRequest(Guid.NewGuid(), vm.ProposedProjectPath, assets, nif, null, true) { NewProjectPluginName = "Recognized.esp", Items = [new(Guid.NewGuid(), "Named sword", WeaponKind.OneHandSword, nif, null, true) { EditorId = "Author_UniqueSword" }] };
        var plan = await vm.PreviewWeaponFolderAsync(request); await vm.CreateFromWeaponFolderAsync(plan, WeaponKind.OneHandSword);
        Assert.Contains("Injected", vm.Error); Assert.False(vm.HasProject); Assert.Empty(vm.Items); Assert.False(Directory.Exists(Path.GetDirectoryName(request.ProjectPath)));
        await vm.CreateFromWeaponFolderAsync(plan, WeaponKind.OneHandSword); Assert.Equal("", vm.Error); Assert.Equal("Author_UniqueSword", Assert.Single(vm.Items).EditorId);
    }
    [Fact]
    public async Task ComparisonLoadsActualGameTemplateModelsAndRecipeData()
    {
        await using var vm = await PrepareSmartAsync(); var choice = vm.ComparisonChoices.First(c => c.Template?.Kind == WeaponKind.OneHandSword);
        var snapshot = await vm.LoadComparisonAsync(choice, CancellationToken.None); Assert.Equal(choice.Template!.EditorId, snapshot.Fields.EditorId); Assert.NotEmpty(snapshot.Fields.ModelPath);
        Assert.False(snapshot.UseProjectResources); Assert.StartsWith("Skyrim.esm:", snapshot.SourceFingerprint);
        var preview = vm.PreviewComparison(snapshot, new(true, true, false)); await vm.ApplyTuningAsync(preview); Assert.Equal("", vm.Error);
    }
}

public sealed class NewWeaponProjectRollbackTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CleanupNeverRemovesForeignOrChangedFiles(bool changed)
    {
        var root = Path.Combine(Path.GetTempPath(), "NewProjectCleanup", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var original = Path.Combine(root, "mod-project.json"); File.WriteAllText(original, "original");
        try
        {
            var rollback = new NewWeaponProjectRollback(root); var foreign = changed ? original : Path.Combine(root, "foreign.txt"); File.WriteAllText(foreign, "preserve");
            Assert.Throws<IOException>(rollback.Rollback); Assert.Equal("preserve", File.ReadAllText(foreign));
        }
        finally { Directory.Delete(root, true); }
    }
}
