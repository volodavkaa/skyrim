using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
namespace SkyrimModPacker.Infrastructure.Persistence;
public sealed class JsonSmartRecipePresetRepository : ISmartRecipePresetRepository
{
    private readonly AtomicJsonFile<List<SmartRecipePreset>> _file;
    public JsonSmartRecipePresetRepository(string directory)
    {
        directory = Path.GetFullPath(directory); PackingPathGuard.EnsureNoLinks(directory); Directory.CreateDirectory(directory);
        _file = new(Path.Combine(directory, "smart-recipe-presets.json"), () => [], new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
    public async Task<List<SmartRecipePreset>> LoadAsync(CancellationToken token = default)
    { var result = await _file.LoadAsync(token: token); foreach (var p in result) p.Validate(); return result; }
    public Task SaveAsync(SmartRecipePreset preset, CancellationToken token = default)
    {
        preset.Validate();
        return _file.UpdateAsync(values =>
        {
            if (values.Count >= 1000 && values.All(p => p.Id != preset.Id)) throw new IOException("Recipe preset limit: 1000.");
            if (values.Any(p => p.Id != preset.Id && p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase))) throw new IOException("A recipe preset with this name already exists / Така назва шаблону вже існує.");
            values.RemoveAll(p => p.Id == preset.Id); values.Add(preset); return values;
        }, token);
    }
    public Task DeleteAsync(Guid id, CancellationToken token = default) => _file.UpdateAsync(values => { values.RemoveAll(p => p.Id == id); return values; }, token);
}
