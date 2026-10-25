using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed record SmartComparisonChoice(string DisplayName, string Source, WeaponKind Kind, Guid? ItemId = null, SwordTemplate? Template = null, bool Saved = false);

public sealed partial class SmartModViewModel
{
    public IReadOnlyList<SmartComparisonChoice> ComparisonChoices =>
        Items.Select(i => new SmartComparisonChoice(i.Name + " [" + i.EditorId + "; " + Name + "]", Name, i.Kind, i.Id))
        .Concat(Templates.Select(t => new SmartComparisonChoice(t.Name + " [" + t.EditorId + "; " + t.PluginName + "]", t.PluginName, t.Kind, Template: t)))
        .Prepend(new(Label("Smart_CompareSaved"), Name, SelectedItem?.Kind ?? WeaponKind.OneHandSword, Saved: true)).ToArray();
    public string ComparisonRecordName(PluginRecordReference? reference) => reference == null ? "—" :
        Options.Ingredients.Concat(Options.Keywords).Concat(Options.Perks).Concat(Options.Enchantments)
            .FirstOrDefault(o => WeaponComparison.SameRecord(o.Reference, reference))?.DisplayName ?? reference.ToString();
    public WeaponComparisonSnapshot CaptureComparisonItem(SmartModItemRow item) => new(Name, item.Kind, item.Capture().Weapon,
        item.Recipes.Select(r => new WeaponComparisonRecipe(r.EditorId, r.Capture())).ToArray(), item.Id, JsonSerializer.Serialize(item.Capture()));

