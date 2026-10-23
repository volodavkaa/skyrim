using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.FileSystem;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class PackingPathSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PackingPathSafetyTests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private readonly List<string> _stages = [];
    private string Data => Path.Combine(_root, "Game", "Data");
    private string Source => Path.Combine(Data, "meshes", "Sword", "model.nif");
    private string Output => Path.Combine(_root, "Output", "Sword-1.0.zip");
    private ModProfile Profile => new() { Name = "Sword", Version = "1.0", PluginFileName = "Sword.esp", Keywords = ["Sword"] };

    public PackingPathSafetyTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "model");
        File.WriteAllText(Path.Combine(Data, "Sword.esp"), "plugin");
    }

    public void Dispose()
    {
        var allowed = Path.Combine(Path.GetTempPath(), "PackingPathSafetyTests") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup must remain inside its temporary workspace.");
        foreach (var junction in _junctions)
            if (new DirectoryInfo(junction).LinkTarget != null) Directory.Delete(junction);
        foreach (var stage in _stages)
        {
            PackingPathGuard.EnsureStagingDirectory(stage, Data);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
        Directory.Delete(_root, recursive: true);
    }

    public static IEnumerable<object[]> UnsafeEntries() => new[]
    {
        "../escape.nif", @"..\escape.nif", "meshes/../../escape.nif", "/absolute.nif", @"\absolute.nif",
        @"C:\absolute.nif", "C:relative.nif", @"\\server\share\file.nif", @"\\?\C:\file.nif",
        "meshes//file.nif", "meshes/./file.nif", "meshes/file.nif:stream", "meshes/folder./file.nif",
        "meshes/folder /file.nif", "meshes/NUL.nif", "meshes/COM1.nif", "meshes/LPT¹.dds", "meshes/file?.nif", ""
    }.Select(path => new object[] { path });

    public static IEnumerable<object[]> UnsafeCopies()
    {
        foreach (var row in UnsafeEntries())
        foreach (var useTemp in new[] { false, true })
            yield return [row[0], useTemp];
    }

    private PackingContext Context(bool useTemp, string relative = "meshes/Sword/model.nif", string? source = null) => new()
    {
        Profile = Profile,
        Settings = new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.GetDirectoryName(Output)!, UseTempDirectory = useTemp },
        SelectedFiles = [new ScannedFile { AbsolutePath = source ?? Source, RelativePath = relative, Category = FileCategory.Mesh }]
    };

    private void CreateOldArchive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        File.WriteAllText(Output, "previous archive bytes");
    }

    private async Task CreateJunctionAsync(string path, string target)
    {
        Assert.Contains(_stages.Append(_root), root => Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:SKYRIM_TEST_LINK -Value $env:SKYRIM_TEST_TARGET -ErrorAction Stop | Out-Null");
        start.Environment["SKYRIM_TEST_LINK"] = path;
        start.Environment["SKYRIM_TEST_TARGET"] = target;
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await error);
        _junctions.Add(path);
        Assert.NotNull(new DirectoryInfo(path).LinkTarget);
    }

    [Theory]
    [MemberData(nameof(UnsafeEntries))]
    public async Task Zip_UnsafeEntry_IsRejectedBeforeReplacingArchive(string entry)
    {
        CreateOldArchive();
        var before = await File.ReadAllBytesAsync(Output);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, entry)]));

        Assert.Equal(before, await File.ReadAllBytesAsync(Output));
        Assert.Equal("model", await File.ReadAllTextAsync(Source));
    }

    [Theory]
    [MemberData(nameof(UnsafeCopies))]
    public async Task Copy_UnsafeEntry_IsRejectedInEveryMode(string entry, bool useTemp)
    {
        var context = Context(useTemp, entry);

        Assert.False(await new CopyStep().ExecuteAsync(context));

        Assert.NotEmpty(context.Errors);
        Assert.Null(context.StagingDirectory);
        Assert.Equal("model", await File.ReadAllTextAsync(Source));
        Assert.False(File.Exists(Path.Combine(_root, "escape.nif")));
    }

    [Theory]
    [InlineData("meshes/a.nif", @"MESHES\A.NIF")]
    [InlineData("meshes", "meshes/file.nif")]
    [InlineData("meshes/file.nif", "MESHES")]
    public async Task Zip_ConflictingPaths_DoNotReplaceArchive(string first, string second)
    {
        CreateOldArchive();
        var before = await File.ReadAllBytesAsync(Output);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, first), (Source, second)]));

        Assert.Equal(before, await File.ReadAllBytesAsync(Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_ConflictingPaths_DoNotCreateStaging(bool useTemp)
    {
        var context = Context(useTemp);
        context.SelectedFiles.Add(new ScannedFile { AbsolutePath = Source, RelativePath = @"MESHES\SWORD\MODEL.NIF" });

        Assert.False(await new CopyStep().ExecuteAsync(context));

        Assert.Contains("Conflicting", Assert.Single(context.Errors));
        Assert.Null(context.StagingDirectory);
    }

    [Theory]
    [InlineData("../Outside.esp")]
    [InlineData(@"..\Outside.esp")]
    [InlineData(@"C:\Outside.esp")]
    [InlineData("folder/Sword.esp")]
    [InlineData("Sword.esp:stream")]
    [InlineData("Sword.esp.")]
    [InlineData("NUL.esp")]
    public async Task Scanner_UnsafePluginFilename_ReturnsFailure(string name)
    {
        var profile = Profile;
        profile.PluginFileName = name;

        Assert.True(ModProfileValidator.Validate(profile).IsFailure);
        var result = await new ModScanner().ScanAsync(Data, profile);

        Assert.True(result.IsFailure);
        Assert.Equal("plugin", await File.ReadAllTextAsync(Path.Combine(Data, "Sword.esp")));
    }

    [Theory]
    [InlineData(false, "data")]
    [InlineData(false, "root")]
    [InlineData(false, "new")]
    [InlineData(true, "data")]
    [InlineData(true, "root")]
    [InlineData(true, "new")]
    public async Task LegacyPipeline_OutputInsideGame_IsBlockedWithoutEsl(bool useTemp, string destination)
    {
        var output = destination switch { "data" => Data, "root" => Path.GetDirectoryName(Data)!, _ => Path.Combine(Data, "NewOutput") };
        var archiver = new ZipArchiver();
        var pipeline = new PackingPipeline([
            new ScanStep(new ModScanner()), new FilterStep(), new ValidateStep(archiver), new CopyStep(), new ArchiveStep(archiver)
        ], NullLogger<PackingPipeline>.Instance);

        var result = await pipeline.ExecuteAsync(Profile, new AppSettings { SkyrimDataPath = Data, OutputDirectory = output, UseTempDirectory = useTemp });

        Assert.False(result.IsSuccess);
        Assert.Contains("game directory", string.Join("; ", result.Errors));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Data)!, "*.zip", SearchOption.AllDirectories));
        if (destination == "new") Assert.False(Directory.Exists(output));
        Assert.Equal("model", await File.ReadAllTextAsync(Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_SourceOutsideAllowedRoot_IsRejected(bool useTemp)
    {
        var external = Path.Combine(_root, "outside.nif");
        await File.WriteAllTextAsync(external, "external");
        var context = Context(useTemp, source: external);

        Assert.False(await new CopyStep().ExecuteAsync(context));
        Assert.False(await new ValidateStep(new ZipArchiver()).ExecuteAsync(context));

        Assert.Null(context.StagingDirectory);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Output)));
        Assert.Equal("external", await File.ReadAllTextAsync(external));
    }

    [Fact]
    public async Task Zip_OutputEqualToSource_IsRejectedWithoutDeletingSource()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync(Source, [(Source, "file.nif")]));

        Assert.Equal("model", await File.ReadAllTextAsync(Source));
    }

    [Fact]
    public async Task Zip_RelativeOutput_IsRejectedWithoutWriting()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync("unexpected-relative.zip", [(Source, "file.nif")]));
    }

    [Fact]
    public async Task JunctionInSource_BlocksScannerCopyAndZip()
    {
        var external = Path.Combine(_root, "External");
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "model.nif"), "external");
        var junction = Path.Combine(Data, "meshes", "Sword", "Linked");
        await CreateJunctionAsync(junction, external);
        var linkedSource = Path.Combine(junction, "model.nif");

        var scanned = await new ModScanner().ScanAsync(Data, Profile);
        Assert.True(scanned.IsFailure);
        Assert.Contains("junction", scanned.Error);
        foreach (var useTemp in new[] { false, true })
        {
            var context = Context(useTemp, "meshes/Sword/Linked/model.nif", linkedSource);
            Assert.False(await new CopyStep().ExecuteAsync(context));
            Assert.Null(context.StagingDirectory);
        }
        CreateOldArchive();
        var before = await File.ReadAllBytesAsync(Output);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(linkedSource, "file.nif")]));
        Assert.Equal(before, await File.ReadAllBytesAsync(Output));
        Assert.Equal("external", await File.ReadAllTextAsync(Path.Combine(external, "model.nif")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionInOutput_IncludingDanglingLink_IsRejected(bool dangling)
    {
        var target = Path.Combine(_root, "Game", "Redirected");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_root, "OutputLink");
        await CreateJunctionAsync(junction, target);
        if (dangling) Directory.Delete(target);
        var context = Context(useTemp: false);
        context.Settings.OutputDirectory = junction;

        Assert.False(await new ValidateStep(new ZipArchiver()).ExecuteAsync(context));
        Assert.Contains("junction", Assert.Single(context.Errors));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync(Path.Combine(junction, "test.zip"), [(Source, "file.nif")]));
        Assert.False(File.Exists(Path.Combine(target, "test.zip")));
        if (dangling) Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task GameDataJunction_CannotHideTheRealGameDirectoryFromWriteProtection()
    {
        var alias = Path.Combine(_root, "Alias", "Data");
        await CreateJunctionAsync(alias, Data);

        var error = Assert.Throws<InvalidOperationException>(() => PluginWriteGuard.EnsureOutsideGame(Path.Combine(Data, "Sword.esp"), alias));

        Assert.Contains("junction", error.Message);
        Assert.Equal("plugin", await File.ReadAllTextAsync(Path.Combine(Data, "Sword.esp")));
    }

    [Fact]
    public async Task ArchiveStep_ForeignStaging_IsRejectedWithoutDeletingItsFiles()
    {
        var foreign = Path.Combine(_root, "ForeignStaging");
        Directory.CreateDirectory(foreign);
        var sentinel = Path.Combine(foreign, "keep.nif");
        await File.WriteAllTextAsync(sentinel, "keep this file");
        var context = Context(useTemp: false);
        context.StagingDirectory = foreign;
        context.OutputArchivePath = Output;

        Assert.False(await new ArchiveStep(new ZipArchiver()).ExecuteAsync(context));

        Assert.Equal("keep this file", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Output));
        Assert.NotEmpty(context.Errors);
    }

    [Fact]
    public async Task ArchiveStep_JunctionInsideStaging_IsRejectedWithoutReadingOrDeletingTarget()
    {
        var external = Path.Combine(_root, "ExternalStagingTarget");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.nif");
        await File.WriteAllTextAsync(sentinel, "keep this file");
        var context = Context(useTemp: true);
        Assert.True(await new CopyStep().ExecuteAsync(context));
        var stage = context.StagingDirectory!;
        _stages.Add(stage);
        await CreateJunctionAsync(Path.Combine(stage, "Linked"), external);
        CreateOldArchive();
        var before = await File.ReadAllBytesAsync(Output);
        context.OutputArchivePath = Output;
        var logger = Substitute.For<ILogger<ArchiveStep>>();

        Assert.False(await new ArchiveStep(new ZipArchiver(), logger: logger).ExecuteAsync(context));

        Assert.Contains("junction", Assert.Single(context.Errors));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(before, await File.ReadAllBytesAsync(Output));
        Assert.False(Directory.Exists(stage), string.Join("\n", logger.ReceivedCalls().Select(call => call.GetArguments().ElementAtOrDefault(3)?.ToString())));
    }

    [Fact]
    public async Task ArchiveStep_DisappearedStaging_DoesNotFallBackToOriginalSources()
    {
        var context = Context(useTemp: true);
        Assert.True(await new CopyStep().ExecuteAsync(context));
        var stage = context.StagingDirectory!;
        PackingPathGuard.EnsureStagingDirectory(stage, Data);
        Directory.Delete(stage, recursive: true);
        context.OutputArchivePath = Output;

        Assert.False(await new ArchiveStep(new ZipArchiver()).ExecuteAsync(context));

        Assert.Contains("missing", Assert.Single(context.Errors));
        Assert.False(File.Exists(Output));
        Assert.Equal("model", await File.ReadAllTextAsync(Source));
    }

    [Fact]
    public void RootContainment_DoesNotAllowDriveRootEscapeOrRejectSiblingDirectory()
    {
        var driveRoot = Path.GetPathRoot(_root)!;
        Assert.Equal(Path.Combine(driveRoot, "safe.nif"), PackingPathGuard.ResolveUnderRoot(driveRoot, "safe.nif"));
        Assert.Throws<InvalidOperationException>(() => PluginWriteGuard.EnsureOutsideGame(Path.Combine(driveRoot, "blocked.zip"), Path.Combine(driveRoot, "Data")));
        PluginWriteGuard.EnsureOutsideGame(Path.Combine(_root, "GameOther", "safe.zip"), Data);
    }
}
