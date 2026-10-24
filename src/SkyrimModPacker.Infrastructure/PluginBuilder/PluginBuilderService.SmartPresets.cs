using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    public Task<Result<IReadOnlyList<SmartMaterialPreset>>> GetSmartMaterialPresetsAsync(string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<SmartMaterialPreset>>(() =>
        {
            ValidateDataPath(gameDataPath);
            var sources = new List<ISkyrimModDisposableGetter>();
            try
            {
                var fingerprints = new List<string>();
                foreach (var name in new[] { "Skyrim.esm", "Dawnguard.esm" }.Where(n => File.Exists(Path.Combine(gameDataPath, n))))
                {
                    cancellationToken.ThrowIfCancellationRequested(); sources.Add(ReadMaster(gameDataPath, name));
                    using var stream = File.OpenRead(Path.Combine(gameDataPath, name)); fingerprints.Add(name + ":" + Convert.ToHexString(SHA256.HashData(stream)));
                }
                var fingerprint = string.Join(";", fingerprints);
                var keywords = sources.SelectMany(s => s.Keywords).Where(k => !k.IsDeleted).GroupBy(k => k.FormKey).ToDictionary(g => g.Key, g => g.Last());
                var items = sources.SelectMany(s => s.EnumerateMajorRecords<IItemGetter>()).Where(i => !i.IsDeleted).GroupBy(i => i.FormKey).ToDictionary(g => g.Key, g => g.Last());
                var perks = sources.SelectMany(s => s.Perks).Where(p => !p.IsDeleted).GroupBy(p => p.FormKey).ToDictionary(g => g.Key, g => g.Last());
                var records = sources.SelectMany(s => s.ConstructibleObjects).GroupBy(r => r.FormKey).Select(g => g.Last()).Where(r => !r.IsDeleted).ToLookup(r => r.CreatedObject.FormKey);
                var result = new List<SmartMaterialPreset>();
                foreach (var (material, prefix, master, ids, materialKeyword) in new[]
                {
                    ("Steel", "Steel", "Skyrim.esm", new uint[] {0x13989,0x13986,0x13983,0x13988,0x13987,0x13984,0x1398A}, "WeapMaterialSteel"),
                    ("Ebony", "Ebony", "Skyrim.esm", new uint[] {0x139B1,0x139AE,0x139AB,0x139B0,0x139AF,0x139AC,0x139B2}, "WeapMaterialEbony"),
                    ("Dragonbone", "DLC1Dragonbone", "Dawnguard.esm", new uint[] {0x14FCE,0x14FCB,0x14FCF,0x14FCD,0x14FCC,0x14FC3,0x14FD0}, "DLC1WeapMaterialDragonbone")
                    ,("Iron", "Iron", "Skyrim.esm", Array.Empty<uint>(), "WeapMaterialIron")
                    ,("Orcish", "Orcish", "Skyrim.esm", Array.Empty<uint>(), "WeapMaterialOrcish")
                    ,("Dwarven", "Dwarven", "Skyrim.esm", Array.Empty<uint>(), "WeapMaterialDwarven")
                    ,("Elven", "Elven", "Skyrim.esm", Array.Empty<uint>(), "WeapMaterialElven")
                    ,("Glass", "Glass", "Skyrim.esm", Array.Empty<uint>(), "WeapMaterialGlass")
                    ,("Daedric", "Daedric", "Skyrim.esm", Array.Empty<uint>(), "WeapMaterialDaedric")
                })
                foreach (var subtype in Enum.GetValues<SmartWeaponSubtype>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var kind = subtype switch { SmartWeaponSubtype.Sword => WeaponKind.OneHandSword, SmartWeaponSubtype.Dagger => WeaponKind.Dagger,
                        SmartWeaponSubtype.WarAxe => WeaponKind.WarAxe, SmartWeaponSubtype.Mace => WeaponKind.Mace, SmartWeaponSubtype.Greatsword => WeaponKind.Greatsword, _ => WeaponKind.BattleaxeWarhammer };
                    var id = material + ":" + subtype; var editor = prefix + subtype;
                    try
                    {
                        var source = sources.FirstOrDefault(s => s.ModKey.FileName.String == master) ?? throw new InvalidDataException("Required master is missing: " + master);
                        var weapon = source.Weapons.SingleOrDefault(w => (ids.Length == 0 || w.FormKey.ID == ids[(int)subtype]) && w.FormKey.ModKey == source.ModKey && w.EditorID == editor && !w.IsDeleted && w.ObjectEffect.IsNull)
                            ?? throw new InvalidDataException("Verified vanilla weapon is missing: " + editor);
                        var key = weapon.FormKey;
                        var expectedType = subtype switch { SmartWeaponSubtype.Sword => "WeapTypeSword", SmartWeaponSubtype.Dagger => "WeapTypeDagger",
                            SmartWeaponSubtype.WarAxe => "WeapTypeWarAxe", SmartWeaponSubtype.Mace => "WeapTypeMace", SmartWeaponSubtype.Greatsword => "WeapTypeGreatsword",
                            SmartWeaponSubtype.Battleaxe => "WeapTypeBattleaxe", _ => "WeapTypeWarhammer" };
                        var tags = weapon.Keywords?.Select(k => keywords.GetValueOrDefault(k.FormKey)).Where(k => k != null).ToArray() ?? [];
                        if (weapon.Data == null || WeaponTypes.Kind(weapon) != kind) throw new InvalidDataException("Weapon animation type mismatch.");
                        if (!tags.Any(k => k!.EditorID == expectedType)) throw new InvalidDataException("Weapon subtype keyword mismatch: " + editor);
                        var tag = tags.SingleOrDefault(k => k!.EditorID == materialKeyword) ?? throw new InvalidDataException("Verified material keyword is missing.");
                        var stats = new SmartWeaponStats(weapon.BasicStats?.Damage ?? 0, weapon.BasicStats?.Weight ?? -1, weapon.BasicStats?.Value ?? 0, weapon.Data?.Speed ?? 0, weapon.Data?.Reach ?? 0); stats.Validate();
                        SmartPresetRecipe Read(string benchName, bool tempering)
                        {
                            var matches = records[key].Where(r => keywords.GetValueOrDefault(r.WorkbenchKeyword.FormKey)?.EditorID == benchName).ToArray();
                            if (matches.Length != 1) throw new InvalidDataException("Expected one standard recipe for " + editor + " / " + benchName);
                            var recipe = ToRecipe(matches[0]);
                            if (recipe.IsTempering != tempering || recipe.OutputCount != 1 || recipe.Ingredients.Count == 0 || recipe.Ingredients.Any(i => !items.TryGetValue(new FormKey(ModKey.FromNameAndExtension(i.Reference.PluginName), i.Reference.FormId), out var item) || item is ILeveledItemGetter))
                                throw new InvalidDataException("Invalid standard recipe ingredients or output.");
                            foreach (var perk in new[] { recipe.RequiredPerk, recipe.TemperingPerk }.Where(p => p != null))
                                if (!perks.ContainsKey(new FormKey(ModKey.FromNameAndExtension(perk!.PluginName), perk.FormId))) throw new InvalidDataException("Recipe perk is missing.");
                            if (tempering && (recipe.TemperingPerk == null || perks[new FormKey(ModKey.FromNameAndExtension(recipe.TemperingPerk.PluginName), recipe.TemperingPerk.FormId)].EditorID != "ArcaneBlacksmith"))
                                throw new InvalidDataException("Unsupported tempering guard.");
                            return new(Reference(matches[0].FormKey)!, recipe);
                        }
                        result.Add(new(id, material, subtype, kind, Reference(key), editor, stats, [Reference(tag!.FormKey)!], Read("CraftingSmithingForge", false), Read("CraftingSmithingSharpeningWheel", true), fingerprint, null));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { result.Add(new(id, material, subtype, kind, null, editor, null, [], null, null, fingerprint, ex.Message)); }
                }
                return result;
            }
            finally { foreach (var source in sources) source.Dispose(); }
        }, cancellationToken);
}
