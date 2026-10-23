using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.FileSystem;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class BuildSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BuildSelectionTests", Guid.NewGuid().ToString("N"));
    private readonly ModScanner _realScanner = new();
    private readonly IModScanner _scanner = Substitute.For<IModScanner>();
    private readonly ModProfile _profile = new() { Name = "Sword", Version = "1.0", PluginFileName = "Sword.esp", Keywords = ["Sword"] };
    private string Data => Path.Combine(_root, "Game", "Data");
    private string Output => Path.Combine(_root, "Archives");
    private AppSettings Settings(bool useTemp) => new() { SkyrimDataPath = Data, OutputDirectory = Output, UseTempDirectory = useTemp };

    public BuildSelectionTests()
    {
        WriteSource("Sword.esp");
        WriteSource("meshes/Sword/model.nif");
        WriteSource("textures/Sword/diffuse.dds");
        WriteSource("textures/Sword/normal.dds");
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<ModProfile>(), Arg.Any<CancellationToken>())
            .Returns(call => _realScanner.ScanAsync(call.ArgAt<string>(0), call.ArgAt<ModProfile>(1), call.ArgAt<CancellationToken>(2)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteSource(string relative)
    {
        var path = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Fixture: " + relative);
    }

    private PackingPipeline Pipeline()
    {
        var archiver = new ZipArchiver();
        return new PackingPipeline([
            new ScanStep(_scanner), new FilterStep(), new ValidateStep(archiver),
            new CopyStep(), new ArchiveStep(archiver)
        ], NullLogger<PackingPipeline>.Instance);
    }

    private BuildViewModel ViewModel(bool useTemp)
    {
        var profiles = Substitute.For<IProfileRepository>();
        profiles.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ModProfile> { _profile });
        var settings = Substitute.For<ISettingsRepository>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(Settings(useTemp));
        return new BuildViewModel(profiles, settings, _scanner, Pipeline());
    }

    private static IEnumerable<FileTreeNode> Leaves(IEnumerable<FileTreeNode> nodes) =>
        nodes.SelectMany(node => node.IsDirectory ? Leaves(node.Children) : [node]);

    private static string[] ArchiveEntries(BuildViewModel vm)
    {
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.HasSuccessNotification);
        Assert.False(vm.IsLoading);
        Assert.True(File.Exists(vm.LastArchiveCreatedPath));
        using var zip = ZipFile.OpenRead(vm.LastArchiveCreatedPath!);
        return zip.Entries.Select(entry => entry.FullName).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PackCommand_UsesTreeFileAndFolderSelection(bool useTemp, bool excludeFolder)
    {
        var vm = ViewModel(useTemp);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        if (excludeFolder)
            vm.FileTree.Single(node => node.Name == "textures").IsChecked = false;
        else
            Leaves(vm.FileTree).Single(node => node.Name == "normal.dds").IsChecked = false;

        await vm.PackCommand.ExecuteAsync(null);

        var expected = new List<string> { "Sword.esp", "meshes/Sword/model.nif" };
        if (!excludeFolder) expected.Add("textures/Sword/diffuse.dds");
        Assert.Equal(expected, ArchiveEntries(vm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackCommand_MissingSelectedFile_StopsWithoutCreatingArchive(bool useTemp)
    {
        var vm = ViewModel(useTemp);
        await vm.ScanCommand.ExecuteAsync(null);
        File.Delete(Path.Combine(Data, "meshes/Sword/model.nif"));

        await vm.PackCommand.ExecuteAsync(null);

        Assert.Contains("model.nif", vm.ErrorMessage);
        Assert.False(vm.HasSuccessNotification);
        Assert.Null(vm.LastArchiveCreatedPath);
        Assert.False(vm.IsLoading);
        Assert.False(Directory.Exists(Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackCommand_NewFileAfterPreview_IsNotAutomaticallyIncluded(bool useTemp)
    {
        var vm = ViewModel(useTemp);
        await vm.ScanCommand.ExecuteAsync(null);
        WriteSource("textures/Sword/new.dds");

        await vm.PackCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Sword.esp", "meshes/Sword/model.nif", "textures/Sword/diffuse.dds", "textures/Sword/normal.dds" }, ArchiveEntries(vm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackCommand_MissingExcludedFile_DoesNotBlockLegacyProfile(bool useTemp)
    {
        var vm = ViewModel(useTemp);
        await vm.ScanCommand.ExecuteAsync(null);
        Leaves(vm.FileTree).Single(node => node.Name == "normal.dds").IsChecked = false;
        File.Delete(Path.Combine(Data, "textures/Sword/normal.dds"));

        await vm.PackCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Sword.esp", "meshes/Sword/model.nif", "textures/Sword/diffuse.dds" }, ArchiveEntries(vm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackCommand_ChangesDuringRescan_DoNotChangeSelectionSnapshot(bool useTemp)
    {
        var vm = ViewModel(useTemp);
        await vm.ScanCommand.ExecuteAsync(null);
        Leaves(vm.FileTree).Single(node => node.Name == "normal.dds").IsChecked = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<ModProfile>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.SetResult();
            await resume.Task;
            return await _realScanner.ScanAsync(call.ArgAt<string>(0), call.ArgAt<ModProfile>(1), call.ArgAt<CancellationToken>(2));
        });

        var operation = vm.PackCommand.ExecuteAsync(null);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.SelectAllCommand.Execute(null);
            vm.ScannedFiles.Clear();
        }
        finally { resume.SetResult(); }
        await operation;

        Assert.Equal(new[] { "Sword.esp", "meshes/Sword/model.nif", "textures/Sword/diffuse.dds" }, ArchiveEntries(vm));
    }

    [Fact]
    public async Task PackCommand_AllFilesUnchecked_DoesNotCreateArchive()
    {
        var vm = ViewModel(useTemp: false);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.DeselectAllCommand.Execute(null);

        await vm.PackCommand.ExecuteAsync(null);

        Assert.Contains("selected", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasSuccessNotification);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task PackCommand_SourceChangedAfterPreview_DoesNotUseSameNamedReplacement()
    {
        var vm = ViewModel(useTemp: false);
        await vm.ScanCommand.ExecuteAsync(null);
        var replacement = Path.Combine(_root, "Replacement", "Sword.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);
        await File.WriteAllTextAsync(replacement, "A different plugin");
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<ModProfile>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var result = await _realScanner.ScanAsync(call.ArgAt<string>(0), call.ArgAt<ModProfile>(1), call.ArgAt<CancellationToken>(2));
            result.Value.Single(file => file.Category == FileCategory.Plugin).AbsolutePath = replacement;
            return result;
        });

        await vm.PackCommand.ExecuteAsync(null);

        Assert.Contains("Sword.esp", vm.ErrorMessage);
        Assert.False(vm.HasSuccessNotification);
        Assert.Null(vm.LastArchiveCreatedPath);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task PackCommand_FailedRetry_ClearsPreviousSuccessNotificationAndArchiveLink()
    {
        var vm = ViewModel(useTemp: false);
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.PackCommand.ExecuteAsync(null);
        Assert.True(vm.HasSuccessNotification);
        var archive = vm.LastArchiveCreatedPath!;
        var before = await File.ReadAllBytesAsync(archive);
        vm.DeselectAllCommand.Execute(null);

        await vm.PackCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.HasSuccessNotification);
        Assert.Null(vm.LastArchiveCreatedPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
    }

    [Fact]
    public async Task ExecuteAsync_EmptySelection_DoesNotFallBackToAllFiles()
    {
        var result = await Pipeline().ExecuteAsync(_profile, Settings(useTemp: false), fileSelection: []);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.SelectedFiles);
        Assert.False(Directory.Exists(Output));
    }
}
