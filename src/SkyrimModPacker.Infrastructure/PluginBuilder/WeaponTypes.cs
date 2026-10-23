using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

internal static class WeaponTypes
{
    // Extended one type at a time, with a separate acceptance matrix for each addition.
    public static bool Supported(WeaponAnimationType? type) => type is WeaponAnimationType.OneHandSword or WeaponAnimationType.TwoHandSword or WeaponAnimationType.OneHandDagger or WeaponAnimationType.OneHandAxe or WeaponAnimationType.OneHandMace or WeaponAnimationType.TwoHandAxe or WeaponAnimationType.Bow or WeaponAnimationType.Crossbow;
    public static readonly string[] VanillaSources = ["Skyrim.esm", "Dawnguard.esm"];
    public static bool IsVanillaSource(string name) => VanillaSources.Contains(name, StringComparer.OrdinalIgnoreCase);
    public static WeaponKind Kind(IWeaponGetter weapon) => (WeaponKind)(int)weapon.Data!.AnimationType;
    public static bool PlayerTemplate(IWeaponGetter weapon) => !weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable) && weapon.Data != null &&
        !weapon.Data.Flags.HasFlag(WeaponData.Flag.NonPlayable) && !weapon.Data.Flags.HasFlag(WeaponData.Flag.BoundWeapon);
}
