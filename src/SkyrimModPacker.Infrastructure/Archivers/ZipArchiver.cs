using System.IO.Compression;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.Archivers;

/// <summary>
/// Creates ZIP archives using <see cref="ZipArchive"/> from <c>System.IO.Compression</c>.
/// Preserves relative paths with standard forward slashes, reports progress, and supports cancellation.
/// </summary>
public sealed class ZipArchiver : IArchiver
{
    private const int BufferSize = 81920; // 80 KB
    private readonly ILogger<ZipArchiver>? _logger;

    public ZipArchiver(ILogger<ZipArchiver>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Extension => ".zip";

    /// <inheritdoc />
    public async Task CreateArchiveAsync(
        string outputPath,
        IReadOnlyList<(string SourcePath, string EntryPath)> files,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path cannot be null or empty.", nameof(outputPath));

        if (files == null || files.Count == 0)
            throw new ArgumentException("No files specified for archive creation.", nameof(files));

        cancellationToken.ThrowIfCancellationRequested();
        outputPath = PackingPathGuard.GetAbsolutePath(outputPath);
        PackingPathGuard.EnsureNoLinks(outputPath);
        var normalizedEntries = PackingPathGuard.ValidateEntries(files.Select(file => file.EntryPath));
        var preparedFiles = files.Select((file, index) => (
            SourcePath: PackingPathGuard.GetAbsolutePath(file.SourcePath), EntryPath: normalizedEntries[index])).ToList();
        foreach (var file in preparedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackingPathGuard.EnsureNoLinks(file.SourcePath);
            if (file.SourcePath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The archive output must not overwrite a source file.");
            if (!File.Exists(file.SourcePath))
                throw new FileNotFoundException($"Source file not found for archiving: '{file.SourcePath}'", file.SourcePath);
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Delete existing archive if present to ensure clean overwrite
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        _logger?.LogInformation("Creating ZIP archive at '{OutputPath}' with {FileCount} entries.",
            outputPath, files.Count);

        try
        {
            await using (var zipFileStream = new FileStream(
                             outputPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             BufferSize,
                             useAsync: true))
            using (var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                for (int i = 0; i < preparedFiles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (sourcePath, entryPath) = preparedFiles[i];
                    PackingPathGuard.EnsureNoLinks(outputPath);
                    PackingPathGuard.EnsureNoLinks(sourcePath);

                    if (!File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException($"Source file not found for archiving: '{sourcePath}'", sourcePath);
                    }

                    var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);

                    await using (var sourceStream = new FileStream(
                                     sourcePath,
                                     FileMode.Open,
                                     FileAccess.Read,
                                     FileShare.Read,
                                     BufferSize,
                                     useAsync: true))
                    await using (var entryStream = entry.Open())
                    {
                        await sourceStream.CopyToAsync(entryStream, BufferSize, cancellationToken);
                    }

                    progress?.Report((double)(i + 1) / files.Count);
                }
            }

            _logger?.LogInformation("ZIP archive '{OutputPath}' created successfully.", outputPath);
        }
        catch
        {
            // Clean up partial/corrupted archive on error or cancellation
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clean up incomplete archive '{OutputPath}'", outputPath);
            }

            throw;
        }
    }
}
