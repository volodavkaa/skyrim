using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Infrastructure.Esl;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public class EslPackingTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "EslPackingTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_testRoot, recursive: true);

    private (ServiceProvider Provider, ModProfile Profile, AppSettings Settings, string PluginPath) Setup(bool useTemp, bool enableEsl, bool compatible)
    {
        var dataPath = Path.Combine(_testRoot, "Game", "Data");
        Directory.CreateDirectory(dataPath);
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Test.esp"), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Stats.Version = 1.70f;
        mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, compatible ? 0x800u : 0x1000u), SkyrimRelease.SkyrimSE));
        var pluginPath = Path.Combine(dataPath, "Test.esp");
        mod.WriteToBinary(pluginPath);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddInfrastructureServices(Path.Combine(_testRoot, "appdata"));
        return (services.BuildServiceProvider(),
            new ModProfile { Name = "Test", Version = "1.0", PluginFileName = "Test.esp", EnableEsl = enableEsl },
            new AppSettings { SkyrimDataPath = dataPath, OutputDirectory = Path.Combine(_testRoot, "output"), UseTempDirectory = useTemp, OverwriteExistingArchive = true },
            pluginPath);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public async Task Pipeline_OptionalEsl_PreservesGameAndExcludesBackups(bool useTemp, bool enableEsl, bool compatible)
    {
        var setup = Setup(useTemp, enableEsl, compatible);
        using var provider = setup.Provider;
        var original = await File.ReadAllBytesAsync(setup.PluginPath);
        var pipeline = provider.GetRequiredService<PackingPipeline>();

        var result = await pipeline.ExecuteAsync(setup.Profile, setup.Settings);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(original, await File.ReadAllBytesAsync(setup.PluginPath));
        Assert.Single(Directory.GetFiles(setup.Settings.SkyrimDataPath));
        if (result.StagingDirectory != null)
            Assert.False(Directory.Exists(result.StagingDirectory));

        using (var archive = ZipFile.OpenRead(result.OutputArchivePath))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("Test.esp", entry.FullName);
            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var packedBytes = buffer.ToArray();
            Assert.Equal(enableEsl, (BitConverter.ToUInt32(packedBytes, 8) & 0x200) != 0);
            if (!enableEsl)
                Assert.Equal(original, packedBytes);
            var inspectDirectory = Path.Combine(_testRoot, "inspect");
            Directory.CreateDirectory(inspectDirectory);
            var inspectPath = Path.Combine(inspectDirectory, "Test.esp");
            await File.WriteAllBytesAsync(inspectPath, packedBytes);
            using var packedMod = SkyrimMod.CreateFromBinaryOverlay(inspectPath, SkyrimRelease.SkyrimSE);
            Assert.Equal(enableEsl, packedMod.IsSmallMaster);
            Assert.Equal(compatible ? 0x800u : 0x1000u, Assert.Single(packedMod.EnumerateMajorRecords()).FormKey.ID);
        }

        var backupRoot = result.OutputArchivePath + ".backups";
        if (enableEsl)
        {
            var backupPath = Assert.Single(Directory.GetFiles(backupRoot, "*.bak", SearchOption.AllDirectories));
            Assert.Equal(original, await File.ReadAllBytesAsync(backupPath));
            Assert.Contains(result.LogMessages, m => m.Contains(backupPath));
            // Overwriting the archive preserves each build's backup independently.
            var second = await pipeline.ExecuteAsync(setup.Profile, setup.Settings);
            Assert.True(second.IsSuccess, string.Join("; ", second.Errors));
            Assert.Equal(2, Directory.GetFiles(backupRoot, "*.bak", SearchOption.AllDirectories).Length);
        }
        else
        {
            Assert.False(Directory.Exists(backupRoot));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pipeline_IncompatibleEsl_StopsBeforeArchivingAndCleansStaging(bool useTemp)
    {
        var setup = Setup(useTemp, true, false);
        using var provider = setup.Provider;
        var original = await File.ReadAllBytesAsync(setup.PluginPath);
        var result = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(setup.Profile, setup.Settings);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.PluginDiagnostics, d => d.Code == "ESL_RANGE" && d.Severity == DiagnosticSeverity.Error);
        Assert.False(File.Exists(result.OutputArchivePath));
        Assert.False(Directory.Exists(result.OutputArchivePath + ".backups"));
        Assert.False(Directory.Exists(result.StagingDirectory));
        Assert.Equal(original, await File.ReadAllBytesAsync(setup.PluginPath));
    }

    [Fact]
    public async Task Pipeline_EslOutputInsideGame_IsRejectedBeforeCreatingFiles()
    {
        var setup = Setup(true, true, true);
        using var provider = setup.Provider;
        setup.Settings.OutputDirectory = Path.Combine(_testRoot, "Game", "output");
        var result = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(setup.Profile, setup.Settings);
        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(setup.Settings.OutputDirectory));
        Assert.Contains(result.Errors, e => e.Contains("game directory"));
    }

    [Fact]
    public async Task Profile_EslOption_RoundTripsThroughEditingAndDuplication()
    {
        var repo = new JsonProfileRepository(_testRoot);
        await repo.SaveAsync(new ModProfile { Name = "Sword", PluginFileName = "Sword.esp", EnableEsl = true });
        var vm = new ProfilesViewModel(repo);
        await vm.LoadProfilesAsync();
        Assert.True(vm.EnableEsl);
        await vm.SaveProfileCommand.ExecuteAsync(null);
        await vm.DuplicateProfileCommand.ExecuteAsync(null);
        var profiles = await repo.LoadAllAsync();
        Assert.Equal(2, profiles.Count);
        Assert.All(profiles, profile => Assert.True(profile.EnableEsl));
        vm.EnableEsl = false;
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.False((await repo.LoadAllAsync()).Single(p => p.Id == vm.SelectedProfile!.Id).EnableEsl);
    }

    [Fact]
    public async Task EslStep_RejectsGameAsStaging_WithoutDeletingIt()
    {
        var setup = Setup(true, true, true);
        using var provider = setup.Provider;
        var original = await File.ReadAllBytesAsync(setup.PluginPath);
        var context = new PackingContext
        {
            Profile = setup.Profile,
            Settings = setup.Settings,
            StagingDirectory = setup.Settings.SkyrimDataPath
        };
        Assert.False(await new EslFlagStep(new EslService()).ExecuteAsync(context));
        Assert.Equal(original, await File.ReadAllBytesAsync(setup.PluginPath));
        Assert.Contains(context.Errors, e => e.Contains("game directory"));
    }

    [Fact]
    public async Task LegacyProfile_WithoutEslOption_DefaultsToDisabled()
    {
        var repo = new JsonProfileRepository(_testRoot);
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "profiles.json"), "[{\"name\":\"Legacy\",\"pluginFileName\":\"Legacy.esp\"}]");
        Assert.False(Assert.Single(await repo.LoadAllAsync()).EnableEsl);
    }
}
