namespace SkyrimModPacker.Core.Models.PluginBuilder;

public enum SmartWeaponSubtype { Sword, Dagger, WarAxe, Mace, Greatsword, Battleaxe, Warhammer }
public enum SmartBalanceMode { Custom, Weaker, Equal, Stronger, MuchWeaker, MuchStronger }
public enum SmartHandlingMode { Standard, Light, Heavy, Fast, Slow }
public enum SmartRecipeIntent { Unspecified, Enabled, Disabled }
public sealed record SmartWeaponStats(ushort Damage, float Weight, uint Value, float Speed, float Reach)
{
    public void Validate()
    {
        if (Damage == 0 || !float.IsFinite(Weight) || Weight < 0 || !float.IsFinite(Speed) || Speed <= 0 || !float.IsFinite(Reach) || Reach <= 0)
            throw new InvalidDataException("Invalid weapon reference statistics.");
    }
}
public sealed record SmartPresetRecipe(PluginRecordReference Source, CraftingRecipe Recipe);
public sealed record SmartMaterialPreset(string Id, string Material, SmartWeaponSubtype Subtype, WeaponKind Kind,
    PluginRecordReference? Reference, string EditorId, SmartWeaponStats? Stats, IReadOnlyList<PluginRecordReference> MaterialKeywords,
    SmartPresetRecipe? Crafting, SmartPresetRecipe? Tempering, string SourceFingerprint, string? Error)
{
    public int Version => 1;
    public bool Available => Reference != null && Stats != null && Error == null;
}
public sealed record SmartWeaponTuning
{
    public int Version { get; init; } = 1;
    public string? PresetId { get; init; }
    public int PresetVersion { get; init; } = 1;
    public PluginRecordReference? Reference { get; init; }
    public string ReferenceEditorId { get; init; } = "";
    public string SourceFingerprint { get; init; } = "";
    public SmartWeaponStats? Base { get; init; }
    public SmartBalanceMode Mode { get; init; } = SmartBalanceMode.Equal;
    public SmartHandlingMode Handling { get; init; }
    public IReadOnlyList<string> ManualFields { get; init; } = [];
    public SmartRecipeIntent CraftingIntent { get; init; }
    public SmartRecipeIntent TemperingIntent { get; init; }
    public IReadOnlyList<uint> PendingRecipeRemovals { get; init; } = [];
}

public static class SmartWeaponBalance
{
    public static readonly string[] Fields = ["Damage", "Weight", "Value", "Speed", "Reach"];
    public static SmartWeaponStats Calculate(SmartWeaponStats reference, SmartBalanceMode mode, SmartHandlingMode handling = SmartHandlingMode.Standard)
    {
        reference.Validate();
        var factor = mode switch { SmartBalanceMode.MuchWeaker => .7m, SmartBalanceMode.Weaker => .85m, SmartBalanceMode.Equal => 1m, SmartBalanceMode.Stronger => 1.15m, SmartBalanceMode.MuchStronger => 1.3m, _ => throw new InvalidOperationException("Choose an automatic balance mode.") };
        var damage = Math.Max(1, decimal.Round(reference.Damage * factor, 0, MidpointRounding.AwayFromZero));
        var value = decimal.Round(reference.Value * factor, 0, MidpointRounding.AwayFromZero);
        if (damage > ushort.MaxValue || value > uint.MaxValue) throw new InvalidDataException("Balanced statistics exceed the record format. Adjust them manually.");
        float Scale(float value, double multiplier)
        {
            var result = (float)Math.Round(value * multiplier, 3, MidpointRounding.AwayFromZero);
            if (!float.IsFinite(result) || value > 0 && result <= 0) throw new InvalidDataException("Handling statistics exceed the record format or round to zero.");
            return result;
        }
        if (!Enum.IsDefined(handling)) throw new InvalidDataException("Unknown handling profile.");
        return reference with { Damage = (ushort)damage, Value = (uint)value,
            Weight = handling switch { SmartHandlingMode.Light => Scale(reference.Weight, .8), SmartHandlingMode.Heavy => Scale(reference.Weight, 1.2), _ => reference.Weight },
            Speed = handling switch { SmartHandlingMode.Fast => Scale(reference.Speed, 1.15), SmartHandlingMode.Slow => Scale(reference.Speed, .85), _ => reference.Speed } };
    }
}
