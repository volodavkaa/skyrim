namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>A portable workspace; plugin and resources live beside the project file.</summary>
public sealed class WeaponProject
{
    public required string Name { get; init; }
    public required string ProjectFilePath { get; init; }
    public required string PluginPath { get; init; }
}
