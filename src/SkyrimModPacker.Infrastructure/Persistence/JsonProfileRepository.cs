using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>
/// JSON-file-based implementation of <see cref="IProfileRepository"/>.
/// </summary>
public sealed class JsonProfileRepository : IProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AtomicJsonFile<List<ModProfile>> _file;

    public JsonProfileRepository(string appDataDirectory, ILogger<JsonProfileRepository>? logger = null)
    {
        appDataDirectory = Path.GetFullPath(appDataDirectory);
        PackingPathGuard.EnsureNoLinks(appDataDirectory);
        Directory.CreateDirectory(appDataDirectory);
        _file = new AtomicJsonFile<List<ModProfile>>(Path.Combine(appDataDirectory, "profiles.json"), () => [], JsonOptions, logger);
    }

    /// <inheritdoc />
    public Task<List<ModProfile>> LoadAllAsync(CancellationToken cancellationToken = default) =>
        _file.LoadAsync(token: cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) =>
        _file.UpdateAsync(profiles =>
        {
            var existing = profiles.FindIndex(p => p.Id == profile.Id);
            if (existing >= 0)
            {
                profile.ModifiedAt = DateTime.UtcNow;
                profiles[existing] = profile;
            }
            else
            {
                profiles.Add(profile);
            }

            return profiles;
        }, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _file.UpdateAsync(profiles => { profiles.RemoveAll(p => p.Id == profileId); return profiles; }, cancellationToken);
}
