namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Parameters for a new plugin; the filename extension specifies ESP or ESL.</summary>
public sealed class CreatePluginRequest
{
    public required string PluginFileName { get; init; }
    public required string OutputDirectory { get; init; }
    public string GameDataPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Masters { get; init; } = [];
}
