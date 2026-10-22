using NSubstitute;
using Serilog.Events;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Logging;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public class LocalizationAndLoggingTests : IDisposable
{
    private readonly string _testDir;

    public LocalizationAndLoggingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void LocalizationService_SetLanguage_UpdatesCurrentLanguageAndFiresEvent()
    {
        var service = new LocalizationService();
        var eventFired = false;
        service.LanguageChanged += () => eventFired = true;

        service.SetLanguage("uk");

        Assert.Equal("uk", service.CurrentLanguage);
        Assert.True(eventFired);

        eventFired = false;
        service.SetLanguage("en");

        Assert.Equal("en", service.CurrentLanguage);
        Assert.True(eventFired);
    }

    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("ua", "uk")]
    [InlineData("ukrainian", "uk")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    public void LocalizationService_NormalizesCultureCodes(string input, string expected)
    {
        var service = new LocalizationService();
        service.SetLanguage(input);

        Assert.Equal(expected, service.CurrentLanguage);
    }

    [Fact]
    public void InMemoryLogSink_CapturesLogEvents_AndFiresEvent()
    {
        var sink = new InMemoryLogSink();
        LogEntry? received = null;
        sink.LogReceived += entry => received = entry;

        var logger = new Serilog.LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("Test message {Code}", 123);

        Assert.NotNull(received);
        Assert.Contains("Test message", received.Message);
        Assert.Equal("INF", received.LevelBadge);

        var entries = sink.GetRecentEntries();
        Assert.Single(entries);

        sink.Clear();
        Assert.Empty(sink.GetRecentEntries());
    }

    [Fact]
    public async Task BuildViewModel_HandleDropAsync_DroppedFolder_AddsKeyword()
    {
        var modFolder = Path.Combine(_testDir, "IronWarAxe");
        Directory.CreateDirectory(modFolder);

        var profileRepo = Substitute.For<IProfileRepository>();
        var profile = new ModProfile { Name = "AxeMod", Keywords = ["ExistingKeyword"] };
        profileRepo.LoadAllAsync().Returns([profile]);

        var settingsRepo = Substitute.For<ISettingsRepository>();
        settingsRepo.LoadAsync().Returns(new AppSettings { SkyrimDataPath = _testDir });

        var scanner = Substitute.For<IModScanner>();
        var pipeline = new PackingPipeline([], Microsoft.Extensions.Logging.Abstractions.NullLogger<PackingPipeline>.Instance);

        var vm = new BuildViewModel(profileRepo, settingsRepo, scanner, pipeline);
        await vm.LoadProfilesAsync();

        await vm.HandleDropAsync([modFolder]);

        Assert.Contains("IronWarAxe", profile.Keywords);
        await profileRepo.Received(1).SaveAsync(profile);
    }

    [Fact]
    public async Task BuildViewModel_HandleDropAsync_DroppedPlugin_SetsPluginFileName()
    {
        var pluginFile = Path.Combine(_testDir, "CustomWeapon.esp");
        await File.WriteAllTextAsync(pluginFile, "header");

        var profileRepo = Substitute.For<IProfileRepository>();
        var profile = new ModProfile { Name = "WeaponMod" };
        profileRepo.LoadAllAsync().Returns([profile]);

        var settingsRepo = Substitute.For<ISettingsRepository>();
        settingsRepo.LoadAsync().Returns(new AppSettings { SkyrimDataPath = _testDir });

        var scanner = Substitute.For<IModScanner>();
        var pipeline = new PackingPipeline([], Microsoft.Extensions.Logging.Abstractions.NullLogger<PackingPipeline>.Instance);

        var vm = new BuildViewModel(profileRepo, settingsRepo, scanner, pipeline);
        await vm.LoadProfilesAsync();

        await vm.HandleDropAsync([pluginFile]);

        Assert.Equal("CustomWeapon.esp", profile.PluginFileName);
        await profileRepo.Received(1).SaveAsync(profile);
    }
}
