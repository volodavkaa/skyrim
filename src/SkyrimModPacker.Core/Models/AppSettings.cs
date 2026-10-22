namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Application-level settings persisted in JSON.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Path to the Skyrim Data directory.</summary>
    public string SkyrimDataPath { get; set; } = string.Empty;

    /// <summary>Output directory for packed archives (default: Desktop).</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Whether to use a temporary directory for staging files before archiving.</summary>
    public bool UseTempDirectory { get; set; } = true;

    /// <summary>Preferred theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Whether to overwrite an existing archive with the same name in the output directory.</summary>
    public bool OverwriteExistingArchive { get; set; } = false;

    /// <summary>UI language code: "uk" or "en".</summary>
    public string Language { get; set; } = "en";
}
