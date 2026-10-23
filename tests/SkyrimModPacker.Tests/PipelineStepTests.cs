using NSubstitute;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using Xunit;

namespace SkyrimModPacker.Tests;

public class PipelineStepTests : IDisposable
{
    private readonly string _testDir;
    private string Data => Path.Combine(_testDir, "Game", "Data");

    public PipelineStepTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PipelineStepTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(Path.Combine(Data, "meshes", "weapons"));
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
    public async Task ScanStep_ReusesAlreadyScannedFiles()
    {
        var scanner = Substitute.For<IModScanner>();
        var step = new ScanStep(scanner);

        var existingFile = new ScannedFile
        {
            AbsolutePath = @"C:\Fake\file.nif",
            RelativePath = @"meshes\file.nif",
            IsSelected = true
        };

        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "Test" },
            Settings = new AppSettings(),
            ScannedFiles = [existingFile]
        };

        var success = await step.ExecuteAsync(context);

        Assert.True(success);
        // Should not have called scanner since files were already present
        await scanner.DidNotReceiveWithAnyArgs().ScanAsync(default!, default!, default);
    }

    [Fact]
    public async Task ScanStep_NoFilesFound_ReturnsFalseAndAddsError()
    {
        var scanner = Substitute.For<IModScanner>();
        scanner.ScanAsync(Arg.Any<string>(), Arg.Any<ModProfile>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<ScannedFile>>.Success([]));

        var step = new ScanStep(scanner);
        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "EmptyMod" },
            Settings = new AppSettings { SkyrimDataPath = _testDir }
        };

        var success = await step.ExecuteAsync(context);

        Assert.False(success);
        Assert.NotEmpty(context.Errors);
    }

    [Fact]
    public async Task FilterStep_FiltersToOnlySelectedFiles()
    {
        var step = new FilterStep();
        var file1 = new ScannedFile { RelativePath = "file1.nif", IsSelected = true };
        var file2 = new ScannedFile { RelativePath = "file2.dds", IsSelected = false };
        var file3 = new ScannedFile { RelativePath = "file3.esp", IsSelected = true };

        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "Test" },
            Settings = new AppSettings(),
            ScannedFiles = [file1, file2, file3]
        };

        var success = await step.ExecuteAsync(context);

        Assert.True(success);
        Assert.Equal(2, context.SelectedFiles.Count);
        Assert.Contains(file1, context.SelectedFiles);
        Assert.Contains(file3, context.SelectedFiles);
        Assert.DoesNotContain(file2, context.SelectedFiles);
    }

    [Fact]
    public async Task FilterStep_NoSelectedFiles_ReturnsFalse()
    {
        var step = new FilterStep();
        var file1 = new ScannedFile { RelativePath = "file1.nif", IsSelected = false };

        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "Test" },
            Settings = new AppSettings(),
            ScannedFiles = [file1]
        };

        var success = await step.ExecuteAsync(context);

        Assert.False(success);
        Assert.Empty(context.SelectedFiles);
        Assert.NotEmpty(context.Errors);
    }

    [Fact]
    public async Task ValidateStep_ArchiveAlreadyExists_FailsWhenOverwriteDisabled()
    {
        var archiver = Substitute.For<IArchiver>();
        archiver.Extension.Returns(".zip");

        var existingZip = Path.Combine(_testDir, "MyMod-1.0.zip");
        await File.WriteAllTextAsync(existingZip, "fake zip");

        var dummySource = Path.Combine(Data, "meshes", "test.nif");
        await File.WriteAllTextAsync(dummySource, "nif");

        var step = new ValidateStep(archiver);
        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "MyMod", Version = "1.0", Keywords = ["MyMod"] },
            Settings = new AppSettings { SkyrimDataPath = Data, OutputDirectory = _testDir, OverwriteExistingArchive = false },
            SelectedFiles = [new ScannedFile { AbsolutePath = dummySource, RelativePath = "meshes\\test.nif" }]
        };

        var success = await step.ExecuteAsync(context);

        Assert.False(success);
        Assert.Contains("already exists", context.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateStep_ArchiveAlreadyExists_SucceedsWhenOverwriteEnabled()
    {
        var archiver = Substitute.For<IArchiver>();
        archiver.Extension.Returns(".zip");

        var existingZip = Path.Combine(_testDir, "MyMod-1.0.zip");
        await File.WriteAllTextAsync(existingZip, "fake zip");

        var dummySource = Path.Combine(Data, "meshes", "test.nif");
        await File.WriteAllTextAsync(dummySource, "nif");

        var step = new ValidateStep(archiver);
        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "MyMod", Version = "1.0", Keywords = ["MyMod"] },
            Settings = new AppSettings { SkyrimDataPath = Data, OutputDirectory = _testDir, OverwriteExistingArchive = true },
            SelectedFiles = [new ScannedFile { AbsolutePath = dummySource, RelativePath = "meshes\\test.nif" }]
        };

        var success = await step.ExecuteAsync(context);

        Assert.True(success);
        Assert.Equal(existingZip, context.OutputArchivePath);
    }

    [Fact]
    public async Task ValidateStep_MissingSourceFile_ReturnsFalse()
    {
        var archiver = Substitute.For<IArchiver>();
        archiver.Extension.Returns(".zip");

        var step = new ValidateStep(archiver);
        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "MyMod", Version = "1.0", Keywords = ["MyMod"] },
            Settings = new AppSettings { SkyrimDataPath = Data, OutputDirectory = _testDir },
            SelectedFiles = [new ScannedFile { AbsolutePath = Path.Combine(Data, "meshes", "missing.nif"), RelativePath = "meshes\\missing.nif" }]
        };

        var success = await step.ExecuteAsync(context);

        Assert.False(success);
        Assert.Contains("not found", context.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopyStep_DirectMode_SkipsStaging()
    {
        var step = new CopyStep();
        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "MyMod" },
            Settings = new AppSettings { UseTempDirectory = false }
        };

        var success = await step.ExecuteAsync(context);

        Assert.True(success);
        Assert.Null(context.StagingDirectory);
    }

    [Fact]
    public async Task CopyStep_TempMode_CopiesFilesPreservingStructure()
    {
        var step = new CopyStep();
        var srcFile = Path.Combine(Data, "meshes", "weapons", "source.nif");
        await File.WriteAllTextAsync(srcFile, "content");

        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "MyMod" },
            Settings = new AppSettings { SkyrimDataPath = Data, UseTempDirectory = true },
            SelectedFiles = [new ScannedFile { AbsolutePath = srcFile, RelativePath = @"meshes\weapons\source.nif" }]
        };

        var success = await step.ExecuteAsync(context);

        try
        {
            Assert.True(success);
            Assert.NotNull(context.StagingDirectory);
            Assert.True(Directory.Exists(context.StagingDirectory));

            var copiedPath = Path.Combine(context.StagingDirectory, @"meshes\weapons\source.nif");
            Assert.True(File.Exists(copiedPath));
            Assert.Equal("content", await File.ReadAllTextAsync(copiedPath));
        }
        finally
        {
            if (context.StagingDirectory != null && Directory.Exists(context.StagingDirectory))
            {
                Directory.Delete(context.StagingDirectory, recursive: true);
            }
        }
    }
}
