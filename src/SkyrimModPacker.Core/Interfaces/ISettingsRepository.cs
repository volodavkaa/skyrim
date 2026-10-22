using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// Manages persistence of application settings.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>Loads saved settings, or returns defaults if none exist.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves settings to persistent storage.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
