using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    public static IEnumerable<object[]> RequiredProjectSelectionCases()
    {
        foreach (var useTemp in new[] { false, true })
        foreach (var esl in new[] { false, true })
        foreach (var excluded in new[] { "plugin", "model", "texture", "meshes", "textures", "all" })
            yield return [useTemp, esl, excluded];
    }

    private BuildViewModel ProjectBuildViewModel(ServiceProvider provider, WeaponProject project, bool useTemp, bool esl = false)
    {
        var profiles = Substitute.For<IProfileRepository>();
        profiles.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ModProfile> { PackingProfile(project, esl) });
        var settings = Substitute.For<ISettingsRepository>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings
        {
            SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives"),
            UseTempDirectory = useTemp, OverwriteExistingArchive = true
        });
        return new BuildViewModel(profiles, settings, provider.GetRequiredService<IModScanner>(), provider.GetRequiredService<PackingPipeline>());
    }

    private static IEnumerable<FileTreeNode> ProjectLeaves(IEnumerable<FileTreeNode> nodes) =>
        nodes.SelectMany(node => node.IsDirectory ? ProjectLeaves(node.Children) : [node]);

    [Theory]
    [MemberData(nameof(RequiredProjectSelectionCases))]
    public async Task BuildCommand_ExcludedRequiredProjectFiles_BlockPackingAndAllowRetry(bool useTemp, bool esl, string excluded)
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();
        var vm = ProjectBuildViewModel(provider, project, useTemp, esl);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var sources = vm.ScannedFiles.ToDictionary(file => file.AbsolutePath, file => File.ReadAllBytes(file.AbsolutePath));
        var master = await File.ReadAllBytesAsync(Path.Combine(Data, "Skyrim.esm"));
        var archives = Path.Combine(_root, "archives");
        Directory.CreateDirectory(archives);
        var archivePath = Path.Combine(archives, "MySword-1.0.zip");
        using (var previous = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(previous.CreateEntry("previous.txt").Open()))
            writer.Write("Previous successful build");
        var originalArchive = await File.ReadAllBytesAsync(archivePath);

        if (excluded == "all") vm.DeselectAllCommand.Execute(null);
        else if (excluded is "meshes" or "textures") vm.FileTree.Single(node => node.Name == excluded).IsChecked = false;
        else ProjectLeaves(vm.FileTree).Single(node => node.Category == (excluded switch
        {
            "plugin" => FileCategory.Plugin, "model" => FileCategory.Mesh, _ => FileCategory.Texture
        })).IsChecked = false;
        var excludedPaths = vm.ScannedFiles.Where(file => !file.IsSelected).Select(file => file.RelativePath.Replace('\\', '/')).ToList();
        Assert.NotEmpty(excludedPaths);

        await vm.PackCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        foreach (var path in excludedPaths) Assert.Contains(path, vm.ErrorMessage.Replace('\\', '/'));
        Assert.Contains("select", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasSuccessNotification);
        Assert.Null(vm.LastArchiveCreatedPath);
        Assert.False(vm.IsLoading);
        Assert.Equal(originalArchive, await File.ReadAllBytesAsync(archivePath));
        Assert.Single(Directory.GetFileSystemEntries(archives));
        Assert.Empty(await provider.GetRequiredService<IBuildHistoryRepository>().LoadAllAsync());
        foreach (var source in sources) Assert.Equal(source.Value, await File.ReadAllBytesAsync(source.Key));
        Assert.Equal(master, await File.ReadAllBytesAsync(Path.Combine(Data, "Skyrim.esm")));

        vm.SelectAllCommand.Execute(null);
        await vm.PackCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.HasSuccessNotification);
        Assert.Equal(archivePath, vm.LastArchiveCreatedPath);
        using var zip = ZipFile.OpenRead(archivePath);
        Assert.Equal(new[] { "MySword.esp", "meshes/Custom/Sword.nif", "textures/Custom/Sword.dds" },
            zip.Entries.Select(entry => entry.FullName).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Single(await provider.GetRequiredService<IBuildHistoryRepository>().LoadAllAsync());
        foreach (var source in sources) Assert.Equal(source.Value, await File.ReadAllBytesAsync(source.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildCommand_NewRegisteredResourceAfterPreview_RequiresRescan(bool useTemp)
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();
        var vm = ProjectBuildViewModel(provider, project, useTemp);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var texture = await AssetAsync(@"textures\Custom\New.dds", "new registered texture");
        var imported = await Service.ImportTexturesAsync(project.ProjectFilePath, [texture], Data);
        Assert.True(imported.IsSuccess, imported.Error);

        await vm.PackCommand.ExecuteAsync(null);

        Assert.Contains("textures/Custom/New.dds", vm.ErrorMessage?.Replace('\\', '/'));
        Assert.False(vm.HasSuccessNotification);
        Assert.False(Directory.Exists(Path.Combine(_root, "archives")));
        Assert.Empty(await provider.GetRequiredService<IBuildHistoryRepository>().LoadAllAsync());

        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.ScannedFiles.Count);
        await vm.PackCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.HasSuccessNotification);
        using var zip = ZipFile.OpenRead(vm.LastArchiveCreatedPath!);
        Assert.Equal(4, zip.Entries.Count);
        Assert.NotNull(zip.GetEntry("textures/Custom/New.dds"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildCommand_VanillaModel_DoesNotRequireCopyingGameResources(bool esl)
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var clone = await _builder.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = project.PluginPath, GameDataPath = Data, TemplateFormId = 0x12EB7,
            EditorId = "InheritedSword", Name = "Inherited sword"
        });
        Assert.True(clone.IsSuccess, clone.Error);
        using var provider = PackingProvider();
        var vm = ProjectBuildViewModel(provider, project, useTemp: false, esl: esl);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Single(vm.ScannedFiles);

        await vm.PackCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.HasSuccessNotification);
        using var zip = ZipFile.OpenRead(vm.LastArchiveCreatedPath!);
        Assert.Equal("MySword.esp", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectWithEmptySelection_ReportsEveryRequiredFile()
    {
        var project = await PreparePackingProjectAsync();
        using var provider = PackingProvider();

        var result = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(PackingProfile(project), new AppSettings
        {
            SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives")
        }, fileSelection: []);

        Assert.False(result.IsSuccess);
        var errors = string.Join("\n", result.Errors).Replace('\\', '/');
        Assert.Contains("MySword.esp", errors);
        Assert.Contains("meshes/Custom/Sword.nif", errors);
        Assert.Contains("textures/Custom/Sword.dds", errors);
        Assert.False(Directory.Exists(Path.Combine(_root, "archives")));
    }
}
