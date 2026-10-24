namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record LeveledListUsage(string Category, string OwnerName, string OwnerEditorId, PluginRecordReference Owner)
{
    public string Kind { get; init; } = "";
    public PluginRecordReference? ViaRecord { get; init; }
}
public sealed record LeveledListEntryInfo(PluginRecordReference? Reference, string Name, string EditorId, short Level, short Count, bool Nested, string? Error = null);
public sealed record LeveledListDetails(PluginRecordReference Reference, string EditorId, string Flags, string ChanceNone,
    IReadOnlyList<LeveledListEntryInfo> Entries)
{
    public uint? SelectionFlags { get; init; }
}
