using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Validates <see cref="ModProfile"/> properties before saving or scanning.
/// </summary>
public static class ModProfileValidator
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Validates the profile and returns a Result indicating whether it is valid.
    /// </summary>
    public static Result Validate(ModProfile profile)
    {
        if (profile == null)
            return Result.Failure("Profile cannot be null.");

        if (string.IsNullOrWhiteSpace(profile.Name))
            return Result.Failure("Mod name cannot be empty.");

        if (profile.Name.IndexOfAny(InvalidFileNameChars) >= 0)
            return Result.Failure("Mod name contains invalid filename characters.");

        if (string.IsNullOrWhiteSpace(profile.Version))
            return Result.Failure("Mod version cannot be empty.");

        if (profile.Version.IndexOfAny(InvalidFileNameChars) >= 0)
            return Result.Failure("Mod version contains invalid filename characters.");

        var hasKeywords = profile.Keywords.Exists(k => !string.IsNullOrWhiteSpace(k));
        var hasPlugin = !string.IsNullOrWhiteSpace(profile.PluginFileName) || !string.IsNullOrWhiteSpace(profile.PluginSourcePath);

        if (!string.IsNullOrWhiteSpace(profile.PluginSourcePath))
        {
            var sourcePath = profile.PluginSourcePath.Trim();
            if (!Path.IsPathFullyQualified(sourcePath))
                return Result.Failure("The workspace plugin path must be absolute.");
            var extension = Path.GetExtension(sourcePath);
            if (!new[] { ".esp", ".esl", ".esm" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return Result.Failure("The workspace plugin must be an ESP, ESM or ESL file.");
            if (!string.IsNullOrWhiteSpace(profile.PluginFileName) &&
                !Path.GetFileName(sourcePath).Equals(profile.PluginFileName.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Failure("The workspace plugin filename must match the profile plugin filename.");
        }

        if (!hasKeywords && !hasPlugin)
            return Result.Failure("Profile must have at least one keyword or a plugin filename.");

        return Result.Success();
    }
}
