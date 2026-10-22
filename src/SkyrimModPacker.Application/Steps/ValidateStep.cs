using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Application.Steps;

/// <summary>
/// Step 3: Validates profile, output path availability, and ensures all source files exist on disk.
/// </summary>
public sealed class ValidateStep : IPackStep
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private readonly IArchiver _archiver;
    private readonly ILogger<ValidateStep>? _logger;

    public string Name => "Validate";
    public int Order => 300;

    public ValidateStep(IArchiver archiver, ILogger<ValidateStep>? logger = null)
    {
        _archiver = archiver;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate profile
        var profileResult = ModProfileValidator.Validate(context.Profile);
        if (profileResult.IsFailure)
        {
            _logger?.LogError("Profile validation failed: {Error}", profileResult.Error);
            context.Errors.Add(profileResult.Error);
            return Task.FromResult(false);
        }

        // 2. Resolve and ensure output directory
        var outputDir = string.IsNullOrWhiteSpace(context.Settings.OutputDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : context.Settings.OutputDirectory.Trim();

        try
        {
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
        }
        catch (Exception ex)
        {
            var msg = $"Cannot access or create output directory '{outputDir}': {ex.Message}";
            _logger?.LogError(ex, "{Message}", msg);
            context.Errors.Add(msg);
            return Task.FromResult(false);
        }

        // 3. Determine archive filename and path
        var safeModName = SanitizeFileName(context.Profile.Name);
        var safeVersion = SanitizeFileName(context.Profile.Version);
        var archiveFileName = $"{safeModName}-{safeVersion}{_archiver.Extension}";
        var outputArchivePath = Path.Combine(outputDir, archiveFileName);

        // 4. Check if archive already exists
        if (File.Exists(outputArchivePath) && !context.Settings.OverwriteExistingArchive)
        {
            var msg = $"Archive already exists: '{outputArchivePath}'. Enable overwrite in settings or update the version.";
            _logger?.LogWarning("{Message}", msg);
            context.Errors.Add(msg);
            return Task.FromResult(false);
        }

        context.OutputArchivePath = outputArchivePath;

        // 5. Verify every selected file exists on disk
        foreach (var file in context.SelectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(file.AbsolutePath))
            {
                var msg = $"Source file not found on disk: '{file.AbsolutePath}'";
                _logger?.LogError("{Message}", msg);
                context.Errors.Add(msg);
                return Task.FromResult(false);
            }
        }

        _logger?.LogInformation("Validation passed. Target archive: '{OutputPath}'", outputArchivePath);
        return Task.FromResult(true);
    }

    private static string SanitizeFileName(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidFileNameChars, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars).Trim();
    }
}
