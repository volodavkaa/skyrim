using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Esl;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ModuleInfrastructureTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "ModuleInfrastructureTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void Services_ResolveNewModules_AndPreservePackingSteps()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddInfrastructureServices(_testRoot);
        services.AddTransient<WeaponsViewModel>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.IsType<EslService>(provider.GetRequiredService<IEslService>());
        Assert.IsType<PluginBuilderService>(provider.GetRequiredService<IPluginBuilderService>());
        Assert.IsType<WeaponProjectService>(provider.GetRequiredService<IWeaponProjectService>());
        Assert.NotNull(provider.GetRequiredService<WeaponsViewModel>());
        Assert.NotNull(provider.GetRequiredService<PackingPipeline>());
        Assert.Equal(new[] { typeof(ScanStep), typeof(FilterStep), typeof(ValidateStep), typeof(CopyStep), typeof(EslFlagStep), typeof(ArchiveStep) },
            provider.GetServices<IPackStep>().OrderBy(s => s.Order).Select(s => s.GetType()));
    }

    [Fact]
    public async Task InvalidBuilderRequest_AndInvalidAnalysis_DoNotChangeFiles()
    {
        Directory.CreateDirectory(_testRoot);
        var sourcePath = Path.Combine(_testRoot, "Source.esp");
        await File.WriteAllTextAsync(sourcePath, "unchanged-plugin");
        var outputDirectory = Path.Combine(_testRoot, "output");

        var analysis = await new EslService().AnalyzeAsync(sourcePath);
        var creation = await new PluginBuilderService().CreateAsync(new CreatePluginRequest
        {
            PluginFileName = "Sword.esp",
            OutputDirectory = outputDirectory,
            Masters = ["Skyrim.esm"]
        });

        Assert.True(analysis.IsFailure);
        Assert.True(creation.IsFailure);
        Assert.Equal("unchanged-plugin", await File.ReadAllTextAsync(sourcePath));
        Assert.False(Directory.Exists(outputDirectory));
        Assert.Single(Directory.GetFiles(_testRoot));
    }
}
