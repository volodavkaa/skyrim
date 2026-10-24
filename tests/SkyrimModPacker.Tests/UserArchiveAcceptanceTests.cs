using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;
namespace SkyrimModPacker.Tests;
public sealed class UserArchiveAcceptanceTests
{
    [Fact]
    public async Task DarkSisterArchiveCreatesOneHandAndTwoHandWeaponsAndRealZip()
    {
        var input = Environment.GetEnvironmentVariable("SKYRIM_WEAPON_ARCHIVE"); var game = Environment.GetEnvironmentVariable("SKYRIM_SE_DATA");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(game)) return;
        var zipHash = SHA256.HashData(File.ReadAllBytes(input));
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/acceptance/user-archive")); Directory.CreateDirectory(root);
        var name = "DarkSisterReview_" + Guid.NewGuid().ToString("N")[..8];
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplicationServices(); services.AddInfrastructureServices(Path.Combine(root, "app-data")); using var provider = services.BuildServiceProvider();
        var settings = Substitute.For<ISettingsRepository>(); settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings { SkyrimDataPath = game, OutputDirectory = root });
        var builder = new PluginBuilderService(); var projects = new WeaponProjectService(builder);
        await using var vm = new SmartModViewModel(builder, projects, settings, new JsonSmartModDraftRepository(), new PluginValidationService(), provider.GetRequiredService<PackingPipeline>(), new WorkspaceHistoryService(Path.Combine(root, "history")));
        await vm.InitializeAsync(); Assert.Equal("", vm.Error); vm.Name = name; vm.OutputDirectory = root;
        await using var source = await vm.ExtractWeaponArchiveAsync(input); Assert.NotNull(source);
        var models = Directory.GetFiles(source.Root, "*.nif", SearchOption.AllDirectories); var sword = models.Single(p => Path.GetFileName(p) == "DarkSister_HD.nif"); var greatsword = models.Single(p => Path.GetFileName(p) == "DarkSister_HD_2h.nif");
        var request = new WeaponFolderRequest(Guid.NewGuid(), vm.ProposedProjectPath, source.Root, sword, null, true) { NewProjectPluginName = name + ".esp", Items = [
            new(Guid.NewGuid(), "Dark Sister", WeaponKind.OneHandSword, sword, null, true), new(Guid.NewGuid(), "Dark Sister — Two-handed", WeaponKind.Greatsword, greatsword, null, true)] };
        var plan = await vm.PreviewWeaponFolderAsync(request); Assert.True(plan.CanImport, string.Join("; ", plan.Warnings.Concat(plan.Dependencies.Select(d => d.Path + ": " + d.Status)))); Assert.False(plan.Incomplete);
        Assert.Equal(2, plan.Items.Count); Assert.Equal(2, plan.Files.Count(f => f.RelativePath.EndsWith(".nif"))); Assert.Equal(3, plan.Files.Count(f => f.RelativePath.EndsWith(".dds")));
        await vm.CreateFromWeaponFolderAsync(plan, WeaponKind.OneHandSword); Assert.Equal("", vm.Error); Assert.True(File.Exists(vm.PluginPath)); Assert.Equal(2, vm.Items.Count);
        await vm.PackCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.NotNull(vm.LastArchivePath);
        Assert.Equal(2, (await builder.GetWeaponsAsync(vm.PluginPath)).Value.Count);
        using (var zip = ZipFile.OpenRead(vm.LastArchivePath))
        {
            Assert.Equal(2, zip.Entries.Count(e => e.FullName.EndsWith(".nif"))); Assert.Equal(3, zip.Entries.Count(e => e.FullName.EndsWith(".dds")));
            foreach (var file in plan.Files) { using var stream = zip.GetEntry(file.RelativePath)!.Open(); Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(stream))); }
        }
        Assert.Equal(zipHash, SHA256.HashData(File.ReadAllBytes(input)));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/audit")); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "user-archive-acceptance.json"), JsonSerializer.Serialize(new { archive = input, sourceSha256 = Convert.ToHexString(zipHash), vm.ProjectPath, vm.PluginPath, vm.LastArchivePath,
            items = vm.Items.Select(i => new { i.Name, i.Kind, i.EditorId, i.ConfirmedFormId, i.ModelPath, i.FirstPersonPath }), plan.Files, plan.Dependencies }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
