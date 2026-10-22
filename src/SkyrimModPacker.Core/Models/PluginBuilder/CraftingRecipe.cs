namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed class RecipeIngredient
{
    public required PluginRecordReference Reference { get; init; }
    public int Count { get; init; } = 1;
}

/// <summary>A forge or tempering recipe for an owned weapon.</summary>
public sealed class CraftingRecipe
{
    public uint? FormId { get; init; }
    public uint WeaponFormId { get; init; }
    public required string EditorId { get; init; }
    public required PluginRecordReference Workbench { get; init; }
    public ushort OutputCount { get; init; } = 1;
    public IReadOnlyList<RecipeIngredient> Ingredients { get; init; } = [];
    public PluginRecordReference? RequiredPerk { get; init; }
}

public sealed class SaveRecipeRequest
{
    public required string PluginPath { get; init; }
    public required string GameDataPath { get; init; }
    public required CraftingRecipe Recipe { get; init; }
}
