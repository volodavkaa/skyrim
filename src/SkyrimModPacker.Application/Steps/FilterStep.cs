using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;

namespace SkyrimModPacker.Application.Steps;

/// <summary>
/// Step 2: Filters discovered files to include only those selected by the user.
/// </summary>
public sealed class FilterStep : IPackStep
{
    private readonly ILogger<FilterStep>? _logger;

    public string Name => "Filter Selected Files";
    public int Order => 200;

    public FilterStep(ILogger<FilterStep>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        context.SelectedFiles = context.ScannedFiles
            .Where(f => f.IsSelected)
            .ToList();

        // The project scanner returns its plugin and all registered resources.
        // NIF texture links are not parsed, so none of these declared files may be omitted.
        if (!string.IsNullOrWhiteSpace(context.Profile.WeaponProjectPath))
        {
            var missingRequiredFiles = false;
            foreach (var file in context.ScannedFiles.Where(f => !f.IsSelected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var msg = $"Required project file is not selected: '{file.RelativePath}'. Scan again and select all project files before packing.";
                _logger?.LogError("{Message}", msg);
                context.Errors.Add(msg);
                missingRequiredFiles = true;
            }

            if (missingRequiredFiles)
                return Task.FromResult(false);
        }

        if (context.SelectedFiles.Count == 0)
        {
            const string msg = "No files selected for packing. Please check at least one file.";
            _logger?.LogWarning("{Message}", msg);
            context.Errors.Add(msg);
            return Task.FromResult(false);
        }

        _logger?.LogInformation("Filtered {SelectedCount} of {TotalCount} files for packing.",
            context.SelectedFiles.Count, context.ScannedFiles.Count);

        return Task.FromResult(true);
    }
}
