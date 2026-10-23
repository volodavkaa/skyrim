using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.FileSystem;
using SkyrimModPacker.Infrastructure.Persistence;
using Xunit;

namespace SkyrimModPacker.Tests;

public class PackingPipelineTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _skyrimDataDir;
    private readonly string _outputDir;
    private readonly string _appDataDir;

    public PackingPipelineTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PackingPipelineTests", Guid.NewGuid().ToString("N"));
        _skyrimDataDir = Path.Combine(_testRoot, "Skyrim", "Data");
        _outputDir = Path.Combine(_testRoot, "Desktop");
        _appDataDir = Path.Combine(_testRoot, "AppData");

        Directory.CreateDirectory(_skyrimDataDir);
        Directory.CreateDirectory(_outputDir);
        Directory.CreateDirectory(_appDataDir);

        SetupSkyrimMockData();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private void SetupSkyrimMockData()
    {
        // Meshes
        CreateFile(@"meshes\weapons\MyAxe\blade.nif", "mesh-blade-binary");
        CreateFile(@"meshes\weapons\MyAxe\handle.nif", "mesh-handle-binary");
        CreateFile(@"meshes\armor\IronArmor\armor.nif", "unrelated-mesh");

        // Textures
        CreateFile(@"textures\weapons\MyAxe\blade_d.dds", "texture-diffuse");
        CreateFile(@"textures\weapons\MyAxe\blade_n.dds", "texture-normal");
        CreateFile(@"textures\armor\IronArmor\armor_d.dds", "unrelated-texture");

        // Plugin
        CreateFile(@"MyAxe.esp", "plugin-data-header");
        CreateFile(@"Skyrim.esm", "base-game-esm");
    }

    private void CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_skyrimDataDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, content);
    }

    [Theory]
    [InlineData(true)]  // Test via temporary staging folder
    [InlineData(false)] // Test direct packing mode
    public async Task ExecuteAsync_FullPipeline_CreatesValidNexusModArchive(bool useTempDirectory)
    {
        var scanner = new ModScanner();
        var archiver = new ZipArchiver();
        var historyRepo = new JsonBuildHistoryRepository(_appDataDir);

        var steps = new List<IPackStep>
        {
            new ScanStep(scanner),
            new FilterStep(),
            new ValidateStep(archiver),
            new CopyStep(),
            new ArchiveStep(archiver, historyRepo)
        };

        var pipeline = new PackingPipeline(steps, NullLogger<PackingPipeline>.Instance);

        var profile = new ModProfile
        {
            Name = "MyAxe",
            Version = "1.0",
            Keywords = ["MyAxe"],
            PluginFileName = "MyAxe.esp"
        };

        var settings = new AppSettings
        {
            SkyrimDataPath = _skyrimDataDir,
            OutputDirectory = _outputDir,
            UseTempDirectory = useTempDirectory,
            OverwriteExistingArchive = true
        };

        var progressReports = new List<double>();
        var progress = new Progress<double>(progressReports.Add);

        // Execute the entire pipeline
        var resultContext = await pipeline.ExecuteAsync(profile, settings, progress);

        // 1. Pipeline succeeded with no errors
        Assert.True(resultContext.IsSuccess);
        Assert.Empty(resultContext.Errors);

        // 2. Output ZIP exists at Desktop\MyAxe-1.0.zip
        var expectedArchive = Path.Combine(_outputDir, "MyAxe-1.0.zip");
        Assert.Equal(expectedArchive, resultContext.OutputArchivePath);
        Assert.True(File.Exists(expectedArchive));
        Assert.True(resultContext.ArchiveSizeBytes > 0);

        // 3. Verify ZIP archive structure matches Nexus Mods Data requirements
        using (var zip = ZipFile.OpenRead(expectedArchive))
        {
            // 2 meshes + 2 textures + 1 plugin = 5 files
            Assert.Equal(5, zip.Entries.Count);

            var entryPaths = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("meshes/weapons/MyAxe/blade.nif", entryPaths);
            Assert.Contains("meshes/weapons/MyAxe/handle.nif", entryPaths);
            Assert.Contains("textures/weapons/MyAxe/blade_d.dds", entryPaths);
            Assert.Contains("textures/weapons/MyAxe/blade_n.dds", entryPaths);
            Assert.Contains("MyAxe.esp", entryPaths);

            // Unrelated files should NOT be present
            Assert.DoesNotContain(entryPaths, p => p.Contains("IronArmor"));
            Assert.DoesNotContain(entryPaths, p => p.Contains("Skyrim.esm"));

            // Verify content integrity
            var espEntry = zip.GetEntry("MyAxe.esp");
            using var reader = new StreamReader(espEntry!.Open());
            var espContent = await reader.ReadToEndAsync();
            Assert.Equal("plugin-data-header", espContent);
        }

        // 4. Staging directory (if used) was cleaned up
        if (useTempDirectory && !string.IsNullOrEmpty(resultContext.StagingDirectory))
        {
            Assert.False(Directory.Exists(resultContext.StagingDirectory));
        }

        // 5. Build record was stored in history
        var history = await historyRepo.LoadAllAsync();
        Assert.Single(history);
        var record = history[0];
        Assert.Equal("MyAxe", record.ProfileName);
        Assert.Equal("1.0", record.Version);
        Assert.Equal(5, record.FileCount);
        Assert.True(record.Success);
        Assert.Equal(expectedArchive, record.ArchivePath);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFileUnchecked_ExcludesFromFinalArchive()
    {
        var scanner = new ModScanner();
        var archiver = new ZipArchiver();

        var steps = new List<IPackStep>
        {
            new ScanStep(scanner),
            new FilterStep(),
            new ValidateStep(archiver),
            new CopyStep(),
            new ArchiveStep(archiver)
        };

        var pipeline = new PackingPipeline(steps, NullLogger<PackingPipeline>.Instance);

        var profile = new ModProfile
        {
            Name = "MyAxe",
            Version = "2.0",
            Keywords = ["MyAxe"],
            PluginFileName = "MyAxe.esp"
        };

        var settings = new AppSettings
        {
            SkyrimDataPath = _skyrimDataDir,
            OutputDirectory = _outputDir,
            UseTempDirectory = false,
            OverwriteExistingArchive = true
        };

        // Scan files beforehand (simulating UI preview)
        var scanResult = await scanner.ScanAsync(_skyrimDataDir, profile);
        Assert.True(scanResult.IsSuccess);
        var scannedFiles = scanResult.Value;

        // User unchecks the normal map texture in UI
        var normalMap = scannedFiles.First(f => f.RelativePath.Contains("blade_n.dds"));
        normalMap.IsSelected = false;

        // Exercise selection through the public pipeline entry point.
        var context = await pipeline.ExecuteAsync(profile, settings, fileSelection: scannedFiles);

        Assert.True(context.IsSuccess);

        // Archive should have 4 files instead of 5, without blade_n.dds
        using var zip = ZipFile.OpenRead(context.OutputArchivePath);
        Assert.Equal(4, zip.Entries.Count);
        Assert.Null(zip.GetEntry("textures/weapons/MyAxe/blade_n.dds"));
    }
}
