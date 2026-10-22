using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// Manages persistence of mod profiles (save/load/delete) in JSON format.
/// </summary>
public interface IProfileRepository
{
    /// <summary>Loads all saved profiles.</summary>
    Task<List<ModProfile>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves a profile (creates or updates).</summary>
    Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Deletes a profile by its ID.</summary>
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}
