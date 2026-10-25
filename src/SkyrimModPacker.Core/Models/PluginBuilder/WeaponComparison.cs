using System.Globalization;

namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record WeaponComparisonRecipe(string Title, SmartModRecipeDraft Data, string? CopyBlock = null)
{
    public bool CanCopy => CopyBlock == null;
}
public sealed record WeaponComparisonSnapshot(string Source, WeaponKind Kind, WeaponDraftFields Fields,
    IReadOnlyList<WeaponComparisonRecipe> Recipes, Guid? ItemId = null, string? SourceDraftJson = null, string? SourceFingerprint = null,
    string Notice = "", bool UseProjectResources = true);
public sealed record WeaponComparisonValue(string Parameter, string Mine, string Sample, string Difference, bool Equal);
public sealed record WeaponComparisonSelection(bool Combat, bool Economy, bool Material,
    WeaponComparisonRecipe? Crafting = null, WeaponComparisonRecipe? Tempering = null);

public static class WeaponComparison
{
    public static bool SameRecord(PluginRecordReference? a, PluginRecordReference? b) =>
        a == null ? b == null : b != null && a.FormId == b.FormId && a.PluginName.Equals(b.PluginName, StringComparison.OrdinalIgnoreCase);
    public static WeaponComparisonValue Numeric(string parameter, string? mine, string? sample, string missing = "—")
    {
        static bool Number(string? raw, out double value) => double.TryParse(raw?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        var validMine = Number(mine, out var m); var validSample = Number(sample, out var s);
        var delta = m - s;
        return new(parameter, validMine ? mine! : missing, validSample ? sample! : missing,
            validMine && validSample ? delta.ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture) +
                (s == 0 ? "" : " / " + (delta * 100 / s).ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture) + "%") : missing,
            validMine && validSample && m == s);
    }
    public static string RecordKey(PluginRecordReference? reference) => reference == null ? "?" : reference.PluginName.ToUpperInvariant() + ":" + reference.FormId;
    public static IReadOnlyList<WeaponComparisonValue> Ingredients(SmartModRecipeDraft? mine, SmartModRecipeDraft? sample, Func<PluginRecordReference?, string> name)
    {
        var left = mine?.Ingredients ?? []; var right = sample?.Ingredients ?? [];
        var references = left.Concat(right).Select(i => i.Reference).DistinctBy(RecordKey).ToArray();
        string? Count(IReadOnlyList<SmartModIngredientDraft> values, PluginRecordReference? reference)
        {
            var matches = values.Where(i => SameRecord(i.Reference, reference)).ToArray();
            if (matches.Length == 0) return "0";
            if (matches.Any(i => !int.TryParse(i.Count, out var n) || n <= 0)) return null;
            return matches.Sum(i => (long)int.Parse(i.Count)).ToString(CultureInfo.InvariantCulture);
        }
        return references.Select(r => Numeric(name(r) + " [" + r + "]", Count(left, r), Count(right, r))).ToArray();
    }
}
