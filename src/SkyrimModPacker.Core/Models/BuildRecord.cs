namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Records a completed build/pack operation for history tracking.
/// </summary>
public sealed class BuildRecord
{
    /// <summary>Profile name used for the build.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Version that was packed.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Full path to the created archive.</summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>Number of files included.</summary>
    public int FileCount { get; set; }

    /// <summary>Total size of the archive in bytes.</summary>
    public long ArchiveSizeBytes { get; set; }

    /// <summary>Timestamp of the build.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the build succeeded.</summary>
    public bool Success { get; set; }
}
