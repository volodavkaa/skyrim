using NSubstitute;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.Esl;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildViewModel_AnalyzeEsl_ShowsResultAndClearsItOnFailure(bool compatible)
    {
        var profiles = Substitute.For<IProfileRepository>();
        profiles.LoadAllAsync().Returns(new List<ModProfile>());
        var esl = Substitute.For<IEslService>();
        var analysis = new EslAnalysisResult
        {
            IsCompatible = compatible,
            OwnRecordCount = 2,
            ProblematicRecordCount = compatible ? 0 : 1,
            Reasons = compatible ? [] : ["Out of range"]
        };
        esl.AnalyzeAsync("Sword.esp", Arg.Any<CancellationToken>())
            .Returns(Result<EslAnalysisResult>.Success(analysis));
        esl.AnalyzeAsync("Broken.esp", Arg.Any<CancellationToken>())
            .Returns(Result<EslAnalysisResult>.Failure("Invalid plugin"));
        var vm = new BuildViewModel(profiles, Substitute.For<ISettingsRepository>(), Substitute.For<IModScanner>(),
            new PackingPipeline([], Microsoft.Extensions.Logging.Abstractions.NullLogger<PackingPipeline>.Instance), eslService: esl);

        await vm.AnalyzeEslCommand.ExecuteAsync("Sword.esp");
        Assert.Same(analysis, vm.EslAnalysis);
        Assert.Equal("Sword.esp", vm.AnalyzedPluginPath);
        Assert.Null(vm.EslAnalysisError);
        Assert.False(vm.IsLoading);

        await vm.AnalyzeEslCommand.ExecuteAsync("Broken.esp");
        Assert.Null(vm.EslAnalysis);
        Assert.Equal("Invalid plugin", vm.EslAnalysisError);
        Assert.False(vm.IsLoading);
    }

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
