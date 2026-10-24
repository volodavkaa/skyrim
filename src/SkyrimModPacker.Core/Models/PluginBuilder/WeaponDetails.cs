namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Editable fields beyond the basic sword statistics; null request details preserve existing fields.</summary>
public sealed class WeaponDetails
{
    private string _description = string.Empty, _modelPath = string.Empty;
    private float _speed = 1, _reach = 1;
    private PluginRecordReference? _enchantment;
    private ushort _enchantmentCharge;
    public string Description { get => _description; init { _description = value; DescriptionSpecified = true; } }
    public bool DescriptionSpecified { get; private set; }
    public float Speed { get => _speed; init { _speed = value; SpeedSpecified = true; } }
    public bool SpeedSpecified { get; private set; }
    public float Reach { get => _reach; init { _reach = value; ReachSpecified = true; } }
    public bool ReachSpecified { get; private set; }
    public float RangeMin { get; init; }
    public float RangeMax { get; init; }
    public float Stagger { get; init; }
    public string ModelPath { get => _modelPath; init { _modelPath = value; ModelPathSpecified = true; } }
    public bool ModelPathSpecified { get; private set; }
    // Null preserves the current WNAM; an explicit reference restores a vanilla STAT.
    public PluginRecordReference? FirstPersonModel { get; init; }
    // Null preserves WNAM, including when older clients edit other weapon fields.
    public string? FirstPersonModelPath { get; init; }
    public bool ClearFirstPersonModel { get; init; }
    private IReadOnlyList<PluginRecordReference> _keywords = [];
    private PluginRecordReference? _attackSound;
    // Omitted fields preserve the cloned/current record. Explicit [] / null clears them.
    public IReadOnlyList<PluginRecordReference> Keywords { get => _keywords; init { _keywords = value; KeywordsSpecified = true; } }
    public bool KeywordsSpecified { get; private set; }
    public PluginRecordReference? AttackSound { get => _attackSound; init { _attackSound = value; AttackSoundSpecified = true; } }
    public bool AttackSoundSpecified { get; private set; }
    // Null preserves other WEAP sound slots; supplied entries are explicit overrides.
    public IReadOnlyDictionary<string, PluginRecordReference?>? Sounds { get; init; }
    public PluginRecordReference? Enchantment { get => _enchantment; init { _enchantment = value; EnchantmentSpecified = true; } }
    public bool EnchantmentSpecified { get; private set; }
    public ushort EnchantmentCharge { get => _enchantmentCharge; init { _enchantmentCharge = value; EnchantmentChargeSpecified = true; } }
    public bool EnchantmentChargeSpecified { get; private set; }
}
