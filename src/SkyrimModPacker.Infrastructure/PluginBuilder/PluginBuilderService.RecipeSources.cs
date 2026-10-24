using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
namespace SkyrimModPacker.Infrastructure.PluginBuilder;
public sealed partial class PluginBuilderService
{
    public Task<Result<IReadOnlyList<SmartRecipeSource>>> GetSmartRecipeSourcesAsync(string gameDataPath, CancellationToken cancellationToken = default) => RunAsync<IReadOnlyList<SmartRecipeSource>>(() =>
    {
        ValidateDataPath(gameDataPath); var sources = new List<ISkyrimModDisposableGetter>();
        try
        {
            var hashes = new List<string>();
            foreach (var name in WeaponTypes.VanillaSources.Where(n => File.Exists(Path.Combine(gameDataPath, n))))
            {
                cancellationToken.ThrowIfCancellationRequested(); sources.Add(ReadMaster(gameDataPath, name));
                using var input = File.OpenRead(Path.Combine(gameDataPath, name)); hashes.Add(name + ":" + Convert.ToHexString(SHA256.HashData(input)));
            }
            var fingerprint = string.Join(";", hashes);
            var keywords = sources.SelectMany(s => s.Keywords).GroupBy(k => k.FormKey).Select(g => g.Last()).ToDictionary(k => k.FormKey, k => k.EditorID);
            var items = sources.SelectMany(s => s.EnumerateMajorRecords<IItemGetter>()).GroupBy(i => i.FormKey).Select(g => g.Last()).Where(i => !i.IsDeleted).ToDictionary(i => i.FormKey);
            var perks = sources.SelectMany(s => s.Perks).GroupBy(p => p.FormKey).Select(g => g.Last()).Where(p => !p.IsDeleted).ToDictionary(p => p.FormKey);
            var weapons = sources.SelectMany(s => s.Weapons).GroupBy(w => w.FormKey).Select(g => g.Last()).Where(w => !w.IsDeleted && w.ObjectEffect.IsNull && WeaponTypes.PlayerTemplate(w) && WeaponTypes.Supported(w.Data?.AnimationType)).ToDictionary(w => w.FormKey);
            var result = new List<SmartRecipeSource>();
            foreach (var record in sources.SelectMany(s => s.ConstructibleObjects).GroupBy(r => r.FormKey).Select(g => g.Last()).Where(r => !r.IsDeleted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!weapons.TryGetValue(record.CreatedObject.FormKey, out var weapon)) continue;
                var bench = keywords.GetValueOrDefault(record.WorkbenchKeyword.FormKey);
                if (bench is not ("CraftingSmithingForge" or "CraftingSmithingSharpeningWheel")) continue;
                try
                {
                    var recipe = ToRecipe(record); SmartRecipeApplication.Validate(recipe);
                    if (recipe.IsTempering != (bench == "CraftingSmithingSharpeningWheel")) continue;
                    Mutagen.Bethesda.Plugins.FormKey Key(PluginRecordReference r) => new(Mutagen.Bethesda.Plugins.ModKey.FromNameAndExtension(r.PluginName), r.FormId);
                    if (recipe.Ingredients.Any(i => !items.TryGetValue(Key(i.Reference), out var ingredient) || ingredient is ILeveledItemGetter || ingredient.FormKey == weapon.FormKey) ||
                        recipe.RequiredPerk != null && !perks.ContainsKey(Key(recipe.RequiredPerk)) ||
                        recipe.IsTempering && (recipe.TemperingPerk == null || !perks.TryGetValue(Key(recipe.TemperingPerk), out var guard) || guard.EditorID != "ArcaneBlacksmith")) continue;
                    var kind = WeaponTypes.Kind(weapon);
                    var tags = weapon.Keywords?.Select(k => keywords.GetValueOrDefault(k.FormKey)).ToHashSet() ?? [];
                    SmartWeaponSubtype? subtype = kind switch { WeaponKind.OneHandSword => SmartWeaponSubtype.Sword, WeaponKind.Dagger => SmartWeaponSubtype.Dagger,
                        WeaponKind.WarAxe => SmartWeaponSubtype.WarAxe, WeaponKind.Mace => SmartWeaponSubtype.Mace, WeaponKind.Greatsword => SmartWeaponSubtype.Greatsword,
                        WeaponKind.BattleaxeWarhammer => tags.Contains("WeapTypeBattleaxe") ? SmartWeaponSubtype.Battleaxe : tags.Contains("WeapTypeWarhammer") ? SmartWeaponSubtype.Warhammer : null, _ => null };
                    result.Add(new(Reference(record.FormKey)!, Reference(weapon.FormKey)!, weapon.EditorID ?? "", weapon.Name?.String ?? weapon.EditorID ?? "", kind, subtype, recipe, fingerprint));
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { /* Advanced conditions are excluded, never simplified. */ }
            }
            return result.OrderBy(r => r.Name).ThenBy(r => r.EditorId).ThenBy(r => r.Source.ToString()).ToArray();
        }
        finally { foreach (var source in sources) source.Dispose(); }
    }, cancellationToken);
}
