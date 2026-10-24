namespace SkyrimModPacker.Core.Models.PluginBuilder;

public enum EditorDraftState { Pending, Clean, Discarded }

/// <summary>Raw editor text is intentional: an invalid value must survive draft persistence.</summary>
public sealed record WeaponDraftFields
{
    public string EditorId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Damage { get; init; } = "0";
    public string Weight { get; init; } = "0";
    public string Value { get; init; } = "0";
    public string Description { get; init; } = "";
    public string Speed { get; init; } = "1";
    public string Reach { get; init; } = "1";
    public string ModelPath { get; init; } = "";
    public string FirstPersonPath { get; init; } = "";
    public bool FirstPersonEdited { get; init; }
    public bool UseSharedModel { get; init; }
    public bool RestoreFirstPerson { get; init; }
    public PluginRecordReference? FirstPersonReference { get; init; }
    public uint? RestorationOrigin { get; init; }
    public string RestorationPluginName { get; init; } = "Skyrim.esm";
    public PluginRecordReference? Sound { get; init; }
    public IReadOnlyDictionary<string, PluginRecordReference?>? Sounds { get; init; }
    public PluginRecordReference? Enchantment { get; init; }
    public string EnchantmentCharge { get; init; } = "0";
    public IReadOnlyList<PluginRecordReference> Keywords { get; init; } = [];
}

public sealed record RecipeDraftFields
{
    public string EditorId { get; init; } = "";
    public string OutputCount { get; init; } = "1";
    public PluginRecordReference? Workbench { get; init; }
    public PluginRecordReference? Perk { get; init; }
    public IReadOnlyList<RecipeIngredient> Ingredients { get; init; } = [];
}

/// <summary>Independent sidecar; never imported into the confirmed plugin by the draft writer.</summary>
public sealed record WeaponEditorDraft
{
    public int Version { get; init; } = 1;
    public long Revision { get; init; }
    public Guid WriterSessionId { get; init; }
    public DateTimeOffset SavedAtUtc { get; init; }
    public int WriterProcessId { get; init; }
    public DateTimeOffset? WriterProcessStartedUtc { get; init; }
    public WorkspaceRevision? SourceRevision { get; init; }
    public string PluginPath { get; init; } = "";
    public string ProjectFilePath { get; init; } = "";
    public string GameDataPath { get; init; } = "";
    public uint? WeaponFormId { get; init; }
    public uint? TemplateFormId { get; init; }
    public string TemplatePluginName { get; init; } = "Skyrim.esm";
    public uint? RecipeFormId { get; init; }
    public uint? RestorationTemplateFormId { get; init; }
    public string RestorationTemplatePluginName { get; init; } = "Skyrim.esm";
    public int CurrentStep { get; init; }
    public int WizardSchemaVersion { get; init; } = 1;
    public bool NewWeapon { get; init; }
    public bool NewRecipe { get; init; }
    public bool WeaponIsDirty { get; init; }
    public bool RecipeIsDirty { get; init; }
    public bool WeaponDiscarded { get; init; }
    public bool RecipeDiscarded { get; init; }
    public EditorDraftState State { get; init; }
    public WeaponDraftFields Weapon { get; init; } = new();
    public RecipeDraftFields Recipe { get; init; } = new();
}

public sealed record DraftWriteReceipt(string FilePath, long Revision, DateTimeOffset SavedAtUtc);
