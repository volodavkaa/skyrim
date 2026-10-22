using System.Text.Json;
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

    private readonly string _filePath;

    public JsonSettingsRepository(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "settings.json");
    }

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            var defaults = CreateDefaults();
            await SaveAsync(defaults, cancellationToken);
            return defaults;
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
               ?? CreateDefaults();
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

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
