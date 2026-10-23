namespace SkyrimModPacker.Core.Models.PluginBuilder;

// Values match Skyrim WEAP animation types. Staff and unarmed are intentionally excluded.
public enum WeaponKind { OneHandSword = 1, Dagger = 2, WarAxe = 3, Mace = 4, Greatsword = 5, BattleaxeWarhammer = 6, Bow = 7, Crossbow = 9 }
public sealed record WeaponTypeChoice(WeaponKind Kind, string Name);
