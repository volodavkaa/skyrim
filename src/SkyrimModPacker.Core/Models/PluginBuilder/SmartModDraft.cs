using System.Text.Json.Serialization;

namespace SkyrimModPacker.Core.Models.PluginBuilder;

public enum SmartModSection { Overview, Weapons, Recipes, Resources, Checks, Finished }
public enum SmartModItemStep { NameAndTemplate, Appearance, Properties, Acquisition, Summary }
public enum SmartModItemState { Empty, InProgress, Ready, NeedsCorrection }

/// <summary>Wizard state, stored separately from confirmed plugin records.</summary>
public sealed record SmartModDraft
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public long Revision { get; init; }
    public DateTimeOffset SavedAtUtc { get; init; }
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    public string ProjectFilePath { get; init; } = "";
    public string PluginPath { get; init; } = "";
    public string Name { get; init; } = "";
    public bool EnableEsl { get; init; }
    public WorkspaceRevision? SourceRevision { get; init; }
    public string? LastArchivePath { get; init; }
    public bool CreateTextureVariant { get; init; }
    public int VariantMaxTextureSize { get; init; } = 2048;
    public string VariantTexconvPath { get; init; } = "";
    public string? LastVariantArchivePath { get; init; }
    public PluginRecordReference? PendingLeveledList { get; init; }
    public string PendingListLevel { get; init; } = "1";
    public string PendingListCount { get; init; } = "1";
    public SmartModSection Section { get; init; }
    public Guid? SelectedItemId { get; init; }
    public Guid? SelectedRecipeId { get; init; }
    public string? SelectedResourcePath { get; init; }
    [JsonRequired] public IReadOnlyList<SmartModItemDraft> Items { get; init; } = [];
}
