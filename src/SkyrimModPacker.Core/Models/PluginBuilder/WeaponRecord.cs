namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Identity and basic statistics of a weapon saved in the output plugin.</summary>
public sealed class WeaponRecord
{
    public WeaponKind Kind { get; init; } = WeaponKind.OneHandSword;
    public uint FormId { get; init; }
    public string EditorId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName => $"{Name} — {EditorId} [{FormId:X6}]";
    public ushort Damage { get; init; }
    public float Weight { get; init; }
    public uint Value { get; init; }
    public WeaponDetails Details { get; init; } = new();
}
