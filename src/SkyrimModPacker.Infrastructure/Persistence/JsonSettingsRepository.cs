using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>
/// JSON-file-based implementation of <see cref="ISettingsRepository"/>.
/// </summary>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AtomicJsonFile<AppSettings> _file;

    public JsonSettingsRepository(string appDataDirectory, ILogger<JsonSettingsRepository>? logger = null)
    {
        appDataDirectory = Path.GetFullPath(appDataDirectory);
        PackingPathGuard.EnsureNoLinks(appDataDirectory);
        Directory.CreateDirectory(appDataDirectory);
        _file = new AtomicJsonFile<AppSettings>(Path.Combine(appDataDirectory, "settings.json"), CreateDefaults, JsonOptions, logger);
    }

    /// <inheritdoc />
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        _file.LoadAsync(createIfMissing: true, token: cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _file.UpdateAsync(_ => settings, cancellationToken);

    private static AppSettings CreateDefaults()
    {
        return new AppSettings
        {
            OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Theme = "Dark",
            Language = "en"
        };
    }
}
