namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Plugin-local record identity, independent of load-order indices.</summary>
public sealed record PluginRecordReference(string PluginName, uint FormId)
{
    public override string ToString() => $"{PluginName}:{FormId:X6}";
}

public sealed class RecordOption
{
    public required PluginRecordReference Reference { get; init; }
    public string EditorId { get; init; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(EditorId) ? Reference.ToString() : EditorId;
}
