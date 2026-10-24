using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeaponsViewModelTests", Guid.NewGuid().ToString("N"));
    private readonly ISettingsRepository _settings = Substitute.For<ISettingsRepository>();
    private readonly JsonProfileRepository _profiles;
    private readonly PluginBuilderService _builder = new();
    private string DataPath => Path.Combine(_root, "Game", "Data");

    public WeaponsViewModelTests()
    {
        Directory.CreateDirectory(DataPath);
        var master = new SkyrimMod(ModKey.FromNameAndExtension("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        master.Weapons.Add(new Weapon(new FormKey(master.ModKey, 0x12EB7), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IronSword", Name = "Iron Sword",
            BasicStats = new WeaponBasicStats { Damage = 7, Weight = 9, Value = 25 },
            Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword, Speed = 1, Reach = 1 },
            Model = new Model { File = @"Weapons\Iron\LongSword.nif" }
        });
        master.WriteToBinary(Path.Combine(DataPath, "Skyrim.esm"));
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings
        {
            SkyrimDataPath = DataPath, OutputDirectory = Path.Combine(_root, "archives")
        });
        _profiles = new JsonProfileRepository(Path.Combine(_root, "appdata"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WeaponsViewModel CreateViewModel() => new(_builder, _settings, _profiles, new WeaponProjectService(_builder))
    {
        PluginName = "Swords", OutputDirectory = Path.Combine(_root, "plugins")
    };

    private async Task<WeaponsViewModel> PrepareAsync()
    {
        var vm = CreateViewModel();
        await vm.InitializeCommand.ExecuteAsync(null);
        await vm.CreatePluginCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        await vm.LoadTemplatesCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    [Fact]
    public async Task Screen_InitializesVanillaTemplatesAutomatically_AndKeepsDraftOnReturn()
    {
        var original = await File.ReadAllBytesAsync(Path.Combine(DataPath, "Skyrim.esm"));
        var vm = CreateViewModel();
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsBaseConnected);
        Assert.Equal("IronSword", Assert.Single(vm.Templates).EditorId);
        Assert.NotNull(vm.SelectedTemplate);
        vm.WeaponName = "My unsaved sword";
        vm.Damage = "42";
        var templates = vm.Templates;
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.Same(templates, vm.Templates);
        Assert.Equal("My unsaved sword", vm.WeaponName);
        Assert.Equal("42", vm.Damage);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(DataPath, "Skyrim.esm")));
    }

    [Fact]
    public async Task Screen_MissingBaseCanBeConnectedFromEditor_PreservingOtherSettings()
    {
        var stored = new AppSettings { OutputDirectory = "KeepThisOutput", Language = "uk", Theme = "Light", UseTempDirectory = false };
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(stored);
        var vm = CreateViewModel();
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.False(vm.IsBaseConnected);
        Assert.Contains("Skyrim.esm", vm.ErrorMessage);
        Assert.Empty(vm.FilteredTemplates);
        await vm.ChooseGameDataCommand.ExecuteAsync(DataPath);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsBaseConnected);
        Assert.Single(vm.FilteredTemplates);
        await _settings.Received(1).SaveAsync(Arg.Is<AppSettings>(s =>
            s.SkyrimDataPath == DataPath && s.OutputDirectory == "KeepThisOutput" &&
            s.Language == "uk" && s.Theme == "Light" && !s.UseTempDirectory), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Screen_InvalidFolderDoesNotReplaceSavedGamePath()
    {
        var vm = CreateViewModel();
        await vm.InitializeCommand.ExecuteAsync(null);
        await vm.ChooseGameDataCommand.ExecuteAsync(_root);
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.IsBaseConnected);
        Assert.Equal(DataPath, vm.GameDataPath);
        await _settings.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Theory]
    [InlineData("iron sword", 1)]
    [InlineData("IRONSWORD", 1)]
    [InlineData("  iron  ", 1)]
    [InlineData("missing sword", 0)]
    public async Task Screen_SearchesTemplatesByNameOrEditorId(string query, int count)
    {
        var vm = CreateViewModel();
        await vm.InitializeCommand.ExecuteAsync(null);
        vm.TemplateSearch = query;
        Assert.Equal(count, vm.FilteredTemplates.Count);
        Assert.Single(vm.Templates);
        vm.TemplateSearch = string.Empty;
        Assert.Single(vm.FilteredTemplates);
    }

    [Fact]
    public async Task Screen_ChangedGamePathClearsPreviousBase_AndCanRetry()
    {
        var stored = new AppSettings { SkyrimDataPath = DataPath };
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(stored);
        var vm = CreateViewModel();
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.True(vm.IsBaseConnected);
        stored.SkyrimDataPath = Path.Combine(_root, "MissingGame", "Data");
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.False(vm.IsBaseConnected);
        Assert.Empty(vm.Templates);
        Assert.Empty(vm.FilteredTemplates);
        Assert.Null(vm.SelectedTemplate);
        await vm.ChooseGameDataCommand.ExecuteAsync(DataPath);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsBaseConnected);
    }

    [Theory]
    [InlineData(".esp")]
    [InlineData(".esl")]
    public async Task Screen_CreateCloneEditDeleteAndCloneAgain_PreservesIdsBackupsAndGame(string extension)
    {
        var originalMaster = await File.ReadAllBytesAsync(Path.Combine(DataPath, "Skyrim.esm"));
        var vm = CreateViewModel();
        vm.PluginExtension = extension;
        await vm.InitializeCommand.ExecuteAsync(null);
        await vm.CreatePluginCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.EndsWith(extension, vm.PluginPath);
        Assert.False(vm.ShowCreationPanel);
        await vm.LoadTemplatesCommand.ExecuteAsync(null);
        Assert.Equal("7", vm.Damage);
        vm.EditorId = "CustomSword";
        vm.WeaponName = "Крижаний меч";
        vm.Damage = "20";
        vm.Weight = "6.5";
        vm.Value = "400";
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(0x800u, Assert.Single(vm.Weapons).FormId);
        Assert.Equal("Крижаний меч", vm.SelectedWeapon!.Name);
        var beforeEdit = await File.ReadAllBytesAsync(vm.PluginPath);
        vm.WeaponName = "Оновлений меч";
        vm.Damage = "30";
        await vm.SaveSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(0x800u, Assert.Single(vm.Weapons).FormId);
        Assert.Equal((ushort)30, vm.SelectedWeapon!.Damage);
        Assert.Equal(beforeEdit, await File.ReadAllBytesAsync(vm.PluginPath + ".bak"));
        using (var saved = SkyrimMod.CreateFromBinaryOverlay(vm.PluginPath, SkyrimRelease.SkyrimSE))
        {
            var sword = Assert.Single(saved.Weapons);
            Assert.Equal(@"Weapons\Iron\LongSword.nif", sword.Model!.File.GivenPath);
            Assert.Equal(1f, sword.Data!.Speed);
        }
        var beforeDelete = await File.ReadAllBytesAsync(vm.PluginPath);
        await vm.DeleteSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.Weapons);
        Assert.Null(vm.SelectedWeapon);
        Assert.Equal(beforeDelete, await File.ReadAllBytesAsync(vm.PluginPath + ".bak"));
        vm.EditorId = "AnotherSword";
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(0x801u, Assert.Single(vm.Weapons).FormId);
        Assert.Equal(originalMaster, await File.ReadAllBytesAsync(Path.Combine(DataPath, "Skyrim.esm")));
        Assert.Single(Directory.GetFiles(DataPath));
    }

    [Theory]
    [InlineData("oops", "1", "1")]
    [InlineData("65536", "1", "1")]
    [InlineData("1", "NaN", "1")]
    [InlineData("1", "-2", "1")]
    [InlineData("1", "1", "-1")]
    public async Task Screen_InvalidStatistics_DoNotWritePlugin(string damage, string weight, string value)
    {
        var vm = await PrepareAsync();
        var original = await File.ReadAllBytesAsync(vm.PluginPath);
        vm.Damage = damage;
        vm.Weight = weight;
        vm.Value = value;
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Weapons);
        Assert.Equal(original, await File.ReadAllBytesAsync(vm.PluginPath));
        Assert.False(File.Exists(vm.PluginPath + ".bak"));
    }

    [Fact]
    public async Task Screen_ImportsModelAndTextures_SavesPath_ReopensAndBlocksMissingImportedModel()
    {
        var vm = await PrepareAsync();
        var sourceDirectory = Path.Combine(_root, "source-assets");
        Directory.CreateDirectory(sourceDirectory);
        var nif = Path.Combine(sourceDirectory, "blade.nif");
        var dds = Path.Combine(sourceDirectory, "blade.dds");
        await File.WriteAllTextAsync(nif, "model fixture");
        await File.WriteAllBytesAsync(dds, PreviewFixtures.ResourceDds("texture fixture"));
        await vm.ImportModelCommand.ExecuteAsync(nif);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(@"Swords\blade.nif", vm.ModelPath);
        Assert.Single(vm.ProjectResources);
        vm.TextureSubdirectory = "Custom";
        await vm.ImportTexturesCommand.ExecuteAsync(new[] { dds });
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("textures/Custom/blade.dds", vm.ProjectResources);
        await vm.CheckResourcesCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.ResourceStatus);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(@"Swords\blade.nif", Assert.Single(vm.Weapons).Details.ModelPath);
        var reopened = CreateViewModel();
        await reopened.OpenProjectCommand.ExecuteAsync(vm.ProjectFilePath);
        Assert.Null(reopened.ErrorMessage);
        Assert.Equal(2, reopened.ProjectResources.Count);
        Assert.Equal(vm.ModelPath, reopened.ModelPath);
        var pluginBefore = await File.ReadAllBytesAsync(vm.PluginPath);
        File.Delete(Path.Combine(Path.GetDirectoryName(vm.PluginPath)!, "meshes", vm.ModelPath));
        reopened.WeaponName = "Must not save";
        await reopened.SaveSwordCommand.ExecuteAsync(null);
        Assert.NotNull(reopened.ErrorMessage);
        Assert.Equal(pluginBefore, await File.ReadAllBytesAsync(vm.PluginPath));
        await reopened.CheckResourcesCommand.ExecuteAsync(null);
        Assert.NotNull(reopened.ErrorMessage);
        Assert.Null(reopened.ResourceStatus);
        Assert.Equal("model fixture", await File.ReadAllTextAsync(nif));
        Assert.Equal(PreviewFixtures.ResourceDds("texture fixture"), await File.ReadAllBytesAsync(dds));
    }

    [Fact]
    public async Task Screen_LoosePluginCannotImportWithoutProject()
    {
        var vm = await PrepareAsync();
        await vm.OpenPluginCommand.ExecuteAsync(vm.PluginPath);
        var source = Path.Combine(_root, "blade.nif");
        await File.WriteAllTextAsync(source, "fixture");
        await vm.ImportModelCommand.ExecuteAsync(source);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.ProjectResources);
        Assert.Empty(vm.ProjectFilePath);
    }

    [Fact]
    public async Task Screen_ReopensProjectAndImportsGamePlugin_WithoutChangingSource()
    {
        var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(File.Exists(vm.ProjectFilePath));
        var opened = CreateViewModel();
        await opened.OpenProjectCommand.ExecuteAsync(vm.ProjectFilePath);
        Assert.Null(opened.ErrorMessage);
        Assert.Equal(vm.PluginPath, opened.PluginPath);
        Assert.Equal(vm.ProjectFilePath, opened.ProjectFilePath);
        Assert.Single(opened.Weapons);
        var source = Path.Combine(DataPath, "Installed.esp");
        File.Copy(vm.PluginPath, source);
        var original = await File.ReadAllBytesAsync(source);
        await opened.OpenPluginCommand.ExecuteAsync(source);
        Assert.NotNull(opened.ErrorMessage);
        Assert.Equal(vm.PluginPath, opened.PluginPath);
        await opened.ImportProjectCommand.ExecuteAsync(source);
        Assert.Null(opened.ErrorMessage);
        Assert.Equal("Installed.esp", Path.GetFileName(opened.PluginPath));
        Assert.Single(opened.Weapons);
        Assert.True(File.Exists(opened.ProjectFilePath));
        opened.WeaponName = "Edited working copy";
        await opened.SaveSwordCommand.ExecuteAsync(null);
        Assert.Null(opened.ErrorMessage);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        await opened.OpenProjectCommand.ExecuteAsync(Path.Combine(_root, "missing-project.json"));
        Assert.NotNull(opened.ErrorMessage);
        Assert.Equal("Edited working copy", opened.SelectedWeapon!.Name);
        await opened.CreatePackingProfileCommand.ExecuteAsync(null);
        Assert.Null(opened.ErrorMessage);
        Assert.Equal(opened.PluginPath, Assert.Single(await _profiles.LoadAllAsync()).PluginSourcePath);
        await opened.OpenPluginCommand.ExecuteAsync(vm.PluginPath);
        Assert.Null(opened.ErrorMessage);
        Assert.Empty(opened.ProjectFilePath);
    }

    [Fact]
    public async Task Screen_OpensExistingPlugin_AndProtectsGameOnOpenFailure()
    {
        var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var opened = CreateViewModel();
        await opened.OpenPluginCommand.ExecuteAsync(vm.PluginPath);
        Assert.Null(opened.ErrorMessage);
        Assert.Equal(vm.PluginPath, opened.PluginPath);
        Assert.Single(opened.Weapons);
        await opened.OpenPluginCommand.ExecuteAsync(Path.Combine(DataPath, "Skyrim.esm"));
        Assert.NotNull(opened.ErrorMessage);
        Assert.Equal(vm.PluginPath, opened.PluginPath);
        Assert.Single(opened.Weapons);
    }

    [Fact]
    public async Task Screen_CreatesAndLinksPackingProfile_WhichBuildRefreshesAndPacks()
    {
        var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        await vm.CreatePackingProfileCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var created = Assert.Single(await _profiles.LoadAllAsync());
        Assert.Equal(vm.PluginPath, created.PluginSourcePath);
        created.EnableEsl = true;
        created.Keywords = ["MyMeshes"];
        created.Version = "3.2";
        await _profiles.SaveAsync(created);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddInfrastructureServices(Path.Combine(_root, "appdata"));
        using var provider = services.BuildServiceProvider();
        var build = new BuildViewModel(_profiles, _settings, provider.GetRequiredService<IModScanner>(), provider.GetRequiredService<PackingPipeline>());
        // The constructor starts an asynchronous load; wait for it before writing the shared JSON file.
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInitialized(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (build.Profiles.Count > 0) initialized.TrySetResult();
        }
        build.PropertyChanged += OnInitialized;
        if (build.Profiles.Count > 0) initialized.TrySetResult();
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        build.PropertyChanged -= OnInitialized;
        await build.LoadProfilesAsync();
        var previous = build.SelectedProfile;
        vm.SelectedPackingProfile = created;
        await vm.LinkPackingProfileCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var linked = Assert.Single(await _profiles.LoadAllAsync());
        Assert.True(linked.EnableEsl);
        Assert.Equal("3.2", linked.Version);
        Assert.Equal(new[] { "MyMeshes" }, linked.Keywords);
        await build.LoadProfilesAsync();
        Assert.NotSame(previous, build.SelectedProfile);
        Assert.Equal(vm.PluginPath, build.SelectedProfile!.PluginSourcePath);
        await build.ScanCommand.ExecuteAsync(null);
        await build.PackCommand.ExecuteAsync(null);
        Assert.Null(build.ErrorMessage);
        Assert.True(File.Exists(build.LastArchiveCreatedPath));
    }

    [Fact]
    public async Task Screen_CancelledTemplateLoad_RejectsConcurrentMutationsAndRecovers()
    {
        var builder = Substitute.For<IPluginBuilderService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.GetWeaponTemplatesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(1));
            return Result<IReadOnlyList<SwordTemplate>>.Success([]);
        });
        var vm = new WeaponsViewModel(builder, _settings, _profiles, new WeaponProjectService(builder));
        var loading = vm.LoadTemplatesCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsLoading);
        await vm.CreatePluginCommand.ExecuteAsync(null);
        await builder.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        vm.CancelOperationCommand.Execute(null);
        await loading;
        Assert.False(vm.IsLoading);
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.StatusMessage);
    }
}
