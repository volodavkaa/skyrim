using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    private static Dictionary<FormKey, List<LeveledListUsage>> ListUsages(IEnumerable<ISkyrimModGetter> sources, CancellationToken token)
    {
        var result = new Dictionary<FormKey, List<LeveledListUsage>>();
        var masters = sources.ToArray();
        var merchants = masters.SelectMany(s => s.Factions).Where(f => !f.IsDeleted && f.MerchantContainer.FormKey != FormKey.Null).ToArray();
        var merchantRefs = merchants.Select(f => f.MerchantContainer.FormKey).ToHashSet();
        var placedMerchants = merchantRefs.Count == 0 ? new Dictionary<FormKey, FormKey>() : masters.SelectMany(s => s.EnumerateMajorRecords<IPlacedObjectGetter>())
            .Where(p => merchantRefs.Contains(p.FormKey) && !p.IsDeleted).GroupBy(p => p.FormKey).ToDictionary(g => g.Key, g => g.Last().Base.FormKey);
        var merchantBases = placedMerchants.Values.ToHashSet();
        var containers = masters.SelectMany(s => s.Containers).Where(c => !c.IsDeleted).GroupBy(c => c.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var outfits = masters.SelectMany(s => s.Outfits).Where(o => !o.IsDeleted).GroupBy(o => o.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var merchantFactions = merchants.GroupBy(f => f.FormKey).ToDictionary(g => g.Key, g => g.Last());
        foreach (var source in masters)
        {
            // Categories describe verified direct references, never guessed EditorID prefixes.
            foreach (var container in source.Containers.Where(c => !c.IsDeleted))
                foreach (var entry in container.Items ?? []) Add(entry.Item.Item.FormKey, merchantBases.Contains(container.FormKey) ? "Merchants" : "Containers", container.Name?.String ?? "", container.EditorID ?? "", container.FormKey, "Container");
            foreach (var outfit in source.Outfits.Where(o => !o.IsDeleted))
                foreach (var item in outfit.Items ?? []) Add(item.FormKey, "Equipment", "", outfit.EditorID ?? "", outfit.FormKey, "Outfit");
            foreach (var npc in source.Npcs.Where(n => !n.IsDeleted))
            {
                foreach (var entry in npc.Items ?? []) Add(entry.Item.Item.FormKey, "Equipment", npc.Name?.String ?? "", npc.EditorID ?? "", npc.FormKey, "Npc");
                foreach (var key in new[] { npc.DefaultOutfit.FormKey, npc.SleepingOutfit.FormKey }.Distinct())
                    if (outfits.TryGetValue(key, out var outfit)) foreach (var item in outfit.Items ?? [])
                        Add(item.FormKey, "Equipment", npc.Name?.String ?? "", npc.EditorID ?? "", npc.FormKey, "Npc", outfit.FormKey);
                foreach (var membership in npc.Factions ?? [])
                    if (membership.Rank >= 0 && merchantFactions.TryGetValue(membership.Faction.FormKey, out var faction) &&
                        placedMerchants.TryGetValue(faction.MerchantContainer.FormKey, out var baseKey) && containers.TryGetValue(baseKey, out var container))
                        foreach (var entry in container.Items ?? []) Add(entry.Item.Item.FormKey, "Merchants", npc.Name?.String ?? "", npc.EditorID ?? "", npc.FormKey, "Npc", container.FormKey);
            }
        }
        return result;
        void Add(FormKey key, string category, string name, string editor, FormKey owner, string kind, FormKey? via = null)
        {
            token.ThrowIfCancellationRequested(); if (key == FormKey.Null) return;
            if (!result.TryGetValue(key, out var usages)) result[key] = usages = [];
            usages.Add(new(category, name, editor, Reference(owner)!) { Kind = kind, ViaRecord = via == null ? null : Reference(via.Value) });
        }
    }

    public Task<Result<LeveledListDetails>> GetLeveledListDetailsAsync(string pluginPath, string gameDataPath, PluginRecordReference list, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var mod = ReadWorkspaceForEditing(pluginPath, gameDataPath); using var resolver = new RecordResolver(mod, gameDataPath);
            var record = resolver.Resolve<ILeveledItemGetter>(list); var entries = new List<LeveledListEntryInfo>();
            foreach (var entry in record.Entries ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested(); var data = entry.Data;
                if (data == null) { entries.Add(new(null, "", "", 0, 0, false, "Entry has no data.")); continue; }
                var reference = Reference(data.Reference.FormKey);
                try
                {
                    var item = resolver.Resolve<IItemGetter>(reference ?? throw new InvalidDataException("Entry has a NULL reference."));
                    var name = item switch { IWeaponGetter w => w.Name?.String, IArmorGetter a => a.Name?.String, IMiscItemGetter m => m.Name?.String,
                        IIngredientGetter i => i.Name?.String, _ => null };
                    entries.Add(new(reference, name ?? item.EditorID ?? reference.ToString(), item.EditorID ?? "", data.Level, data.Count, item is ILeveledItemGetter));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { entries.Add(new(reference, reference?.ToString() ?? "NULL", "", data.Level, data.Count, false, ex.Message)); }
            }
            return new LeveledListDetails(list, record.EditorID ?? "", record.Flags.ToString(), record.ChanceNone.ToString(), entries) { SelectionFlags = (uint)record.Flags };
        }, cancellationToken);
}
