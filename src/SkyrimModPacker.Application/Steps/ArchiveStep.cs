using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Common;

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

        var ownsStagingDirectory = false;
        try
        {
            if (!string.IsNullOrEmpty(context.StagingDirectory))
            {
                PackingPathGuard.EnsureStagingDirectory(context.StagingDirectory, context.Settings.SkyrimDataPath);
                ownsStagingDirectory = true;
            }
            PluginWriteGuard.EnsureOutsideGame(context.OutputArchivePath, context.Settings.SkyrimDataPath);
            IReadOnlyList<(string SourcePath, string EntryPath)> filesToArchive;

            if (!string.IsNullOrEmpty(context.StagingDirectory))
            {
                PackingPathGuard.EnsureStagingDirectory(context.StagingDirectory, context.Settings.SkyrimDataPath);
                if (!Directory.Exists(context.StagingDirectory))
                    throw new DirectoryNotFoundException("The prepared staging directory is missing. Pack again.");
                // Archive from staging directory
                filesToArchive = EnumerateStagedFiles(context.StagingDirectory, cancellationToken)
                    .Select(fullPath => (
                        SourcePath: fullPath,
                        EntryPath: Path.GetRelativePath(context.StagingDirectory, fullPath)))
                    .ToList();
            }
            else
            {
                PackingPathGuard.ValidateSources(context.SelectedFiles, context.Profile, context.Settings.SkyrimDataPath);
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
            if (ownsStagingDirectory && !string.IsNullOrEmpty(context.StagingDirectory))
            {
                try
                {
                    if (Directory.Exists(context.StagingDirectory))
                    {
                        PackingPathGuard.DeleteStagingDirectory(context.StagingDirectory, context.Settings.SkyrimDataPath);
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

    private static IEnumerable<string> EnumerateStagedFiles(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PackingPathGuard.EnsureNoLinks(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            token.ThrowIfCancellationRequested();
            PackingPathGuard.EnsureNoLinks(file);
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
            foreach (var file in EnumerateStagedFiles(child, token)) yield return file;
    }
}
