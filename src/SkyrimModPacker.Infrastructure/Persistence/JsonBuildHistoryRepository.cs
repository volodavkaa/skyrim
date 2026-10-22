using System.Text.Json;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>
/// JSON-file-based implementation of <see cref="IBuildHistoryRepository"/>.
/// </summary>
public sealed class JsonBuildHistoryRepository : IBuildHistoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public JsonBuildHistoryRepository(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "build_history.json");
    }

    /// <inheritdoc />
    public async Task<List<BuildRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<BuildRecord>>(stream, JsonOptions, cancellationToken)
               ?? [];
    }

    /// <inheritdoc />
    public async Task AddAsync(BuildRecord record, CancellationToken cancellationToken = default)
    {
        var records = await LoadAllAsync(cancellationToken);
        records.Add(record);
        await WriteAllAsync(records, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await WriteAllAsync([], cancellationToken);
    }

    private async Task WriteAllAsync(List<BuildRecord> records, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
    }
}
