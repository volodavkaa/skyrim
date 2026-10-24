using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Core.Interfaces;

public interface IWeaponPresetRepository
{
    Task<List<WeaponPreset>> LoadAsync(CancellationToken token = default);
    Task SaveAsync(WeaponPreset preset, CancellationToken token = default);
    Task DeleteAsync(Guid id, CancellationToken token = default);
}
