using NSubstitute;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ViewModelTests
{
    [Fact]
    public async Task ProfilesViewModel_AddAndRemoveKeyword_UpdatesCollection()
    {
        var repo = Substitute.For<IProfileRepository>();
        repo.LoadAllAsync().Returns(new List<ModProfile>());

        var vm = new ProfilesViewModel(repo);
        await vm.LoadProfilesAsync();

        vm.NewKeyword = "MyAxe";
        vm.AddKeywordCommand.Execute(null);

        Assert.Contains("MyAxe", vm.Keywords);
        Assert.Empty(vm.NewKeyword);

        vm.RemoveKeywordCommand.Execute("MyAxe");
        Assert.DoesNotContain("MyAxe", vm.Keywords);
    }

    [Fact]
    public async Task ProfilesViewModel_SaveValidProfile_CallsRepository()
    {
        var repo = Substitute.For<IProfileRepository>();
        var existing = new ModProfile { Name = "OldName", Version = "1.0", Keywords = ["OldKeyword"] };
        repo.LoadAllAsync().Returns([existing]);

        var vm = new ProfilesViewModel(repo);
        await vm.LoadProfilesAsync();

        vm.Name = "NewAxe";
        vm.Version = "2.0";
        vm.PluginFileName = "NewAxe.esp";
        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        await repo.Received(1).SaveAsync(Arg.Is<ModProfile>(p => p.Name == "NewAxe" && p.Version == "2.0"));
    }

    [Fact]
    public async Task ProfilesViewModel_SaveInvalidProfile_SetsErrorMessageAndDoesNotCallRepo()
    {
        var repo = Substitute.For<IProfileRepository>();
        var profile = new ModProfile { Name = "Valid", Version = "1.0", Keywords = ["K"] };
        repo.LoadAllAsync().Returns([profile]);

        var vm = new ProfilesViewModel(repo);
        await vm.LoadProfilesAsync();

        vm.Name = ""; // Invalid
        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        await repo.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task ProfilesViewModel_DeleteProfile_CallsRepositoryAndRemoves()
    {
        var repo = Substitute.For<IProfileRepository>();
        var profile = new ModProfile { Name = "ToDelete", Version = "1.0", Keywords = ["K"] };
        repo.LoadAllAsync().Returns([profile]);

        var vm = new ProfilesViewModel(repo);
        await vm.LoadProfilesAsync();

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        await repo.Received(1).DeleteAsync(profile.Id);
        Assert.DoesNotContain(profile, vm.Profiles);
    }

    [Fact]
    public async Task SettingsViewModel_AutoDetectSkyrim_CallsLocator()
    {
        var settingsRepo = Substitute.For<ISettingsRepository>();
        settingsRepo.LoadAsync().Returns(new AppSettings());

        var locator = Substitute.For<ISkyrimLocator>();
        locator.LocateSkyrimDataPath().Returns(Result<string>.Success(@"C:\Games\Skyrim\Data"));

        var vm = new SettingsViewModel(settingsRepo, locator);
        await vm.LoadSettingsAsync();

        await vm.AutoDetectSkyrimCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Games\Skyrim\Data", vm.SkyrimDataPath);
        Assert.Contains("detected", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsViewModel_ResetOutputDirectory_SetsToDesktop()
    {
        var settingsRepo = Substitute.For<ISettingsRepository>();
        settingsRepo.LoadAsync().Returns(new AppSettings { OutputDirectory = @"C:\CustomOutput" });

        var locator = Substitute.For<ISkyrimLocator>();
        var vm = new SettingsViewModel(settingsRepo, locator);
        await vm.LoadSettingsAsync();

        vm.ResetOutputDirectoryCommand.Execute(null);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        Assert.Equal(desktop, vm.OutputDirectory);
    }

    [Fact]
    public async Task BuildViewModel_ScanWithoutProfile_SetsErrorMessage()
    {
        var profileRepo = Substitute.For<IProfileRepository>();
        profileRepo.LoadAllAsync().Returns(new List<ModProfile>());

        var settingsRepo = Substitute.For<ISettingsRepository>();
        var scanner = Substitute.For<IModScanner>();
        var pipeline = new PackingPipeline([], Microsoft.Extensions.Logging.Abstractions.NullLogger<PackingPipeline>.Instance);

        var vm = new BuildViewModel(profileRepo, settingsRepo, scanner, pipeline);
        await vm.LoadProfilesAsync();

        vm.SelectedProfile = null;
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("profile", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildViewModel_PackWithoutSelectedFiles_SetsErrorMessage()
    {
        var profileRepo = Substitute.For<IProfileRepository>();
        var profile = new ModProfile { Name = "MyMod", Version = "1.0", Keywords = ["MyMod"] };
        profileRepo.LoadAllAsync().Returns([profile]);

        var settingsRepo = Substitute.For<ISettingsRepository>();
        var scanner = Substitute.For<IModScanner>();
        var pipeline = new PackingPipeline([], Microsoft.Extensions.Logging.Abstractions.NullLogger<PackingPipeline>.Instance);

        var vm = new BuildViewModel(profileRepo, settingsRepo, scanner, pipeline);
        await vm.LoadProfilesAsync();

        // No files in tree
        await vm.PackCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("selected", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
