using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.AddSingleton<IProfileRepository>(provider => new JsonProfileRepository(appDataDirectory, provider.GetService<ILogger<JsonProfileRepository>>()));
        services.AddSingleton<ISettingsRepository>(provider => new JsonSettingsRepository(appDataDirectory, provider.GetService<ILogger<JsonSettingsRepository>>()));
        services.AddSingleton<IBuildHistoryRepository>(provider => new JsonBuildHistoryRepository(appDataDirectory, provider.GetService<ILogger<JsonBuildHistoryRepository>>()));
        services.AddSingleton<FileSystem.ModScanner>();
        services.AddSingleton<IModScanner, FileSystem.ProfileModScanner>();
        services.AddSingleton<ISkyrimLocator, Services.SkyrimLocator>();
        services.AddSingleton<IArchiver, Archivers.ZipArchiver>();
        services.AddSingleton<IReportedArchiveService, Archivers.ReportedArchiveService>();
        services.AddSingleton<IEslService, Esl.EslService>();
        services.AddSingleton<IPluginValidationService, PluginBuilder.PluginValidationService>();
        services.AddSingleton<IPluginBuilderService, PluginBuilder.PluginBuilderService>();
        services.AddSingleton<IWeaponProjectService, PluginBuilder.WeaponProjectService>();
        services.AddSingleton<IModelPreviewService, Preview.ModelPreviewService>();
        services.AddSingleton<IWeaponDraftRepository>(_ => new JsonWeaponDraftRepository(appDataDirectory));
        services.AddSingleton<IWeaponPresetRepository>(_ => new JsonWeaponPresetRepository(appDataDirectory));
        services.AddSingleton<IWorkspaceHistoryService>(_ => new WorkspaceHistoryService(appDataDirectory));

        var logSink = inMemoryLogSink ?? new InMemoryLogSink();
        services.AddSingleton<ILogEventService>(logSink);
        services.AddSingleton(logSink);

        return services;
    }
}
