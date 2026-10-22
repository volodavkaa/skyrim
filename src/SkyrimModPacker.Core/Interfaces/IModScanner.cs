using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// Scans the Skyrim Data directory for files matching mod profile keywords.
/// Preserves relative directory structure and categorizes files.
/// </summary>
public interface IModScanner
{
    /// <summary>
    /// Scans the Data directory for files matching the given profile keywords and plugin name.
    /// </summary>
    /// <param name="dataPath">Path to the Skyrim Data directory.</param>
    /// <param name="profile">Mod profile containing keywords and plugin filename.</param>
    /// <param name="cancellationToken">Token to support cancellation.</param>
    /// <returns>A Result containing the list of discovered files, or an error description.</returns>
    Task<Result<List<ScannedFile>>> ScanAsync(
        string dataPath,
        ModProfile profile,
        CancellationToken cancellationToken = default);
}
