using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Infrastructure.Logging;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure;

/// <summary>
/// Registers Infrastructure layer services into the DI container.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        string appDataDirectory,
        InMemoryLogSink? inMemoryLogSink = null)
    {
        services.AddSingleton<IProfileRepository>(new JsonProfileRepository(appDataDirectory));
        services.AddSingleton<ISettingsRepository>(new JsonSettingsRepository(appDataDirectory));
        services.AddSingleton<IBuildHistoryRepository>(new JsonBuildHistoryRepository(appDataDirectory));
        services.AddSingleton<FileSystem.ModScanner>();
        services.AddSingleton<IModScanner, FileSystem.ProfileModScanner>();
        services.AddSingleton<ISkyrimLocator, Services.SkyrimLocator>();
        services.AddSingleton<IArchiver, Archivers.ZipArchiver>();
        services.AddSingleton<IEslService, Esl.EslService>();
        services.AddSingleton<IPluginBuilderService, PluginBuilder.PluginBuilderService>();
        services.AddSingleton<IWeaponProjectService, PluginBuilder.WeaponProjectService>();

        var logSink = inMemoryLogSink ?? new InMemoryLogSink();
        services.AddSingleton<ILogEventService>(logSink);
        services.AddSingleton(logSink);

        return services;
    }
}
