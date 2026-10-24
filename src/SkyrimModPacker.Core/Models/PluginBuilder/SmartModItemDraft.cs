using System.Text.Json.Serialization;

namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>List order is the filling queue; identity remains stable across renames and saves.</summary>
public sealed record SmartModItemDraft
{
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    public WeaponKind Kind { get; init; } = WeaponKind.OneHandSword;
    public SmartModItemStep Step { get; init; }
    public SmartModItemState State { get; init; }
    public uint? ConfirmedFormId { get; init; }
    public PluginRecordReference? Template { get; init; }
    [JsonRequired] public WeaponDraftFields Weapon { get; init; } = new();
    [JsonRequired] public IReadOnlyList<SmartModRecipeDraft> Recipes { get; init; } = [];
}

public sealed record SmartModRecipeDraft
{
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    public uint? ConfirmedFormId { get; init; }
    public bool IsTempering { get; init; }
    public string EditorId { get; init; } = "";
    public string OutputCount { get; init; } = "1";
    public PluginRecordReference? Workbench { get; init; }
    public PluginRecordReference? RequiredPerk { get; init; }
    public PluginRecordReference? TemperingPerk { get; init; }
    [JsonRequired] public IReadOnlyList<SmartModIngredientDraft> Ingredients { get; init; } = [];
}

/// <summary>Invalid or partially entered quantities must survive saving a draft.</summary>
public sealed record SmartModIngredientDraft(PluginRecordReference? Reference, string Count);
