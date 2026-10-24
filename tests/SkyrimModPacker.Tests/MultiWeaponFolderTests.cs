using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;
namespace SkyrimModPacker.Tests;
public sealed class ImportDependencyTests
{
    [Theory]
    [InlineData("hidden", true)] [InlineData("hiddenUnknown", false)] [InlineData("hiddenCorrupt", false)]
    public void HiddenTextureSlotsAreInspectedWithoutDrawingHiddenMeshes(string scenario, bool complete)
    {
        var bytes = PreviewFixtures.Nif(scenario); var hash = SHA256.HashData(bytes); var scene = SseNifReader.Read(bytes);
        Assert.Single(scene.Meshes); Assert.Equal(complete, scene.TextureDependenciesComplete); Assert.Contains("textures/test.dds", scene.TexturePaths);
        if (scenario != "hiddenCorrupt") Assert.Contains("textures/hidden.dds", scene.TexturePaths);
        if (!complete) Assert.Contains(scene.Warnings, w => w.Contains("Shader") || w.Contains("Texture"));
        Assert.Equal(hash, SHA256.HashData(bytes));
    }
}
public partial class WeaponsViewModelTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MultiWeaponImportCreatesBothTypesInOnePluginAndDeduplicatesResources(bool sharedNif)
    {
        await using var fixture = await PrepareSmartAsync();
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplicationServices(); services.AddInfrastructureServices(Path.Combine(_root, "multi-pipeline")); using var provider = services.BuildServiceProvider();
        await using var vm = SmartEditor(provider.GetRequiredService<PackingPipeline>()); await vm.InitializeAsync(); vm.Name = "MultiWeapons"; vm.OutputDirectory = Path.Combine(_root, "multi-output");
        var source = Path.Combine(_root, "multi-assets"); Directory.CreateDirectory(Path.Combine(source, "meshes", "Custom")); Directory.CreateDirectory(Path.Combine(source, "textures", "Custom"));
        var sword = Path.Combine(source, "meshes", "Custom", "Sword.nif"); var greatsword = sharedNif ? sword : Path.Combine(source, "meshes", "Custom", "Greatsword.nif");
        File.WriteAllBytes(sword, PreviewFixtures.Nif(diffuse: "textures/Custom/Blade.dds")); if (!sharedNif) File.WriteAllBytes(greatsword, PreviewFixtures.Nif(diffuse: "textures/Custom/Blade.dds"));
        File.WriteAllBytes(Path.Combine(source, "textures", "Custom", "Blade.dds"), PreviewFixtures.Bc7());
        var request = new WeaponFolderRequest(Guid.NewGuid(), vm.ProposedProjectPath, source, sword, null, true) { NewProjectPluginName = vm.Name + ".esp", Items = [
            new(Guid.NewGuid(), "One handed weapon", WeaponKind.OneHandSword, sword, null, true), new(Guid.NewGuid(), "Two handed weapon", WeaponKind.Greatsword, greatsword, null, true)] };
        var plan = await vm.PreviewWeaponFolderAsync(request); Assert.True(plan.CanImport); Assert.Equal(2, plan.Items.Count); Assert.Equal(sharedNif ? 2 : 3, plan.Files.Count);
        Assert.False(Directory.Exists(Path.GetDirectoryName(vm.ProposedProjectPath))); await vm.CreateFromWeaponFolderAsync(plan, WeaponKind.OneHandSword); Assert.Equal("", vm.Error);
        Assert.Equal(2, vm.Items.Count); Assert.Equal(2, vm.Items.Select(i => i.EditorId).Distinct().Count()); Assert.Contains(vm.Items, i => i.Kind == WeaponKind.Greatsword && i.Name == "Two handed weapon");
        await vm.ImportWeaponFolderAsync(plan, true, WeaponKind.OneHandSword); Assert.Equal("", vm.Error); Assert.Equal(2, vm.Items.Count);
        vm.EnableEsl = sharedNif; await vm.PackCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.NotNull(vm.LastArchivePath);
        var weapons = (await _builder.GetWeaponsAsync(vm.PluginPath)).Value; Assert.Equal(2, weapons.Count); Assert.Contains(weapons, w => w.Kind == WeaponKind.OneHandSword); Assert.Contains(weapons, w => w.Kind == WeaponKind.Greatsword);
        using var zip = ZipFile.OpenRead(vm.LastArchivePath); Assert.Equal(sharedNif ? 1 : 2, zip.Entries.Count(e => e.FullName.EndsWith(".nif"))); Assert.Single(zip.Entries, e => e.FullName.EndsWith(".dds"));
        foreach (var file in plan.Files) { using var input = zip.GetEntry(file.RelativePath)!.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output); Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(output.ToArray()))); }
        await using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal("", reopened.Error); Assert.Equal(2, reopened.Items.Count); Assert.All(reopened.Items, i => Assert.Equal(request.Id, i.Capture().LastFolderPlanId));
    }
}
public partial class WeaponProjectTests
{
    [Fact]
    public async Task OccupiedImportDestinationCanBeRenamedWithoutChangingExistingFiles()
    {
        var source = Path.Combine(_root, "new-source"); Directory.CreateDirectory(source); var model = Path.Combine(source, "Sword.nif"); File.WriteAllBytes(model, PreviewFixtures.Nif(diffuse: ""));
        var parent = Path.Combine(_root, "new-output"); var occupied = Path.GetDirectoryName(WeaponImportDestination.ProjectPath(parent, "Taken"))!; Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "user-data.txt"), "preserve me");
        var request = new WeaponFolderRequest(Guid.NewGuid(), Path.Combine(occupied, "mod-project.json"), source, model, null, true) { NewProjectPluginName = "Taken.esp" };
        var failed = await Service.PreviewWeaponFolderAsync(request, Data); Assert.True(failed.IsFailure); Assert.Contains("папка вже існує", failed.Error);
        var suggested = WeaponImportDestination.AvailableName(parent, "Taken"); Assert.Equal("Taken_2", suggested);
        var path = WeaponImportDestination.ProjectPath(parent, suggested); var plan = await Service.PreviewWeaponFolderAsync(request with { ProjectPath = path, NewProjectPluginName = suggested + ".esp" }, Data);
        Assert.True(plan.IsSuccess, plan.Error); Assert.True(plan.Value.CanImport); Assert.False(Directory.Exists(Path.GetDirectoryName(path))); Assert.Equal("preserve me", File.ReadAllText(Path.Combine(occupied, "user-data.txt")));
        Assert.ThrowsAny<Exception>(() => WeaponImportDestination.ProjectPath(parent, "../outside"));
    }
}
