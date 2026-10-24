using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    private static IReadOnlyDictionary<string, PluginRecordReference?> WeaponSounds(IWeaponGetter weapon) =>
        new Dictionary<string, PluginRecordReference?>
        {
            ["AttackSound"] = Reference(weapon.AttackSound.FormKey),
            ["AttackSound2D"] = Reference(weapon.AttackSound2D.FormKey),
            ["AttackLoopSound"] = Reference(weapon.AttackLoopSound.FormKey),
            ["AttackFailSound"] = Reference(weapon.AttackFailSound.FormKey),
            ["IdleSound"] = Reference(weapon.IdleSound.FormKey),
            ["EquipSound"] = Reference(weapon.EquipSound.FormKey),
            ["UnequipSound"] = Reference(weapon.UnequipSound.FormKey),
            ["PickUpSound"] = Reference(weapon.PickUpSound.FormKey),
            ["PutDownSound"] = Reference(weapon.PutDownSound.FormKey)
        };

    private static void SetWeaponSound(IWeapon weapon, string slot, PluginRecordReference? value)
    {
        var link = slot switch
        {
            "AttackSound" => weapon.AttackSound, "AttackSound2D" => weapon.AttackSound2D,
            "AttackLoopSound" => weapon.AttackLoopSound, "AttackFailSound" => weapon.AttackFailSound,
            "IdleSound" => weapon.IdleSound, "EquipSound" => weapon.EquipSound,
            "UnequipSound" => weapon.UnequipSound, "PickUpSound" => weapon.PickUpSound,
            "PutDownSound" => weapon.PutDownSound,
            _ => throw new InvalidOperationException("Unsupported weapon sound slot: " + slot)
        };
        link.SetTo(value == null ? (FormKey?)null : new FormKey(ModKey.FromNameAndExtension(value.PluginName), value.FormId));
    }
}
