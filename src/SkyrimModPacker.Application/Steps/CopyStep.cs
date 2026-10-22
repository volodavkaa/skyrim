using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;

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
        cancellationToken.ThrowIfCancellationRequested();

        if (!context.Settings.UseTempDirectory)
        {
            _logger?.LogInformation("Direct packing mode selected. Staging directory skipped.");
            context.StagingDirectory = null;
            return true;
        }

        var stagingDir = Path.Combine(
            Path.GetTempPath(),
            "SkyrimModPacker",
            "staging",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(stagingDir);
            context.StagingDirectory = stagingDir;

            _logger?.LogInformation("Copying {Count} files to staging directory '{StagingDir}'...",
                context.SelectedFiles.Count, stagingDir);

            for (int i = 0; i < context.SelectedFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = context.SelectedFiles[i];
                var destinationPath = Path.Combine(stagingDir, file.RelativePath);
                var destDirectory = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrEmpty(destDirectory) && !Directory.Exists(destDirectory))
                {
                    Directory.CreateDirectory(destDirectory);
                }

                // Copy file asynchronously using FileStream
                await using (var sourceStream = new FileStream(file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                await using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await sourceStream.CopyToAsync(destStream, cancellationToken);
                }
            }

            _logger?.LogInformation("All files successfully staged.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = $"Failed while staging files: {ex.Message}";
            _logger?.LogError(ex, "{Message}", msg);
            context.Errors.Add(msg);

            // Clean up staging on failure
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup error
            }

            return false;
        }
    }
}
