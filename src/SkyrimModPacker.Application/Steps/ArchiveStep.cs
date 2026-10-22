using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Application.Steps;

/// <summary>
/// Step 5: Creates the final mod archive using the configured <see cref="IArchiver"/>,
/// cleans up temporary staging files, and records the build in history.
/// </summary>
public sealed class ArchiveStep : IPackStep
{
    private readonly IArchiver _archiver;
    private readonly IBuildHistoryRepository? _historyRepository;
    private readonly ILogger<ArchiveStep>? _logger;

    public string Name => "Create Archive";
    public int Order => 500;

    public ArchiveStep(
        IArchiver archiver,
        IBuildHistoryRepository? historyRepository = null,
        ILogger<ArchiveStep>? logger = null)
    {
        _archiver = archiver;
        _historyRepository = historyRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IReadOnlyList<(string SourcePath, string EntryPath)> filesToArchive;

            if (!string.IsNullOrEmpty(context.StagingDirectory) && Directory.Exists(context.StagingDirectory))
            {
                // Archive from staging directory
                filesToArchive = Directory
                    .EnumerateFiles(context.StagingDirectory, "*", SearchOption.AllDirectories)
                    .Select(fullPath => (
                        SourcePath: fullPath,
                        EntryPath: Path.GetRelativePath(context.StagingDirectory, fullPath)))
                    .ToList();
            }
            else
            {
                // Direct archive from original Skyrim Data files
                filesToArchive = context.SelectedFiles
                    .Select(f => (SourcePath: f.AbsolutePath, EntryPath: f.RelativePath))
                    .ToList();
            }

            _logger?.LogInformation("Archiving {Count} files into '{ArchivePath}'...",
                filesToArchive.Count, context.OutputArchivePath);

            await _archiver.CreateArchiveAsync(
                context.OutputArchivePath,
                filesToArchive,
                context.Progress,
                cancellationToken);

            if (File.Exists(context.OutputArchivePath))
            {
                var fileInfo = new FileInfo(context.OutputArchivePath);
                context.ArchiveSizeBytes = fileInfo.Length;

                _logger?.LogInformation("Archive created successfully ({Size} bytes).", context.ArchiveSizeBytes);

                // Record successful build in history
                if (_historyRepository != null)
                {
                    await _historyRepository.AddAsync(new BuildRecord
                    {
                        ProfileName = context.Profile.Name,
                        Version = context.Profile.Version,
                        ArchivePath = context.OutputArchivePath,
                        FileCount = filesToArchive.Count,
                        ArchiveSizeBytes = context.ArchiveSizeBytes,
                        Timestamp = DateTime.UtcNow,
                        Success = true
                    }, cancellationToken);
                }

                return true;
            }

            var msg = "Archive creation completed but output file was not found.";
            _logger?.LogError("{Message}", msg);
            context.Errors.Add(msg);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = $"Archive creation failed: {ex.Message}";
            _logger?.LogError(ex, "{Message}", msg);
            context.Errors.Add(msg);
            return false;
        }
        finally
        {
            // Clean up staging directory if one was created
            if (!string.IsNullOrEmpty(context.StagingDirectory))
            {
                try
                {
                    if (Directory.Exists(context.StagingDirectory))
                    {
                        Directory.Delete(context.StagingDirectory, recursive: true);
                        _logger?.LogInformation("Cleaned up staging directory: '{StagingDir}'", context.StagingDirectory);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete temporary staging directory '{StagingDir}'",
                        context.StagingDirectory);
                }
            }
        }
    }
}
