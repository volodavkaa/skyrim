namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record SmartRecipeSource(PluginRecordReference Source, PluginRecordReference Weapon, string EditorId, string Name,
    WeaponKind Kind, SmartWeaponSubtype? Subtype, CraftingRecipe Recipe, string SourceFingerprint)
{
    public string DisplayName => Name + " [" + EditorId + "; " + Weapon + "]";
}

public sealed record SmartRecipePreset
{
    public int Version { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public WeaponKind Kind { get; init; }
    public SmartWeaponSubtype? Subtype { get; init; }
    public required CraftingRecipe Recipe { get; init; }
    public void Validate()
    {
        if (Version != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl) || !Enum.IsDefined(Kind) || Subtype != null && !Enum.IsDefined(Subtype.Value))
            throw new InvalidDataException("Invalid recipe preset identity, name or type.");
        SmartRecipeApplication.Validate(Recipe);
    }
}

public static class SmartRecipeApplication
{
    public static void Validate(CraftingRecipe recipe)
    {
        static bool Valid(PluginRecordReference? reference) => reference != null && reference.FormId is > 0 and <= 0xFFFFFF && !string.IsNullOrWhiteSpace(reference.PluginName) &&
            Path.GetFileName(reference.PluginName) == reference.PluginName && reference.PluginName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && new[] { ".esm", ".esp", ".esl" }.Contains(Path.GetExtension(reference.PluginName), StringComparer.OrdinalIgnoreCase);
        if (recipe == null || !Valid(recipe.Workbench) || recipe.OutputCount == 0 || recipe.IsTempering && recipe.OutputCount != 1 || !recipe.IsTempering && recipe.TemperingPerk != null ||
            recipe.RequiredPerk != null && !Valid(recipe.RequiredPerk) || recipe.TemperingPerk != null && !Valid(recipe.TemperingPerk) || recipe.Ingredients == null || recipe.Ingredients.Count == 0 ||
            recipe.Ingredients.Any(i => i == null || !Valid(i.Reference) || i.Count <= 0)) throw new InvalidDataException("Invalid recipe workbench, output or ingredients.");
    }
    public static SmartModItemDraft Apply(SmartModItemDraft item, CraftingRecipe source, bool keepManual)
    {
        Validate(source);
        if (item.Recipes.GroupBy(r => r.IsTempering).Any(g => g.Count() > 1)) throw new InvalidOperationException("Resolve duplicate recipes before applying a template.");
        var field = source.IsTempering ? "Tempering" : "Crafting";
        var old = item.Recipes.SingleOrDefault(r => r.IsTempering == source.IsTempering);
        if (keepManual && old != null && (item.Tuning == null || item.Tuning.ManualFields.Contains(field))) return item;
        var id = old?.Id ?? Guid.NewGuid();
        var recipe = new SmartModRecipeDraft { Id = id, ConfirmedFormId = old?.ConfirmedFormId, EditorId = old?.EditorId ?? "SMP_Recipe_" + id.ToString("N"), IsTempering = source.IsTempering,
            OutputCount = source.OutputCount.ToString(System.Globalization.CultureInfo.InvariantCulture), Workbench = source.Workbench, RequiredPerk = source.RequiredPerk, TemperingPerk = source.TemperingPerk,
            Ingredients = source.Ingredients.GroupBy(i => i.Reference).Select(g => new SmartModIngredientDraft(g.Key, checked(g.Sum(i => i.Count)).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray() };
        var tuning = item.Tuning ?? new SmartWeaponTuning { Mode = SmartBalanceMode.Custom, ManualFields = SmartWeaponBalance.Fields,
            CraftingIntent = item.Recipes.Any(r => !r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled,
            TemperingIntent = item.Recipes.Any(r => r.IsTempering) ? SmartRecipeIntent.Enabled : SmartRecipeIntent.Disabled };
        tuning = tuning with { ManualFields = tuning.ManualFields.Except(new[] { field }).ToArray() };
        tuning = source.IsTempering ? tuning with { TemperingIntent = SmartRecipeIntent.Enabled } : tuning with { CraftingIntent = SmartRecipeIntent.Enabled };
        return item with { State = SmartModItemState.InProgress, Tuning = tuning, Recipes = item.Recipes.Where(r => r.IsTempering != source.IsTempering).Append(recipe).ToArray() };
    }
}
