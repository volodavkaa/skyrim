namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// Abstraction for archive creation.
/// Implementations: ZipArchiver (MVP), SevenZipArchiver (future).
/// </summary>
public interface IArchiver
{
    /// <summary>File extension produced by this archiver (e.g. ".zip").</summary>
    string Extension { get; }

    /// <summary>
    /// Creates an archive from a collection of files.
    /// </summary>
    /// <param name="outputPath">Full path to the output archive file.</param>
    /// <param name="files">Tuples of (absolute source path, relative entry path inside archive).</param>
    /// <param name="progress">Optional progress callback (0.0–1.0).</param>
    /// <param name="cancellationToken">Token to support cancellation.</param>
    Task CreateArchiveAsync(
        string outputPath,
        IReadOnlyList<(string SourcePath, string EntryPath)> files,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
