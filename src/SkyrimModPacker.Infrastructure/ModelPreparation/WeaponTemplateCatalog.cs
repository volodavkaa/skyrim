namespace SkyrimModPacker.Infrastructure.ModelPreparation;

public sealed record WeaponTemplateView(string Source, string WeaponMesh,
    string BloodEffect, string BloodLighting);

public sealed record WeaponTemplateDefinition(string Id, string DisplayName,
    WeaponTemplateView ThirdPerson, WeaponTemplateView FirstPerson,
    bool ExportEnabled, bool SupportsScabbard = true, bool ConvexCollision = false,
    int CollisionBoxCount = 3, string BloodMode = "blade", bool GripFromCollision = true,
    string ScabbardFit = "blade");

/// <summary>Verified against the installed Skyrim SE mesh archives; export is enabled only after a type-specific trial.</summary>
public static class WeaponTemplateCatalog
{
    public static IReadOnlyList<WeaponTemplateDefinition> All { get; } =
    [
        new("sword1h", "Одноручний меч",
            new(@"meshes\weapons\iron\longsword.nif", "IronLongSword01:0", "BloodEffects", "BloodLighting"),
            new(@"meshes\weapons\iron\1stpersonlongsword.nif", "IronLongSword:0", "BloodEffects", "BloodLighting"), true),
        new("dagger", "Кинджал",
            new(@"meshes\weapons\iron\irondagger.nif", "IronDagger01:0", "DaggerBloodFX", "DaggerBloodLighting01"),
            new(@"meshes\weapons\iron\1stpersonirondagger.nif", "IronDagger:0", "DaggerBloodFX", "DaggerBloodLighting"), true),
        new("sword2h", "Дворучний меч",
            new(@"meshes\weapons\steel\steelgreatsword.nif", "SteelGreatsword:0", "EdgeBlood", "EdgeBlood01"),
            new(@"meshes\weapons\steel\1stpersonsteelgreatsword.nif", "1stPersonSteelGreatsword:0", "Edgeblood", "Edgeblood01"), true, false, true),
        new("axe1h", "Одноручна сокира",
            new(@"meshes\weapons\iron\waraxe.nif", "WarAxe01:0", "WarAxeBloodFX01", "WarAxeBloodLighting01"),
            new(@"meshes\weapons\iron\1stpersonwaraxe.nif", "WarAxe:0", "WarAxeBloodFX", "WarAxeBloodLighting"), true,
            true, false, 3, "side_head", false, "template_scaled"),
        new("axe2h", "Бойова сокира",
            new(@"meshes\weapons\iron\ironbattleaxe.nif", "BattleAxe01:0", "BloodFX01", "BloodLighting"),
            new(@"meshes\weapons\iron\1stpersonironbattleaxe.nif", "BattleAxe:0", "BattleAxeBloodFX", "BattleAxeBloodLighting"), true,
            false, false, 2, "head", false),
        new("mace", "Булава",
            new(@"meshes\weapons\iron\ironmace.nif", "IronMace01:0", "BloodFX", "BloodLighting"),
            new(@"meshes\weapons\iron\1stpersonironmace.nif", "IronMace:0", "MaceBloodFX", "MaceBloodLighting"), true,
            false, false, 2, "bounded_head", false),
        new("hammer", "Бойовий молот",
            new(@"meshes\weapons\iron\warhammer.nif", "WarHammer02:0", "BloodFX", "BloodLighting"),
            new(@"meshes\weapons\iron\1stpersonwarhammer.nif", "WarHammer:0", "WarhammerBloodFX", "WarhammerBloodLighting"), true,
            false, false, 2, "head", false)
    ];

    public static WeaponTemplateDefinition GetExportable(string id) =>
        All.FirstOrDefault(item => item.Id == id && item.ExportEnabled)
        ?? throw new ArgumentException("Weapon type is not yet supported by the exporter: " + id);
}
