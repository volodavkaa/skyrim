using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed record ItemListLink(PluginRecordReference List, string EditorId, short Level, short Count);

public static class ItemOverviewInspector
{
    /// <summary>Only direct list entries saved in this plugin; does not infer the full load order.</summary>
    public static IReadOnlyList<ItemListLink> ReadDirectLists(string pluginPath, uint formId, CancellationToken token = default)
    {
        PackingPathGuard.EnsureNoLinks(pluginPath);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var weapon = new FormKey(ModKey.FromNameAndExtension(Path.GetFileName(pluginPath)), formId);
        var result = new List<ItemListLink>();
        foreach (var list in mod.LeveledItems)
        {
            token.ThrowIfCancellationRequested();
            if (list.IsDeleted) continue;
            foreach (var entry in list.Entries ?? [])
                if (entry.Data is { } data && data.Reference.FormKey == weapon)
                    result.Add(new(new(list.FormKey.ModKey.FileName.String, list.FormKey.ID), list.EditorID ?? "", data.Level, data.Count));
        }
        return result;
    }
}
