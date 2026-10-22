using SkyrimModPacker.Core.Models;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ModProfileValidatorTests
{
    [Fact]
    public void Validate_ValidProfile_ReturnsSuccess()
    {
        var profile = new ModProfile
        {
            Name = "MyAxe",
            Version = "1.0",
            Keywords = ["MyAxe"],
            PluginFileName = "MyAxe.esp"
        };

        var result = ModProfileValidator.Validate(profile);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_NullProfile_ReturnsFailure()
    {
        var result = ModProfileValidator.Validate(null!);

        Assert.False(result.IsSuccess);
        Assert.Contains("null", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyName_ReturnsFailure(string invalidName)
    {
        var profile = new ModProfile
        {
            Name = invalidName,
            Version = "1.0",
            Keywords = ["MyAxe"]
        };

        var result = ModProfileValidator.Validate(profile);

        Assert.False(result.IsSuccess);
        Assert.Contains("name", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_InvalidFileNameCharsInName_ReturnsFailure()
    {
        var profile = new ModProfile
        {
            Name = "My:Axe/Invalid?",
            Version = "1.0",
            Keywords = ["MyAxe"]
        };

        var result = ModProfileValidator.Validate(profile);

        Assert.False(result.IsSuccess);
        Assert.Contains("invalid", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MissingKeywordsAndPlugin_ReturnsFailure()
    {
        var profile = new ModProfile
        {
            Name = "EmptyMod",
            Version = "1.0",
            Keywords = [],
            PluginFileName = ""
        };

        var result = ModProfileValidator.Validate(profile);

        Assert.False(result.IsSuccess);
        Assert.Contains("at least one", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_OnlyKeywords_ReturnsSuccess()
    {
        var profile = new ModProfile
        {
            Name = "TexturesOnlyMod",
            Version = "2.1",
            Keywords = ["RetexturePack"],
            PluginFileName = ""
        };

        var result = ModProfileValidator.Validate(profile);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_OnlyPlugin_ReturnsSuccess()
    {
        var profile = new ModProfile
        {
            Name = "PluginOnlyMod",
            Version = "1.0",
            Keywords = [],
            PluginFileName = "Tweaks.esp"
        };

        var result = ModProfileValidator.Validate(profile);

        Assert.True(result.IsSuccess);
    }
}
