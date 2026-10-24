namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Personal parameters, deliberately independent of item identity and workspace assets.</summary>
public sealed record WeaponPreset
{
    public int Version { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public WeaponKind Kind { get; init; }
    public ushort Damage { get; init; }
    public float Weight { get; init; }
    public uint Value { get; init; }
    public float Speed { get; init; } = 1;
    public float Reach { get; init; } = 1;
    public string Description { get; init; } = "";
    public IReadOnlyList<PluginRecordReference> Keywords { get; init; } = [];
    public PluginRecordReference? Sound { get; init; }
    public PluginRecordReference? Enchantment { get; init; }
    public ushort Charge { get; init; }

    public void Validate()
    {
        if (Version != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl) || !Enum.IsDefined(Kind))
            throw new InvalidOperationException("Invalid preset name, version, identity or weapon type.");
        if (!float.IsFinite(Weight) || Weight < 0 || !float.IsFinite(Speed) || Speed <= 0 || !float.IsFinite(Reach) || Reach <= 0 ||
            (Enchantment == null ? Charge != 0 : Charge == 0) || Description.Contains('\0') || Keywords == null || Keywords.Distinct().Count() != Keywords.Count)
            throw new InvalidOperationException("Invalid preset parameters or enchantment charge.");
    }
}
