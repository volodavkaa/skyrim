using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// Shared context passed through all pipeline steps.
/// Accumulates state as each step processes.
/// </summary>
public sealed class PackingContext
{
    /// <summary>The mod profile being packed.</summary>
    public required ModProfile Profile { get; init; }

    /// <summary>Application settings.</summary>
    public required AppSettings Settings { get; init; }

    /// <summary>Files discovered during scanning.</summary>
    public List<ScannedFile> ScannedFiles { get; set; } = [];

    /// <summary>Optional preview snapshot used to preserve selection when scanning again.</summary>
    public IReadOnlyList<ScannedFile>? FileSelection { get; init; }

    /// <summary>Files selected for packing (after filtering).</summary>
    public List<ScannedFile> SelectedFiles { get; set; } = [];

    /// <summary>Path to the temporary staging directory (if used).</summary>
    public string? StagingDirectory { get; set; }

    /// <summary>Full path to the output archive.</summary>
    public string OutputArchivePath { get; set; } = string.Empty;

    /// <summary>Log messages accumulated during pipeline execution.</summary>
    public List<string> LogMessages { get; } = [];

    /// <summary>Errors accumulated during pipeline execution.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Total size in bytes of the created archive file.</summary>
    public long ArchiveSizeBytes { get; set; }

    /// <summary>Indicates whether the pipeline finished successfully with no errors.</summary>
    public bool IsSuccess => Errors.Count == 0 && !string.IsNullOrEmpty(OutputArchivePath) && System.IO.File.Exists(OutputArchivePath);

    /// <summary>Progress callback (0.0–1.0).</summary>
    public IProgress<double>? Progress { get; set; }
}
