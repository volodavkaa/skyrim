namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Editable fields beyond the basic sword statistics; null request details preserve existing fields.</summary>
public sealed class WeaponDetails
{
    public string Description { get; init; } = string.Empty;
    public float Speed { get; init; } = 1;
    public float Reach { get; init; } = 1;
    public string ModelPath { get; init; } = string.Empty;
    // Null preserves the current WNAM; an explicit reference restores a vanilla STAT.
    public PluginRecordReference? FirstPersonModel { get; init; }
    // Null preserves WNAM, including when older clients edit other weapon fields.
    public string? FirstPersonModelPath { get; init; }
    public bool ClearFirstPersonModel { get; init; }
    public IReadOnlyList<PluginRecordReference> Keywords { get; init; } = [];
    public PluginRecordReference? AttackSound { get; init; }
    public PluginRecordReference? Enchantment { get; init; }
    public ushort EnchantmentCharge { get; init; }
}
