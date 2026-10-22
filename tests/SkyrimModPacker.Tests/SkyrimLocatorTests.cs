using SkyrimModPacker.Infrastructure.Services;
using Xunit;

namespace SkyrimModPacker.Tests;

public class SkyrimLocatorTests : IDisposable
{
    private readonly string _tempDir;

    public SkyrimLocatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SkyrimLocatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void IsValidSkyrimData_WithSkyrimEsm_ReturnsTrue()
    {
        var dataDir = Path.Combine(_tempDir, "Data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "Skyrim.esm"), "fake header");

        var isValid = SkyrimLocator.IsValidSkyrimData(dataDir);

        Assert.True(isValid);
    }

    [Fact]
    public void IsValidSkyrimData_WithMeshesFolder_ReturnsTrue()
    {
        var dataDir = Path.Combine(_tempDir, "Data");
        Directory.CreateDirectory(Path.Combine(dataDir, "meshes"));

        var isValid = SkyrimLocator.IsValidSkyrimData(dataDir);

        Assert.True(isValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\DoesNotExist_123456789")]
    public void IsValidSkyrimData_NonExistentOrInvalid_ReturnsFalse(string? path)
    {
        var isValid = SkyrimLocator.IsValidSkyrimData(path);

        Assert.False(isValid);
    }
}
