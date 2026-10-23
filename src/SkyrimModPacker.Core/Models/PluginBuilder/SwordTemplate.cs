namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Vanilla one-handed sword available for cloning from Skyrim.esm.</summary>
public sealed class SwordTemplate
{
    public string PluginName { get; init; } = "Skyrim.esm";
    public WeaponKind Kind { get; init; } = WeaponKind.OneHandSword;
    public string DisplayName => $"{EditorId} [{PluginName}:{FormId:X6}]";
    public uint FormId { get; init; }
    public string EditorId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ushort Damage { get; init; }
    public float Weight { get; init; }
    public uint Value { get; init; }
    public WeaponDetails Details { get; init; } = new();
}
