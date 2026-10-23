using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Interfaces;

namespace SkyrimModPacker.Application;

/// <summary>
/// Registers Application layer services and pipeline steps into the DI container.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Pipeline steps executed in order (Scan -> Filter -> Validate -> Copy -> optional ESL -> Archive)
        services.AddSingleton<IPackStep, ScanStep>();
        services.AddSingleton<IPackStep, FilterStep>();
        services.AddSingleton<IPackStep, ValidateStep>();
        services.AddSingleton<IPackStep, PluginValidationStep>();
        services.AddSingleton<IPackStep, CopyStep>();
        services.AddSingleton<IPackStep, EslFlagStep>();
        services.AddSingleton<IPackStep, ArchiveStep>();

        // Pipeline orchestrator
        services.AddSingleton<PackingPipeline>();

        return services;
    }
}
