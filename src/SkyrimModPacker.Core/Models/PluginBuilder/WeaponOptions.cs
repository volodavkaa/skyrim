namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Vanilla records available in the editor's reference pickers.</summary>
public sealed class WeaponOptions
{
    public IReadOnlyList<RecordOption> Keywords { get; init; } = [];
    public IReadOnlyList<RecordOption> Sounds { get; init; } = [];
    public IReadOnlyList<RecordOption> Enchantments { get; init; } = [];
    public IReadOnlyList<RecordOption> Ingredients { get; init; } = [];
    public IReadOnlyList<RecordOption> Perks { get; init; } = [];
    public IReadOnlyList<RecordOption> LeveledLists { get; init; } = [];
    public IReadOnlyList<RecordOption> ExistingEditorIds { get; init; } = [];
}
