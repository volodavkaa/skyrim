using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.FileSystem;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ModScannerTests : IDisposable
{
    private readonly string _tempDataDir;
    private readonly ModScanner _scanner;

    public ModScannerTests()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), "SkyrimModPackerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);
        _scanner = new ModScanner();

        SetupMockSkyrimData();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDataDir))
            {
                Directory.Delete(_tempDataDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    private void SetupMockSkyrimData()
    {
        // Meshes
        CreateTestFile(@"meshes\weapons\MyAxe\axe.nif", 1024);
        CreateTestFile(@"meshes\weapons\MyAxe\axe_scabbard.nif", 512);
        CreateTestFile(@"meshes\weapons\MyAxe_standalone.nif", 256);
        CreateTestFile(@"meshes\armor\IronArmor\armor.nif", 2048); // Unrelated

        // Textures
        CreateTestFile(@"textures\weapons\MyAxe\axe_d.dds", 4096);
        CreateTestFile(@"textures\weapons\MyAxe\axe_n.dds", 2048);
        CreateTestFile(@"textures\armor\IronArmor\armor_d.dds", 4096); // Unrelated

        // Plugins
        CreateTestFile(@"MyAxe.esp", 100);
        CreateTestFile(@"Skyrim.esm", 10000); // Unrelated
    }

    private void CreateTestFile(string relativePath, int byteLength)
    {
        var fullPath = Path.Combine(_tempDataDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(fullPath, new byte[byteLength]);
    }

    [Fact]
    public async Task ScanAsync_WithMatchingKeywordsAndPlugin_FindsAllRelevantFiles()
    {
        var profile = new ModProfile
        {
            Name = "MyAxe",
            Version = "1.0",
            Keywords = ["MyAxe"],
            PluginFileName = "MyAxe.esp"
        };

        var result = await _scanner.ScanAsync(_tempDataDir, profile);

        Assert.True(result.IsSuccess);
        var files = result.Value;

        // 3 meshes (folder + standalone) + 2 textures + 1 plugin = 6 files
        Assert.Equal(6, files.Count);

        // Verify relative paths are preserved
        Assert.Contains(files, f => f.RelativePath == @"meshes\weapons\MyAxe\axe.nif" && f.Category == FileCategory.Mesh);
        Assert.Contains(files, f => f.RelativePath == @"meshes\weapons\MyAxe\axe_scabbard.nif" && f.Category == FileCategory.Mesh);
        Assert.Contains(files, f => f.RelativePath == @"meshes\weapons\MyAxe_standalone.nif" && f.Category == FileCategory.Mesh);
        Assert.Contains(files, f => f.RelativePath == @"textures\weapons\MyAxe\axe_d.dds" && f.Category == FileCategory.Texture);
        Assert.Contains(files, f => f.RelativePath == @"textures\weapons\MyAxe\axe_n.dds" && f.Category == FileCategory.Texture);
        Assert.Contains(files, f => f.RelativePath == "MyAxe.esp" && f.Category == FileCategory.Plugin);

        // Verify unrelated files are excluded
        Assert.DoesNotContain(files, f => f.RelativePath.Contains("IronArmor"));
        Assert.DoesNotContain(files, f => f.RelativePath.Contains("Skyrim.esm"));

        // Verify sizes are populated
        Assert.All(files, f => Assert.True(f.SizeBytes > 0));
        Assert.All(files, f => Assert.True(f.IsSelected));
    }

    [Fact]
    public async Task ScanAsync_CaseInsensitiveKeyword_MatchesFiles()
    {
        var profile = new ModProfile
        {
            Name = "myaxe",
            Version = "1.0",
            Keywords = ["myaxe"], // lower-case
            PluginFileName = "myaxe.esp" // lower-case
        };

        var result = await _scanner.ScanAsync(_tempDataDir, profile);

        Assert.True(result.IsSuccess);
        Assert.Equal(6, result.Value.Count);
    }

    [Fact]
    public async Task ScanAsync_MultipleKeywords_FindsAllMatches()
    {
        var profile = new ModProfile
        {
            Name = "MultiMod",
            Version = "1.0",
            Keywords = ["MyAxe", "IronArmor"],
            PluginFileName = "MyAxe.esp"
        };

        var result = await _scanner.ScanAsync(_tempDataDir, profile);

        Assert.True(result.IsSuccess);
        // 6 files from MyAxe + 2 files from IronArmor = 8 files
        Assert.Equal(8, result.Value.Count);
        Assert.Contains(result.Value, f => f.RelativePath.Contains("IronArmor"));
    }

    [Fact]
    public async Task ScanAsync_PluginWithoutExtension_ResolvesAutomatically()
    {
        var profile = new ModProfile
        {
            Name = "MyAxe",
            Version = "1.0",
            Keywords = ["MyAxe"],
            PluginFileName = "MyAxe" // Omitted .esp
        };

        var result = await _scanner.ScanAsync(_tempDataDir, profile);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value, f => f.RelativePath == "MyAxe.esp");
    }

    [Fact]
    public async Task ScanAsync_NonExistentDataPath_ReturnsFailure()
    {
        var nonExistentPath = Path.Combine(_tempDataDir, "DoesNotExist");
        var profile = new ModProfile
        {
            Name = "TestMod",
            Keywords = ["Test"]
        };

        var result = await _scanner.ScanAsync(nonExistentPath, profile);

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_InvalidProfile_ReturnsFailure()
    {
        var profile = new ModProfile
        {
            Name = "", // Invalid
            Keywords = []
        };

        var result = await _scanner.ScanAsync(_tempDataDir, profile);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ScanAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var profile = new ModProfile
        {
            Name = "MyAxe",
            Keywords = ["MyAxe"]
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _scanner.ScanAsync(_tempDataDir, profile, cts.Token);
        });
    }
}
