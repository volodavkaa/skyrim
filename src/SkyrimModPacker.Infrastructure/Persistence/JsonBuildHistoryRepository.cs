using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Common;
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

    private readonly AtomicJsonFile<List<BuildRecord>> _file;

    public JsonBuildHistoryRepository(string appDataDirectory, ILogger<JsonBuildHistoryRepository>? logger = null)
    {
        appDataDirectory = Path.GetFullPath(appDataDirectory);
        PackingPathGuard.EnsureNoLinks(appDataDirectory);
        Directory.CreateDirectory(appDataDirectory);
        _file = new AtomicJsonFile<List<BuildRecord>>(Path.Combine(appDataDirectory, "build_history.json"), () => [], JsonOptions, logger);
    }

    /// <inheritdoc />
    public Task<List<BuildRecord>> LoadAllAsync(CancellationToken cancellationToken = default) =>
        _file.LoadAsync(token: cancellationToken);

    /// <inheritdoc />
    public Task AddAsync(BuildRecord record, CancellationToken cancellationToken = default) =>
        _file.UpdateAsync(records => { records.Add(record); return records; }, cancellationToken);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _file.UpdateAsync(_ => [], cancellationToken);
}
