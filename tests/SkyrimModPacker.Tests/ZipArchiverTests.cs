using System.IO.Compression;
using SkyrimModPacker.Infrastructure.Archivers;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ZipArchiverTests : IDisposable
{
    private readonly string _testDir;
    private readonly ZipArchiver _archiver;

    public ZipArchiverTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ZipArchiverTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _archiver = new ZipArchiver();
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
    public async Task CreateArchiveAsync_CreatesValidZipWithCorrectStructure()
    {
        var file1 = Path.Combine(_testDir, "blade.nif");
        var file2 = Path.Combine(_testDir, "blade_d.dds");
        var file3 = Path.Combine(_testDir, "plugin.esp");

        await File.WriteAllTextAsync(file1, "nif-content");
        await File.WriteAllTextAsync(file2, "dds-content");
        await File.WriteAllTextAsync(file3, "esp-content");

        var zipOutput = Path.Combine(_testDir, "output", "MyMod-1.0.zip");

        var entries = new List<(string SourcePath, string EntryPath)>
        {
            (file1, @"meshes\weapons\MyAxe\blade.nif"),
            (file2, @"textures\weapons\MyAxe\blade_d.dds"),
            (file3, @"MyMod.esp")
        };

        var progressValues = new List<double>();
        var progress = new Progress<double>(progressValues.Add);

        await _archiver.CreateArchiveAsync(zipOutput, entries, progress);

        Assert.True(File.Exists(zipOutput));
        Assert.True(new FileInfo(zipOutput).Length > 0);

        // Verify ZIP contents using standard ZipArchive
        using var archive = ZipFile.OpenRead(zipOutput);
        Assert.Equal(3, archive.Entries.Count);

        // Ensure entry keys use forward slashes
        Assert.NotNull(archive.GetEntry("meshes/weapons/MyAxe/blade.nif"));
        Assert.NotNull(archive.GetEntry("textures/weapons/MyAxe/blade_d.dds"));
        Assert.NotNull(archive.GetEntry("MyMod.esp"));

        // Verify contents of extracted entry
        var nifEntry = archive.GetEntry("meshes/weapons/MyAxe/blade.nif");
        using var reader = new StreamReader(nifEntry!.Open());
        var content = await reader.ReadToEndAsync();
        Assert.Equal("nif-content", content);
    }

    [Fact]
    public async Task CreateArchiveAsync_Cancellation_CleansUpIncompleteFile()
    {
        var file1 = Path.Combine(_testDir, "large.dat");
        await File.WriteAllBytesAsync(file1, new byte[1024 * 1024]); // 1 MB

        var zipOutput = Path.Combine(_testDir, "cancelled.zip");
        var entries = new List<(string SourcePath, string EntryPath)>
        {
            (file1, "large.dat")
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _archiver.CreateArchiveAsync(zipOutput, entries, cancellationToken: cts.Token);
        });

        // The incomplete zip file should be deleted on cancellation
        Assert.False(File.Exists(zipOutput));
    }

    [Fact]
    public async Task CreateArchiveAsync_MissingSourceFile_ThrowsFileNotFoundException()
    {
        var nonExistent = Path.Combine(_testDir, "doesnotexist.nif");
        var zipOutput = Path.Combine(_testDir, "test.zip");

        var entries = new List<(string SourcePath, string EntryPath)>
        {
            (nonExistent, @"meshes\blade.nif")
        };

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await _archiver.CreateArchiveAsync(zipOutput, entries);
        });

        Assert.False(File.Exists(zipOutput));
    }
}
