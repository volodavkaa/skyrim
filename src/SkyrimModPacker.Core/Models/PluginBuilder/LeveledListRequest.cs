namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Adds or removes an owned sword from an existing leveled item list.</summary>
public sealed class LeveledListRequest
{
    public required string PluginPath { get; init; }
    public required string GameDataPath { get; init; }
    public uint WeaponFormId { get; init; }
    public required PluginRecordReference List { get; init; }
    public short Level { get; init; } = 1;
    public short Count { get; init; } = 1;
    public bool UpdateExisting { get; init; }
}
