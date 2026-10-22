using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// Service for automatically detecting the Skyrim installation and Data directory
/// via Windows Registry, Steam library folders, GOG, and default paths.
/// </summary>
public interface ISkyrimLocator
{
    /// <summary>
    /// Attempts to locate the Skyrim Data directory.
    /// </summary>
    /// <returns>A Result containing the detected Data directory path, or an error if not found.</returns>
    Result<string> LocateSkyrimDataPath();
}
