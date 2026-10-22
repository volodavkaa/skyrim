using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// Manages build history records.
/// </summary>
public interface IBuildHistoryRepository
{
    /// <summary>Loads all build records.</summary>
    Task<List<BuildRecord>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a build record.</summary>
    Task AddAsync(BuildRecord record, CancellationToken cancellationToken = default);

    /// <summary>Clears all build history.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
