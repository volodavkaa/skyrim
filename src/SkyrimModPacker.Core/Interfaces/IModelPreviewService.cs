using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

public interface IModelPreviewService
{
    Task<ModelPreview> LoadAsync(string projectDirectory, string modelPath, string gameDataPath, CancellationToken token = default);
}
