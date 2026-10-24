using System.Text.Json;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.Persistence;

public sealed class JsonWeaponPresetRepository : IWeaponPresetRepository
{
    private readonly AtomicJsonFile<List<WeaponPreset>> _file;
    public JsonWeaponPresetRepository(string directory)
    {
        directory = Path.GetFullPath(directory);
        PackingPathGuard.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        _file = new(Path.Combine(directory, "weapon-presets.json"), () => [], new JsonSerializerOptions { WriteIndented = true });
    }
    public Task<List<WeaponPreset>> LoadAsync(CancellationToken token = default) => _file.LoadAsync(token: token);
    public Task SaveAsync(WeaponPreset preset, CancellationToken token = default)
    {
        preset.Validate();
        return _file.UpdateAsync(values =>
        {
            if (values.Any(v => v.Id != preset.Id && v.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A preset with this name already exists.");
            values.RemoveAll(v => v.Id == preset.Id);
            values.Add(preset);
            return values;
        }, token);
    }
    public Task DeleteAsync(Guid id, CancellationToken token = default) => _file.UpdateAsync(values => { values.RemoveAll(v => v.Id == id); return values; }, token);
}
