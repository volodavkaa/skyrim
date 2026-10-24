using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SmartFolderMaterialBalancePackPreservesBc7AndSharedModel(bool esl)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplicationServices(); services.AddInfrastructureServices(Path.Combine(_root, "smart-folder-pack"));
        using var provider = services.BuildServiceProvider(); await using var vm = await PrepareSmartAsync(provider.GetRequiredService<PackingPipeline>());
        var preset = TestMaterial(vm); var root = Path.Combine(_root, "folder-pack-assets");
        var model = Path.Combine(root, "meshes", "Custom", "blade.nif"); var texture = Path.Combine(root, "textures", "Custom", "blade.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!); Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
        File.WriteAllBytes(model, PreviewFixtures.Nif(diffuse: "textures/Custom/blade.dds")); File.WriteAllBytes(texture, PreviewFixtures.Bc7());
        var hashes = new[] { model, texture, Path.Combine(DataPath, "Skyrim.esm") }.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        var plan = await vm.PreviewWeaponFolderAsync(new(Guid.NewGuid(), vm.ProjectPath, root, model, null, true));
        await vm.ImportWeaponFolderAsync(plan, false, WeaponKind.OneHandSword); Assert.Equal("", vm.Error);
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true)); await vm.ApplyTuningAsync(vm.PreviewBalance(SmartBalanceMode.Stronger, false));
        Assert.Equal("", vm.Error); var item = vm.SelectedItem!; Assert.Equal("12", item.Damage);
        vm.EnableEsl = esl; await vm.PackCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.NotNull(vm.LastArchivePath);
        using var zip = ZipFile.OpenRead(vm.LastArchivePath);
        foreach (var file in plan.Files)
        {
            var entry = Assert.Single(zip.Entries, e => e.FullName == file.RelativePath);
            using var input = entry.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output);
            Assert.Equal(hashes[file.SourcePath], SHA256.HashData(output.ToArray()));
        }
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("draft", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".bak"));
        Assert.Equal(2, (await _builder.GetRecipesAsync(vm.PluginPath, item.ConfirmedFormId!.Value)).Value.Count);
        foreach (var hash in hashes) Assert.Equal(hash.Value, SHA256.HashData(File.ReadAllBytes(hash.Key)));
        var outputFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/acceptance/smart-folder-material-balance")); Directory.CreateDirectory(outputFolder);
        File.Copy(vm.LastArchivePath, Path.Combine(outputFolder, esl ? "FixtureWeapon-esl.zip" : "FixtureWeapon-esp.zip"), true);
        await using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal("", reopened.Error);
        var restored = reopened.Items.Single(i => i.Id == item.Id); Assert.Equal("12", restored.Damage); Assert.Equal(SmartBalanceMode.Stronger, restored.Tuning!.Mode);
    }

    [Fact]
    public async Task SmartFolderDraftFailureRestoresUiAndDiskAndAllowsRetry()
    {
        await using var original = await PrepareSmartAsync(); Assert.True(await original.SaveDraftAsync());
        var inner = new JsonSmartModDraftRepository(); var repository = Substitute.For<ISmartModDraftRepository>(); var armed = false;
        repository.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c => inner.LoadAsync(c.ArgAt<string>(0), c.ArgAt<string>(1), c.Arg<CancellationToken>()));
        repository.SaveAsync(Arg.Any<SmartModDraft>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            if (armed && c.Arg<SmartModDraft>().Items.Any(i => i.LastFolderPlanId != null)) { armed = false; return Task.FromResult(Result<DraftWriteReceipt>.Failure("Injected folder draft failure")); }
            return inner.SaveAsync(c.Arg<SmartModDraft>(), c.Arg<string>(), c.Arg<long>(), c.Arg<CancellationToken>());
        });
        await using var vm = new SmartModViewModel(_builder, new WeaponProjectService(_builder), _settings, repository, new PluginValidationService(),
            history: new WorkspaceHistoryService(Path.Combine(_root, "folder-failure-history")));
        await vm.InitializeAsync(original.ProjectPath); Assert.Equal("", vm.Error);
        var root = Path.Combine(_root, "folder-failure-assets"); Directory.CreateDirectory(root); var model = Path.Combine(root, "new.nif"); File.WriteAllBytes(model, PreviewFixtures.Nif(diffuse: ""));
        var plan = await vm.PreviewWeaponFolderAsync(new(Guid.NewGuid(), vm.ProjectPath, root, model, null, true));
        var before = File.ReadAllBytes(vm.ProjectPath); var plugin = File.ReadAllBytes(vm.PluginPath); var count = vm.Items.Count; var selected = vm.SelectedItem!.Id;
        armed = true; await vm.ImportWeaponFolderAsync(plan, true, WeaponKind.OneHandSword); Assert.NotEmpty(vm.Error);
        Assert.Equal(count, vm.Items.Count); Assert.Equal(selected, vm.SelectedItem!.Id); Assert.Equal(before, File.ReadAllBytes(vm.ProjectPath)); Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath));
        foreach (var file in plan.Files) Assert.False(File.Exists(Path.Combine(vm.ProjectDirectory, file.RelativePath)));
        await vm.ImportWeaponFolderAsync(plan, true, WeaponKind.OneHandSword); Assert.Equal("", vm.Error); Assert.Equal(count + 1, vm.Items.Count);
    }

    [Fact]
    public async Task SmartMaterialKeepsManualIngredientsAndLegacyValuesUntilExplicitReplacement()
    {
        await using var vm = await PrepareSmartAsync(); var preset = TestMaterial(vm); var item = vm.SelectedItem!;
        item.ApplyAutomation(item.Capture() with { Tuning = null }); item.Damage = "19";
        Assert.False(vm.DefaultCrafting); await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, true, false, false)); Assert.Equal("19", item.Damage); Assert.Empty(item.Recipes);
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true)); Assert.Equal("10", item.Damage);
        vm.SelectedRecipe = item.Recipes.Single(r => !r.IsTempering); vm.SelectedRecipe.Ingredients[0].Count = "10";
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, true, true, true)); Assert.Equal("10", item.Recipes.Single(r => !r.IsTempering).Ingredients[0].Count);
        await vm.ApplyTuningAsync(vm.PreviewMaterial(preset, false, true, true)); Assert.Equal("2", item.Recipes.Single(r => !r.IsTempering).Ingredients[0].Count);
        await vm.ApplyTuningAsync(vm.PreviewManualTuning()); Assert.Equal(SmartBalanceMode.Custom, item.Tuning!.Mode); Assert.Null(item.Tuning.PresetId); Assert.Equal("10", item.Damage);
    }
}
