namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Exact vanilla EditorID allowlist, used only after checking the record's item type.</summary>
public static class CraftingMaterialCatalog
{
    private static readonly Dictionary<string, (string Category, string Aliases)> Materials = Build();
    private static Dictionary<string, (string, string)> Build()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (metal, alias) in new[] { ("Iron", "залізо залізний"), ("Steel", "сталь сталевий"), ("Corundum", "корунд"),
            ("Dwarven", "двемерський двемер"), ("Ebony", "ебоніт ебонітовий"), ("Gold", "золото золотий"), ("Silver", "срібло срібний"),
            ("Moonstone", "місячний камінь"), ("Orichalcum", "орихалк оріхалк"), ("Quicksilver", "ртуть ртутний"), ("Malachite", "малахіт") })
        { result["Ingot" + metal] = ("Metal", alias + " злиток ingot"); result["Ore" + metal] = ("Metal", alias + " руда ore"); }
        foreach (var id in new[] { "Leather01", "LeatherStrips" }) result[id] = ("Leather", "шкіра смужки шкіряні leather strips");
        result["Firewood01"] = result["Firewood"] = ("Wood", "дрова деревина wood firewood");
        result["DragonBone"] = ("Dragon", "дракон кістка кістки dragon bone"); result["DragonScales"] = ("Dragon", "дракон луска dragon scales");
        foreach (var (gem, alias) in new[] { ("Amethyst", "аметист"), ("Diamond", "діамант алмаз"), ("Emerald", "смарагд"), ("Garnet", "гранат"), ("Ruby", "рубін"), ("Sapphire", "сапфір") })
        { result["Gem" + gem] = ("Gems", alias + " gem"); result["Gem" + gem + "Flawless"] = ("Gems", alias + " бездоганний flawless gem"); }
        result["DaedraHeart"] = ("Special", "серце даедра daedra heart");
        result["DLC1VampireDust"] = ("Special", "прах вампір vampire dust");
        return result;
    }
    public static (string Category, string Aliases) Classify(string editorId, bool eligibleType) =>
        eligibleType && Materials.TryGetValue(editorId, out var item) ? item : ("", "");
}

public sealed record MaterialSearchResult(IReadOnlyList<RecordOption> Items, int Total);
public sealed class MaterialSearchIndex
{
    private readonly (RecordOption Item, string Search)[] _entries;
    public MaterialSearchIndex(IEnumerable<RecordOption> items) => _entries = items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .Select(i => (i, (i.DisplayName + " " + i.EditorId + " " + i.SearchAliases).ToUpperInvariant())).ToArray();
    public MaterialSearchResult Search(string text, string category = "", bool includeOther = false, int limit = 50)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToUpperInvariant()).ToArray();
        var found = new List<RecordOption>(); var total = 0; limit = Math.Clamp(limit, 1, 100);
        foreach (var entry in _entries)
            if ((includeOther || entry.Item.MaterialCategory.Length > 0) && (category.Length == 0 || entry.Item.MaterialCategory == category) && words.All(entry.Search.Contains))
            { total++; if (found.Count < limit) found.Add(entry.Item); }
        return new(found, total);
    }
}
