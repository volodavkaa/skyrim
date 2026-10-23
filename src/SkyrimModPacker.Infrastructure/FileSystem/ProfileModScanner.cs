using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.FileSystem;

/// <summary>Routes explicit project profiles to the project source; legacy profiles retain their scanner.</summary>
public sealed class ProfileModScanner(ModScanner legacyScanner, IWeaponProjectService projects) : IModScanner
{
    public async Task<Result<List<ScannedFile>>> ScanAsync(string dataPath, ModProfile profile, CancellationToken cancellationToken = default)
    {
        var validation = ModProfileValidator.Validate(profile);
        if (validation.IsFailure) return Result<List<ScannedFile>>.Failure(validation.Error);
        if (string.IsNullOrWhiteSpace(profile.WeaponProjectPath))
            return await legacyScanner.ScanAsync(dataPath, profile, cancellationToken);
        var result = await projects.GetPackingFilesAsync(profile.WeaponProjectPath.Trim(), dataPath, cancellationToken);
        if (result.IsFailure) return result;
        var plugin = result.Value.Single(f => f.Category == FileCategory.Plugin);
        if (!Path.GetFullPath(profile.PluginSourcePath.Trim()).Equals(Path.GetFullPath(plugin.AbsolutePath), StringComparison.OrdinalIgnoreCase) ||
            !profile.PluginFileName.Trim().Equals(Path.GetFileName(plugin.AbsolutePath), StringComparison.OrdinalIgnoreCase))
            return Result<List<ScannedFile>>.Failure("The profile plugin does not match its weapon project. Relink the profile from Weapons.");
        return result;
    }
}
