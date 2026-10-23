using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

public interface IWeaponProjectService
{
    Task<Result<WeaponProject>> CreateAsync(CreatePluginRequest request, CancellationToken cancellationToken = default);
    Task<Result<WeaponProject>> OpenAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<WeaponProject>> ImportAsync(string sourcePluginPath, string outputDirectory, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<string>> ImportModelAsync(string projectFilePath, string sourcePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> ImportTexturesAsync(string projectFilePath, IReadOnlyList<string> sourcePaths, string gameDataPath, string targetSubdirectory = "", CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> GetResourcesAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> ValidateResourcesAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<string>> ValidateModelAsync(string projectFilePath, string modelPath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<List<ScannedFile>>> GetPackingFilesAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
}
