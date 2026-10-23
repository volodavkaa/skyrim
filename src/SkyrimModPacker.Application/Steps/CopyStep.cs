using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Application.Steps;

/// <summary>
/// Step 4: Staging copy step. Copies selected files into a temporary staging folder
/// preserving relative directory structure, or skips staging if direct packing is enabled.
/// </summary>
public sealed class CopyStep : IPackStep
{
    private readonly ILogger<CopyStep>? _logger;

    public string Name => "Prepare Files";
    public int Order => 400;

    public CopyStep(ILogger<CopyStep>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default)
    {
        var stagingDir = Path.Combine(
            Path.GetTempPath(),
            "SkyrimModPacker",
            "staging",
            Guid.NewGuid().ToString("N"));

        var completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackingPathGuard.ValidateSources(context.SelectedFiles, context.Profile, context.Settings.SkyrimDataPath);
            if (!context.Settings.UseTempDirectory && !context.Profile.EnableEsl)
            {
                _logger?.LogInformation("Direct packing mode selected. Staging directory skipped.");
                context.StagingDirectory = null;
                completed = true;
                return true;
            }

            PackingPathGuard.EnsureStagingDirectory(stagingDir, context.Settings.SkyrimDataPath);
            context.RegisterStagingDirectory(stagingDir);
            Directory.CreateDirectory(stagingDir);

            _logger?.LogInformation("Copying {Count} files to staging directory '{StagingDir}'...",
                context.SelectedFiles.Count, stagingDir);

            for (int i = 0; i < context.SelectedFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = context.SelectedFiles[i];
                var destinationPath = PackingPathGuard.ResolveUnderRoot(stagingDir, file.RelativePath);
                PluginWriteGuard.EnsureOutsideGame(destinationPath, context.Settings.SkyrimDataPath);
                PackingPathGuard.EnsureNoLinks(file.AbsolutePath);
                var destDirectory = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrEmpty(destDirectory) && !Directory.Exists(destDirectory))
                {
                    Directory.CreateDirectory(destDirectory);
                }

                PackingPathGuard.EnsureNoLinks(destinationPath);
                // Copy file asynchronously using FileStream
                await using (var sourceStream = new FileStream(file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                await using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await sourceStream.CopyToAsync(destStream, cancellationToken);
                }
            }

            _logger?.LogInformation("All files successfully staged.");
            completed = true;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = $"Failed while staging files: {ex.Message}";
            _logger?.LogError(ex, "{Message}", msg);
            context.Errors.Add(msg);

            return false;
        }
        finally
        {
            if (!completed && !context.TryCleanupStaging())
                _logger?.LogWarning("{Warning}", context.Warnings.Last());
        }
    }
}
