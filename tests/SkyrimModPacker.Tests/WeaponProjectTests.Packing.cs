using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    private ServiceProvider PackingProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddApplicationServices();
        services.AddInfrastructureServices(Path.Combine(_root, "appdata"));
        return services.BuildServiceProvider();
    }

    private async Task<WeaponProject> PreparePackingProjectAsync()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var nif = await AssetAsync(@"meshes\Custom\Sword.nif", "custom model");
        var dds = await AssetAsync(@"textures\Custom\Sword.dds", "custom texture");
        var imported = await Service.ImportModelAsync(project.ProjectFilePath, nif, Data);
        Assert.True(imported.IsSuccess, imported.Error);
        Assert.True((await Service.ImportTexturesAsync(project.ProjectFilePath, [dds], Data)).IsSuccess);
        var sword = await _builder.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = project.PluginPath, GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "CustomSword", Name = "Custom sword",
            Details = new WeaponDetails { ModelPath = imported.Value }
        });
        Assert.True(sword.IsSuccess, sword.Error);
        return project;
    }

    private ModProfile PackingProfile(WeaponProject project, bool esl = false) => new()
    {
        Name = "MySword", Version = "1.0", PluginFileName = Path.GetFileName(project.PluginPath), PluginSourcePath = project.PluginPath,
        WeaponProjectPath = project.ProjectFilePath, Keywords = ["Custom"], EnableEsl = esl
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Project_ArchivesOnlyRegisteredResources_WithOptionalEsl_AndUnchangedSources(bool useTemp, bool esl)
    {
        var project = await PreparePackingProjectAsync();
        var originalPlugin = await File.ReadAllBytesAsync(project.PluginPath);
        var master = await File.ReadAllBytesAsync(Path.Combine(Data, "Skyrim.esm"));
        var gameModel = Path.Combine(Data, "meshes", "Custom", "Sword.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(gameModel)!);
        await File.WriteAllTextAsync(gameModel, "different game model");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(project.PluginPath)!, "meshes", "Unregistered.nif"), "ignore this");
        using var provider = PackingProvider();
        var profile = PackingProfile(project, esl);
        var scan = await provider.GetRequiredService<IModScanner>().ScanAsync(Data, profile);
        Assert.True(scan.IsSuccess, scan.Error);
        Assert.Equal(3, scan.Value.Count);
        Assert.DoesNotContain(scan.Value, f => f.AbsolutePath == gameModel);
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile, new AppSettings
        {
            SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives"), UseTempDirectory = useTemp
        });
        Assert.True(packed.IsSuccess, string.Join("; ", packed.Errors));
        using (var zip = ZipFile.OpenRead(packed.OutputArchivePath))
        {
            Assert.Equal(new[] { "MySword.esp", "meshes/Custom/Sword.nif", "textures/Custom/Sword.dds" }, zip.Entries.Select(e => e.FullName).OrderBy(p => p, StringComparer.Ordinal));
            using var reader = new StreamReader(zip.GetEntry("meshes/Custom/Sword.nif")!.Open());
            Assert.Equal("custom model", await reader.ReadToEndAsync());
            var unpack = Path.Combine(_root, "unpacked");
            Directory.CreateDirectory(unpack);
            var plugin = Path.Combine(unpack, "MySword.esp");
            zip.GetEntry("MySword.esp")!.ExtractToFile(plugin);
            using var mod = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE);
            Assert.Equal(esl, mod.IsSmallMaster);
            Assert.Equal(0x800u, Assert.Single(mod.Weapons).FormKey.ID);
        }
        Assert.Equal(originalPlugin, await File.ReadAllBytesAsync(project.PluginPath));
        Assert.Equal(master, await File.ReadAllBytesAsync(Path.Combine(Data, "Skyrim.esm")));
        Assert.Equal("different game model", await File.ReadAllTextAsync(gameModel));
        Assert.Equal(esl, Directory.Exists(packed.OutputArchivePath + ".backups"));
        if (esl) Assert.Single(Directory.GetFiles(packed.OutputArchivePath + ".backups", "*.bak", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("texture")]
    [InlineData("manifest")]
    [InlineData("plugin")]
    public async Task MissingProjectFiles_AfterPreview_StopPackingWithoutFallbackOrArchive(string missing)
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();
        var profile = PackingProfile(project);
        Assert.True((await provider.GetRequiredService<IModScanner>().ScanAsync(Data, profile)).IsSuccess);
        var folder = Path.GetDirectoryName(project.PluginPath)!;
        var target = missing switch
        {
            "model" => Path.Combine(folder, "meshes", "Custom", "Sword.nif"),
            "texture" => Path.Combine(folder, "textures", "Custom", "Sword.dds"),
            "manifest" => project.ProjectFilePath,
            _ => project.PluginPath
        };
        File.Delete(target);
        // A same-named Data plugin must never rescue a broken project configuration.
        await File.WriteAllTextAsync(Path.Combine(Data, "MySword.esp"), "wrong plugin");
        var archives = Path.Combine(_root, "archives");
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile, new AppSettings { SkyrimDataPath = Data, OutputDirectory = archives });
        Assert.False(packed.IsSuccess);
        Assert.NotEmpty(packed.Errors);
        Assert.False(Directory.Exists(archives));
    }

    [Fact]
    public async Task GameOutputAndMismatchedPlugin_AreRejected()
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();
        var profile = PackingProfile(project);
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile, new AppSettings { SkyrimDataPath = Data, OutputDirectory = Data });
        Assert.False(packed.IsSuccess);
        Assert.Empty(Directory.GetFiles(Data, "*.zip"));
        profile.PluginSourcePath = Path.Combine(_root, "other", "MySword.esp");
        var scan = await provider.GetRequiredService<IModScanner>().ScanAsync(Data, profile);
        Assert.True(scan.IsFailure);
        Assert.Contains("does not match", scan.Error);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("texture")]
    [InlineData("manifest")]
    [InlineData("plugin")]
    public async Task BuildCommand_MissingProjectFileAfterPreview_StillValidatesProject(string missing)
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();
        var profile = PackingProfile(project);
        var archives = Path.Combine(_root, "archives");
        var profiles = Substitute.For<IProfileRepository>();
        profiles.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ModProfile> { profile });
        var settings = Substitute.For<ISettingsRepository>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings { SkyrimDataPath = Data, OutputDirectory = archives });
        var vm = new BuildViewModel(profiles, settings, provider.GetRequiredService<IModScanner>(), provider.GetRequiredService<PackingPipeline>());
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(3, vm.ScannedFiles.Count);
        var folder = Path.GetDirectoryName(project.PluginPath)!;
        File.Delete(missing switch
        {
            "model" => Path.Combine(folder, "meshes", "Custom", "Sword.nif"),
            "texture" => Path.Combine(folder, "textures", "Custom", "Sword.dds"),
            "manifest" => project.ProjectFilePath,
            _ => project.PluginPath
        });
        await File.WriteAllTextAsync(Path.Combine(Data, "MySword.esp"), "wrong plugin");

        await vm.PackCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.HasSuccessNotification);
        Assert.Null(vm.LastArchiveCreatedPath);
        Assert.False(vm.IsLoading);
        Assert.False(Directory.Exists(archives));
    }

    [Fact]
    public async Task UnregisteredCustomModel_IsRejected_AndVanillaModelDoesNotNeedCopying()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var clone = await _builder.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = project.PluginPath, GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "Sword", Name = "Sword"
        });
        Assert.True(clone.IsSuccess, clone.Error);
        var vanilla = await Service.GetPackingFilesAsync(project.ProjectFilePath, Data);
        Assert.True(vanilla.IsSuccess, vanilla.Error);
        Assert.Single(vanilla.Value);
        var update = await _builder.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = project.PluginPath, GameDataPath = Data, FormId = clone.Value.FormId, EditorId = "Sword", Name = "Sword", Damage = 7, Weight = 9, Value = 25,
            Details = new WeaponDetails { ModelPath = @"Custom\missing.nif" }
        });
        Assert.True(update.IsSuccess, update.Error);
        Assert.True((await Service.GetPackingFilesAsync(project.ProjectFilePath, Data)).IsFailure);
    }

    [Fact]
    public async Task LegacyJsonProfile_StillScansDataAndPacks_OpaquePluginAndAssets()
    {
        var appdata = Path.Combine(_root, "oldprofiles");
        Directory.CreateDirectory(appdata);
        await File.WriteAllTextAsync(Path.Combine(appdata, "profiles.json"), """
            [{"name":"Legacy","version":"1.0","pluginFileName":"Legacy.esp","keywords":["Legacy"]}]
            """);
        var profile = Assert.Single(await new JsonProfileRepository(appdata).LoadAllAsync());
        Assert.Empty(profile.WeaponProjectPath);
        await File.WriteAllTextAsync(Path.Combine(Data, "Legacy.esp"), "legacy plugin fixture");
        Directory.CreateDirectory(Path.Combine(Data, "meshes"));
        await File.WriteAllTextAsync(Path.Combine(Data, "meshes", "Legacy.nif"), "legacy model");
        using var provider = PackingProvider();
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile, new AppSettings
        {
            SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives")
        });
        Assert.True(packed.IsSuccess, string.Join("; ", packed.Errors));
        using var zip = ZipFile.OpenRead(packed.OutputArchivePath);
        Assert.Equal(2, zip.Entries.Count);
        Assert.NotNull(zip.GetEntry("Legacy.esp"));
        Assert.NotNull(zip.GetEntry("meshes/Legacy.nif"));
    }

    [Fact]
    public async Task Wizard_PacksRegisteredResourcesDirectly_AndPreservesProjectAssociationOnProfileEditAndCopy()
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();
        var settings = provider.GetRequiredService<ISettingsRepository>();
        await settings.SaveAsync(new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives") });
        var profiles = provider.GetRequiredService<IProfileRepository>();
        var vm = ActivatorUtilities.CreateInstance<WeaponsViewModel>(provider);
        await vm.OpenProjectCommand.ExecuteAsync(project.ProjectFilePath);
        Assert.Null(vm.ErrorMessage);
        vm.CurrentStep = 4;
        await vm.SaveWizardSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        vm.EnableProjectEsl = true;
        await vm.PackProjectCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(File.Exists(vm.LastProjectArchivePath));
        using (var zip = ZipFile.OpenRead(vm.LastProjectArchivePath!)) Assert.Equal(3, zip.Entries.Count);
        var saved = Assert.Single(await profiles.LoadAllAsync());
        Assert.Equal(project.ProjectFilePath, saved.WeaponProjectPath);
        Assert.True(saved.EnableEsl);
        var build = new BuildViewModel(profiles, settings, provider.GetRequiredService<IModScanner>(), provider.GetRequiredService<PackingPipeline>());
        await build.LoadProfilesAsync();
        await build.ScanCommand.ExecuteAsync(null);
        Assert.Null(build.ErrorMessage);
        Assert.Equal(3, build.ScannedFiles.Count);
        var mockRepository = Substitute.For<IProfileRepository>();
        mockRepository.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ModProfile> { saved });
        var editor = new ProfilesViewModel(mockRepository);
        editor.Version = "2.0";
        await editor.SaveProfileCommand.ExecuteAsync(null);
        await mockRepository.Received().SaveAsync(Arg.Is<ModProfile>(p => p.WeaponProjectPath == project.ProjectFilePath && p.Version == "2.0"), Arg.Any<CancellationToken>());
        await editor.DuplicateProfileCommand.ExecuteAsync(null);
        Assert.Equal(project.ProjectFilePath, editor.SelectedProfile!.WeaponProjectPath);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("sound")]
    [InlineData("keywords")]
    public async Task UnsavedAdvancedChanges_BlockWizardPacking(string field)
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();
        var settings = provider.GetRequiredService<ISettingsRepository>();
        var archives = Path.Combine(_root, "archives");
        await settings.SaveAsync(new AppSettings { SkyrimDataPath = Data, OutputDirectory = archives });
        var vm = new WeaponsViewModel(_builder, settings, provider.GetRequiredService<IProfileRepository>(), Service,
            packingPipeline: provider.GetRequiredService<PackingPipeline>());
        await vm.OpenProjectCommand.ExecuteAsync(project.ProjectFilePath);
        vm.CurrentStep = 4;
        await vm.SaveWizardSwordCommand.ExecuteAsync(null);
        Assert.True(vm.IsWizardSaved);
        switch (field)
        {
            case "description": vm.Description = "Unsaved description"; break;
            case "sound": vm.SelectedSound = new RecordOption { Reference = new PluginRecordReference("Skyrim.esm", 0x100), EditorId = "Sound" }; break;
            case "keywords": vm.WeaponKeywords.Add(new RecordOption { Reference = new PluginRecordReference("Skyrim.esm", 0x101), EditorId = "Keyword" }); break;
        }
        Assert.False(vm.IsWizardSaved);
        await vm.PackProjectCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.LastProjectArchivePath);
        Assert.False(Directory.Exists(archives));
    }
}
