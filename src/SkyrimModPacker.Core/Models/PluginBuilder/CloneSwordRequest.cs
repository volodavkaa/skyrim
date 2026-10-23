namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Clones a vanilla sword into a workspace plugin. Null statistics preserve the template values.</summary>
public sealed class CloneSwordRequest
{
    public required string PluginPath { get; init; }
    public required string GameDataPath { get; init; }
    public uint TemplateFormId { get; init; }
    public string TemplatePluginName { get; init; } = "Skyrim.esm";
    public uint? VariantParentFormId { get; init; }
    public string VariantLabel { get; init; } = "";
    public string VariantMaterialNotes { get; init; } = "";
    public required string EditorId { get; init; }
    public required string Name { get; init; }
    public ushort? Damage { get; init; }
    public float? Weight { get; init; }
    public uint? Value { get; init; }
    public WeaponDetails? Details { get; init; }
    public string? ProjectFilePath { get; init; }
}