    public async Task<WeaponComparisonSnapshot> LoadComparisonAsync(SmartComparisonChoice choice, CancellationToken token)
    {
        if (choice.Saved)
        {
            var selectedId = SelectedItem?.Id;
            var savedDocument = Require(await _drafts.LoadAsync(ProjectPath, _gameData, token));
            var saved = savedDocument?.Items.FirstOrDefault(i => i.Id == selectedId) ?? throw new IOException(Label("Smart_CompareNoSaved"));
            return new(Label("Smart_CompareSaved"), saved.Kind, saved.Weapon, saved.Recipes.Select(r => new WeaponComparisonRecipe(r.EditorId, r)).ToArray());
        }
        if (choice.ItemId is { } id)
            return CaptureComparisonItem(Items.FirstOrDefault(i => i.Id == id) ?? throw new IOException(Label("Smart_TuningStale")));
        var template = choice.Template ?? throw new IOException(Label("Smart_SelectTemplate"));
        var path = Path.Combine(_gameData, template.PluginName);
        // Refresh the template rather than treating the startup catalog as the current file contents.
        var fingerprint = await Task.Run(() => { using var file = File.OpenRead(path); return template.PluginName + ":" + Convert.ToHexString(SHA256.HashData(file)); }, token);
        var current = Require(await _builder.GetWeaponTemplatesAsync(_gameData, token)).FirstOrDefault(t => t.FormId == template.FormId && t.PluginName.Equals(template.PluginName, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException(Label("Smart_TuningSourceChanged"));
        var models = await _builder.GetWeaponModelsAsync(path, _gameData, current.FormId, token);
        var recipes = await _builder.GetComparisonRecipesAsync(path, current.FormId, token);
        var fields = Fields(current.EditorId, current.Name, current.Damage, current.Weight, current.Value, current.Details);
        if (models.IsSuccess) fields = fields with { ModelPath = models.Value.ThirdPersonPath, FirstPersonPath = models.Value.FirstPersonPath ?? "" };
        var entries = recipes.IsSuccess ? recipes.Value.Select(r => r with { Data = r.Data with {
            IsTempering = Options.Keywords.Any(k => WeaponComparison.SameRecord(k.Reference, r.Data.Workbench) && k.EditorId == "CraftingSmithingSharpeningWheel") } }).ToArray() : [];
        return new(current.PluginName, current.Kind, fields, entries, SourceFingerprint: fingerprint,
            Notice: string.Join(Environment.NewLine, new[] { models.IsFailure ? models.Error : models.Value.Error, recipes.IsFailure ? recipes.Error : null }.Where(n => !string.IsNullOrEmpty(n))), UseProjectResources: false);
    }

    public SmartTuningPreview PreviewComparison(WeaponComparisonSnapshot source, WeaponComparisonSelection selection)
    {
        var before = SelectedItem?.Capture() ?? throw new IOException(Label("Smart_SelectTemplate"));
        if (!selection.Combat && !selection.Economy && !selection.Material && selection.Crafting == null && selection.Tempering == null)
            throw new IOException(Label("Smart_CompareSelectGroups"));
        var w = before.Weapon;
        if (selection.Combat) w = w with { Damage = source.Fields.Damage, Speed = source.Fields.Speed, Reach = source.Fields.Reach };
        if (selection.Economy) w = w with { Weight = source.Fields.Weight, Value = source.Fields.Value };
        var materials = Options.Keywords.Where(k => k.EditorId.StartsWith("WeapMaterial", StringComparison.Ordinal) || k.EditorId.StartsWith("DLC1WeapMaterial", StringComparison.Ordinal)).Select(k => k.Reference).ToArray();
        if (selection.Material) w = w with { Keywords = w.Keywords.Where(k => !materials.Any(m => WeaponComparison.SameRecord(k, m)))
            .Concat(source.Fields.Keywords.Where(k => materials.Any(m => WeaponComparison.SameRecord(k, m)))).DistinctBy(WeaponComparison.RecordKey).ToArray() };
        if (!ushort.TryParse(w.Damage, out _) || !SmartModItemRow.Number(w.Weight, out var weight) || weight < 0 || !uint.TryParse(w.Value, out _) ||
            !SmartModItemRow.Number(w.Speed, out var speed) || speed <= 0 || !SmartModItemRow.Number(w.Reach, out var reach) || reach <= 0)
            throw new IOException(Label("Smart_CompareInvalid"));
        var after = before with { Weapon = w, State = SmartModItemState.InProgress };
        foreach (var pair in new[] { (Recipe: selection.Crafting, Tempering: false), (Recipe: selection.Tempering, Tempering: true) })
        {
            if (pair.Recipe == null) continue;
            if (!source.Recipes.Contains(pair.Recipe) || !pair.Recipe.CanCopy || pair.Recipe.Data.IsTempering != pair.Tempering || !RecipeIsValid(pair.Recipe.Data))
                throw new IOException(Label("Smart_CompareRecipeBlocked") + " " + pair.Recipe.CopyBlock);
            var r = pair.Recipe.Data;
            if (r.TemperingPerk != null && !Options.Perks.Any(p => WeaponComparison.SameRecord(p.Reference, r.TemperingPerk) && p.EditorId == "ArcaneBlacksmith")) throw new IOException(Label("Smart_CompareRecipeBlocked"));
            after = SmartRecipeApplication.Apply(after, new() { EditorId = r.EditorId, Workbench = r.Workbench!, IsTempering = r.IsTempering, OutputCount = ushort.Parse(r.OutputCount),
                RequiredPerk = r.RequiredPerk, TemperingPerk = r.TemperingPerk, Ingredients = r.Ingredients.Select(i => new RecipeIngredient { Reference = i.Reference!, Count = int.Parse(i.Count) }).ToArray() }, false);
        }
        var tuning = after.Tuning ?? ReferenceFor(before);
        after = after with { Tuning = tuning with { Mode = SmartBalanceMode.Custom, ManualFields = tuning.ManualFields
            .Concat(selection.Combat ? new[] { "Damage", "Speed", "Reach" } : []).Concat(selection.Economy ? new[] { "Weight", "Value" } : [])
            .Concat(selection.Material ? new[] { "Material" } : []).Distinct().ToArray() } };
        var dependencies = after.Weapon.Keywords.Concat(after.Recipes.SelectMany(r => r.Ingredients.Select(i => i.Reference).Concat(new[] { r.Workbench, r.RequiredPerk, r.TemperingPerk })).OfType<PluginRecordReference>())
            .Select(r => r.PluginName).Distinct(StringComparer.OrdinalIgnoreCase).Except(Dependencies.Split(", ", StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
            .Where(n => !n.Equals(Path.GetFileName(PluginPath), StringComparison.OrdinalIgnoreCase)).ToArray();
        return DescribeTuning(before, after) with { ExpectedSourceFingerprint = source.SourceFingerprint, ComparisonSourceId = source.ItemId,
            ComparisonSourceJson = source.SourceDraftJson, Description = DescribeTuning(before, after).Description + Environment.NewLine + Label("Smart_CompareNewMasters") + " " +
                (dependencies.Length == 0 ? "—" : string.Join(", ", dependencies)) };
    }
}
