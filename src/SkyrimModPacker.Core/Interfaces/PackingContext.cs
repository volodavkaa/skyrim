using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Common;

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

    /// <summary>Staging created by this operation, retained independently of later step changes.</summary>
    public string? OwnedStagingDirectory { get; private set; }

    public PackingStatus Status { get; set; } = PackingStatus.NotStarted;

    /// <summary>Set by the archive step only after the current archive operation succeeds.</summary>
    public bool ArchiveCreated { get; set; }

    /// <summary>Full path to the output archive.</summary>
    public string OutputArchivePath { get; set; } = string.Empty;

    /// <summary>Log messages accumulated during pipeline execution.</summary>
    public List<string> LogMessages { get; } = [];

    /// <summary>Errors accumulated during pipeline execution.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Non-fatal diagnostics, including failures to release temporary files.</summary>
    public List<string> Warnings { get; } = [];
    public List<PluginDiagnostic> PluginDiagnostics { get; } = [];
    public List<string> ExternalDependencies { get; } = [];

    /// <summary>Total size in bytes of the created archive file.</summary>
    public long ArchiveSizeBytes { get; set; }

    /// <summary>Indicates whether the pipeline finished successfully with no errors.</summary>
    public bool IsSuccess => Status == PackingStatus.Succeeded && ArchiveCreated && Errors.Count == 0 &&
                             !string.IsNullOrEmpty(OutputArchivePath) && System.IO.File.Exists(OutputArchivePath);

    /// <summary>Progress callback (0.0–1.0).</summary>
    public IProgress<double>? Progress { get; set; }

    public void RegisterStagingDirectory(string directory)
    {
        PackingPathGuard.EnsureStagingDirectory(directory, Settings.SkyrimDataPath);
        if (OwnedStagingDirectory != null)
            throw new InvalidOperationException("This operation already owns a staging directory.");
        StagingDirectory = PackingPathGuard.GetAbsolutePath(directory);
        OwnedStagingDirectory = StagingDirectory;
    }

    public void EnsureOwnsStagingDirectory()
    {
        if (string.IsNullOrEmpty(StagingDirectory))
            throw new InvalidOperationException("No staging directory has been prepared.");
        PackingPathGuard.EnsureStagingDirectory(StagingDirectory, Settings.SkyrimDataPath);
        if (!string.Equals(OwnedStagingDirectory, PackingPathGuard.GetAbsolutePath(StagingDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The staging directory does not belong to this packing operation.");
    }

    /// <summary>Cleanup ignores cancellation and never substitutes a different directory.</summary>
    public bool TryCleanupStaging()
    {
        if (OwnedStagingDirectory == null) return true;
        try
        {
            PackingPathGuard.DeleteStagingDirectory(OwnedStagingDirectory, Settings.SkyrimDataPath);
            OwnedStagingDirectory = null;
            return true;
        }
        catch (Exception ex)
        {
            var message = $"Could not clean temporary staging '{OwnedStagingDirectory}': {ex.Message}";
            if (!Warnings.Contains(message))
            {
                Warnings.Add(message);
                LogMessages.Add(message);
            }
            return false;
        }
    }
}
