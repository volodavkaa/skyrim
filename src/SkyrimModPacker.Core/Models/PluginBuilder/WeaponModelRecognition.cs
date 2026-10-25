using System.Text.RegularExpressions;

namespace SkyrimModPacker.Core.Models.PluginBuilder;

public enum ModelRecognitionStatus { Record, Suggested, NeedsChoice }
public sealed record WeaponModelSuggestion(WeaponFolderSource Model, WeaponFolderSource? FirstPerson, string Name,
    WeaponKind? Kind, ModelRecognitionStatus Status, string Evidence, bool Included)
{ public SmartWeaponSubtype? Subtype { get; init; } }

/// <summary>Conservative proposals only. Filename evidence never counts as a verified weapon type.</summary>
public static class WeaponModelRecognition
{
    private static readonly Regex First = new(@"(?:^|[ _.-])(?:1st(?:person)?|first(?:person)?|1stp)(?=$|[ _.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path.Replace('/', Path.DirectorySeparatorChar));
    private static bool FirstModel(string path) => First.IsMatch(Stem(path)) || Normal(path).Split('/').SkipLast(1).Any(s => First.IsMatch(s));
    private static string GroupKey(string path) => string.Join('/', Normal(path).Split('/').SkipLast(1).Where(s => !First.IsMatch(s))) + "/" + First.Replace(Stem(path), " ").Trim(' ', '_', '-', '.');
    private static string Normal(string path) => path.Replace('\\', '/').TrimStart('/');
    public static IReadOnlyList<WeaponModelSuggestion> Analyze(WeaponFolderScan scan, CancellationToken token = default)
    {
        var models = scan.Files.Where(f => f.SourcePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).ToArray();
        var firstModels = models.Where(m => FirstModel(m.RelativePath)).ToArray();
        var firstGroups = firstModels.GroupBy(m => GroupKey(m.RelativePath), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        string HintPath(WeaponModelRecordHint hint)
        { var value = Normal(hint.ModelPath); return value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? value : "meshes/" + value; }
        var hintGroups = scan.RecordHints.GroupBy(HintPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WeaponModelSuggestion>();
        foreach (var model in models.Where(m => !FirstModel(m.RelativePath)))
        {
            token.ThrowIfCancellationRequested();
            var firsts = firstGroups.GetValueOrDefault(GroupKey(model.RelativePath)) ?? [];
            // Ambiguous pairs stay separate, so no resource is silently lost.
            var first = firsts.Length == 1 ? firsts[0] : null;
            if (first != null) paired.Add(first.SourcePath);
            result.Add(Propose(model, first));
        }
        foreach (var model in firstModels.Where(m => !paired.Contains(m.SourcePath)))
            result.Add(new(model, null, Stem(model.RelativePath), null, ModelRecognitionStatus.NeedsChoice, "UnpairedFirstPerson", false));
        return result;

        WeaponModelSuggestion Propose(WeaponFolderSource model, WeaponFolderSource? first)
        {
            if (model.Error != null) return new(model, first, Stem(model.RelativePath), null, ModelRecognitionStatus.NeedsChoice, model.Error, false);
            var path = Normal(model.RelativePath);
            var segments = path.Split('/'); var hints = new List<WeaponModelRecordHint>();
            for (var i = 0; i < segments.Length; i++)
                if (segments[i].Equals("meshes", StringComparison.OrdinalIgnoreCase) && hintGroups.TryGetValue(string.Join('/', segments.Skip(i)), out var matches)) hints.AddRange(matches);
            if (hints.Count == 1) return new(model, first, hints[0].Name, hints[0].Kind, ModelRecognitionStatus.Record, hints[0].Plugin + " · " + hints[0].EditorId, true);
            if (hints.Count > 1) return new(model, first, Stem(model.RelativePath), null, ModelRecognitionStatus.NeedsChoice, "ConflictingRecords", true);
            var evidence = Stem(model.RelativePath) + " " + string.Join(' ', Normal(model.RelativePath).Split('/').SkipLast(1));
            var words = Regex.Replace(Regex.Replace(evidence, "([a-z])([A-Z])", "$1 $2"), "[_.-]+", " ").ToLowerInvariant();
            bool Has(string pattern) => Regex.IsMatch(words, @"(?:^|[ _.-])(?:" + pattern + @")(?=$|[ _.-])", RegexOptions.CultureInvariant);
            var kinds = new HashSet<WeaponKind>();
            if (Has("greatsword|great sword|claymore|2h|two handed")) kinds.Add(WeaponKind.Greatsword);
            if (Has("dagger|knife")) kinds.Add(WeaponKind.Dagger);
            if (Has("waraxe|war axe|hatchet")) kinds.Add(WeaponKind.WarAxe);
            if (Has("mace")) kinds.Add(WeaponKind.Mace);
            if (Has("battleaxe|battle axe|warhammer|war hammer")) kinds.Add(WeaponKind.BattleaxeWarhammer);
            if (Has("crossbow|cross bow")) kinds.Add(WeaponKind.Crossbow);
            else if (Has("bow")) kinds.Add(WeaponKind.Bow);
            if (Has("sword") && !Has("greatsword|great sword|2h|two handed")) kinds.Add(WeaponKind.OneHandSword);
            if (kinds.Count == 0 && Has("1h|one handed")) kinds.Add(WeaponKind.OneHandSword);
            // A generic 2h marker must not override a more explicit axe/hammer type.
            if (Has("2h|two handed") && kinds.Contains(WeaponKind.BattleaxeWarhammer)) kinds.Remove(WeaponKind.Greatsword);
            var helper = Has("scabbard|sheath|collision|blood|inventory|preview");
            var kind = kinds.Count == 1 && !helper ? kinds.Single() : (WeaponKind?)null;
            return new(model, first, Stem(model.RelativePath).Replace('_', ' '), kind,
                kind == null ? ModelRecognitionStatus.NeedsChoice : ModelRecognitionStatus.Suggested,
                helper ? "Auxiliary" : kind == null ? "UnknownType" : model.RelativePath, !helper)
            { Subtype = kind == WeaponKind.BattleaxeWarhammer ? Has("warhammer|war hammer") && !Has("battleaxe|battle axe") ? SmartWeaponSubtype.Warhammer : Has("battleaxe|battle axe") && !Has("warhammer|war hammer") ? SmartWeaponSubtype.Battleaxe : null : null };
        }
    }
}
