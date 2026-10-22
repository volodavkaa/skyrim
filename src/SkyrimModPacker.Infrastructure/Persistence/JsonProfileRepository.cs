using System.Text.Json;
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

    private readonly string _filePath;

    public JsonProfileRepository(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "profiles.json");
    }

    /// <inheritdoc />
    public async Task<List<ModProfile>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<ModProfile>>(stream, JsonOptions, cancellationToken)
               ?? [];
    }

    /// <inheritdoc />
    public async Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadAllAsync(cancellationToken);
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

        await WriteAllAsync(profiles, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadAllAsync(cancellationToken);
        profiles.RemoveAll(p => p.Id == profileId);
        await WriteAllAsync(profiles, cancellationToken);
    }

    private async Task WriteAllAsync(List<ModProfile> profiles, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
    }
}
