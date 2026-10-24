using System.Globalization;

namespace SkyrimModPacker.Core.Models.PluginBuilder;

public static class SmartTuningApplication
{
    public static SmartModItemDraft Balance(SmartModItemDraft item, SmartWeaponTuning reference, SmartBalanceMode mode, bool keepManual)
    {
        var stats = SmartWeaponBalance.Calculate(reference.Base ?? throw new InvalidOperationException("Choose a valid reference weapon."), mode, reference.Handling);
        var manual = reference.ManualFields;
        string Pick(string field, string old, string value) => keepManual && manual.Contains(field) ? old : value;
        var w = item.Weapon;
        return item with { State = SmartModItemState.InProgress, Weapon = w with
        {
            Damage = Pick("Damage", w.Damage, stats.Damage.ToString(CultureInfo.InvariantCulture)), Weight = Pick("Weight", w.Weight, stats.Weight.ToString(CultureInfo.InvariantCulture)),
            Value = Pick("Value", w.Value, stats.Value.ToString(CultureInfo.InvariantCulture)), Speed = Pick("Speed", w.Speed, stats.Speed.ToString(CultureInfo.InvariantCulture)),
            Reach = Pick("Reach", w.Reach, stats.Reach.ToString(CultureInfo.InvariantCulture))
        }, Tuning = reference with { Mode = mode, ManualFields = keepManual ? manual : manual.Except(SmartWeaponBalance.Fields).ToArray() } };
    }

    public static SmartModItemDraft Material(SmartModItemDraft item, SmartMaterialPreset preset, IReadOnlyCollection<PluginRecordReference> materialFamily,
        bool keepManual, bool crafting, bool tempering)
    {
        if (!preset.Available || preset.Kind != item.Kind) throw new InvalidOperationException(preset.Error ?? "Incompatible material preset.");
        if (item.Recipes.GroupBy(r => r.IsTempering).Any(g => g.Count() > 1)) throw new InvalidOperationException("Resolve duplicate recipes before applying a material.");
        var reference = (item.Tuning ?? new SmartWeaponTuning { ManualFields = SmartWeaponBalance.Fields.Concat(new[] { "Crafting", "Tempering" }).ToArray() }) with
        { PresetId = preset.Id, PresetVersion = preset.Version, Reference = preset.Reference, ReferenceEditorId = preset.EditorId, SourceFingerprint = preset.SourceFingerprint, Base = preset.Stats };
        var mode = reference.Mode == SmartBalanceMode.Custom ? SmartBalanceMode.Equal : reference.Mode;
        var result = Balance(item, reference, mode, keepManual);
        var recipes = item.Recipes.ToList(); var removals = reference.PendingRecipeRemovals.ToHashSet();
        void SetRecipe(bool isTempering, bool enabled, SmartPresetRecipe? source)
        {
            var old = recipes.SingleOrDefault(r => r.IsTempering == isTempering);
            if (!enabled)
            { if (old != null) { recipes.Remove(old); if (old.ConfirmedFormId is { } id) removals.Add(id); } return; }
            if (old != null && keepManual && reference.ManualFields.Contains(isTempering ? "Tempering" : "Crafting")) return;
            if (source == null) throw new InvalidOperationException("The standard recipe is unavailable.");
            var data = source.Recipe; var identity = old?.Id ?? Guid.NewGuid();
            var ingredients = data.Ingredients.GroupBy(i => i.Reference).Select(g => new SmartModIngredientDraft(g.Key, checked(g.Sum(i => i.Count)).ToString(CultureInfo.InvariantCulture))).ToArray();
            if (ingredients.Any(i => !int.TryParse(i.Count, out var n) || n <= 0)) throw new InvalidDataException("Invalid material quantity.");
            var recipe = new SmartModRecipeDraft { Id = identity, ConfirmedFormId = old?.ConfirmedFormId, EditorId = old?.EditorId ?? "SMP_Recipe_" + identity.ToString("N"),
                IsTempering = isTempering, OutputCount = data.OutputCount.ToString(CultureInfo.InvariantCulture), Workbench = data.Workbench,
                RequiredPerk = data.RequiredPerk, TemperingPerk = data.TemperingPerk, Ingredients = ingredients };
            if (old != null) recipes.Remove(old); recipes.Add(recipe);
        }
        SetRecipe(false, crafting, preset.Crafting); SetRecipe(true, tempering, preset.Tempering);
        return result with { Weapon = result.Weapon with { Keywords = item.Weapon.Keywords.Except(materialFamily).Concat(preset.MaterialKeywords).Distinct().ToArray() }, Recipes = recipes,
            Tuning = result.Tuning! with { CraftingIntent = crafting ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled,
                TemperingIntent = tempering ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled, PendingRecipeRemovals = removals.ToArray(),
                ManualFields = keepManual ? result.Tuning!.ManualFields : result.Tuning!.ManualFields.Except(new[] { "Crafting", "Tempering" }).ToArray() } };
    }
}
