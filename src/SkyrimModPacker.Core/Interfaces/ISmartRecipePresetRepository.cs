using SkyrimModPacker.Core.Models.PluginBuilder;
namespace SkyrimModPacker.Core.Interfaces;
public interface ISmartRecipePresetRepository
{
    Task<List<SmartRecipePreset>> LoadAsync(CancellationToken token = default);
    Task SaveAsync(SmartRecipePreset preset, CancellationToken token = default);
    Task DeleteAsync(Guid id, CancellationToken token = default);
}
