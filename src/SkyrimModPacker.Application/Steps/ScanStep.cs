using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Application.Steps;

/// <summary>
/// Step 1: Scans the Skyrim Data directory for mod files if not already scanned.
/// </summary>
public sealed class ScanStep : IPackStep
{
    private readonly IModScanner _scanner;
    private readonly ILogger<ScanStep>? _logger;

    public string Name => "Scan Files";
    public int Order => 100;

    public ScanStep(IModScanner scanner, ILogger<ScanStep>? logger = null)
    {
        _scanner = scanner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // If files are already scanned (e.g. from preview UI), reuse them
        if (context.ScannedFiles.Count > 0 && context.FileSelection == null)
        {
            _logger?.LogInformation("Using {Count} files previously scanned.", context.ScannedFiles.Count);
            return true;
        }

        var dataPath = context.Settings.SkyrimDataPath;
        _logger?.LogInformation("Scanning '{DataPath}' for mod '{ModName}'...", dataPath, context.Profile.Name);

        var scanResult = await _scanner.ScanAsync(dataPath, context.Profile, cancellationToken);

        if (scanResult.IsFailure)
        {
            _logger?.LogError("Scan failed: {Error}", scanResult.Error);
            context.Errors.Add(scanResult.Error);
            return false;
        }

        if (scanResult.Value.Count == 0)
        {
            var msg = $"No matching files found for mod '{context.Profile.Name}' in '{dataPath}'.";
            _logger?.LogWarning("{Message}", msg);
            context.Errors.Add(msg);
            return false;
        }

        if (context.FileSelection != null)
        {
            var selectedFiles = context.FileSelection.Where(file => file.IsSelected).ToLookup(
                file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            var availableFiles = scanResult.Value.ToLookup(
                file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

            foreach (var file in context.FileSelection.Where(file => file.IsSelected))
            {
                if (!availableFiles[file.RelativePath.Replace('\\', '/')].Any(candidate => SameSource(candidate, file)))
                {
                    var msg = $"Previously selected file is no longer available: '{file.RelativePath}'. Scan again before packing.";
                    _logger?.LogError("{Message}", msg);
                    context.Errors.Add(msg);
                    return false;
                }
            }

            foreach (var file in scanResult.Value)
                file.IsSelected = selectedFiles[file.RelativePath.Replace('\\', '/')].Any(selected => SameSource(selected, file));
        }

        context.ScannedFiles = scanResult.Value;
        _logger?.LogInformation("Discovered {Count} files for packing.", context.ScannedFiles.Count);
        return true;
    }

    private static bool SameSource(ScannedFile first, ScannedFile second) =>
        Path.GetFullPath(first.AbsolutePath).Equals(Path.GetFullPath(second.AbsolutePath), StringComparison.OrdinalIgnoreCase);
}
