namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Edits an owned sword while keeping its FormID and inherited fields.</summary>
public sealed class UpdateSwordRequest
{
    public required string PluginPath { get; init; }
    public required string GameDataPath { get; init; }
    public uint FormId { get; init; }
    public required string EditorId { get; init; }
    public required string Name { get; init; }
    public ushort Damage { get; init; }
    public float Weight { get; init; }
    public uint Value { get; init; }
    public WeaponDetails? Details { get; init; }
    public string? ProjectFilePath { get; init; }
    public uint? VanillaTemplateFormId { get; init; }
    public string VanillaTemplatePluginName { get; init; } = "Skyrim.esm";
}
