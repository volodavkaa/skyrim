using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

public interface IWeaponProjectService
{
    Task<Result<IReadOnlyList<WeaponVariant>>> GetVariantsAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<PluginRecordReference?>> GetTemplateReferenceAsync(string projectFilePath, string gameDataPath, uint weaponFormId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<ResourceVersion>>> GetResourceVersionsAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<ResourceVersion>> BackupResourceAsync(string projectFilePath, string relativePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<string>> ImportModelVersionCopyAsync(string projectFilePath, Guid versionId, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<string>> ReplaceResourceAsync(string projectFilePath, string relativePath, string sourcePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<string>> RestoreResourceVersionAsync(string projectFilePath, Guid versionId, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<string>> DeleteResourceAsync(string projectFilePath, string relativePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<string>> ImportModelCopyAsync(string projectFilePath, string sourcePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<ProjectResourceCatalog>> GetResourceCatalogAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<uint?>> GetTemplateOriginAsync(string projectFilePath, string gameDataPath, uint weaponFormId, CancellationToken cancellationToken = default);
    Task<Result<WeaponModels?>> GetOriginalModelsAsync(string projectFilePath, string gameDataPath, uint weaponFormId, CancellationToken cancellationToken = default);
    Task<Result<ModelResourceInfo>> InspectModelAsync(string projectFilePath, string modelPath, string gameDataPath, bool vanilla, CancellationToken cancellationToken = default);
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
