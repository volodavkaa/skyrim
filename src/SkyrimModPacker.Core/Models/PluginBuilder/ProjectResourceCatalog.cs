using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Models.PluginBuilder;

public enum ResourceUseKind { ThirdPerson, FirstPerson, Static }
public enum ResourceArchiveStatus { Included, VanillaExcluded, Blocked, NotRegistered }
public enum ResourceIssue { DuplicateRegistration, VanillaShadow, UnregisteredCustom, MissingOrEmpty, ReadFailed, InvalidPath, ArchivePathConflict }

public sealed record ResourceUsage(PluginRecordReference Record, string EditorId, ResourceUseKind Kind,
    PluginRecordReference? ViaRecord = null);

/// <summary>Saved plugin references only. Texture references inside NIF are deliberately unknown.</summary>
public sealed record ProjectResourceEntry(string RelativePath, FileCategory Category, ModelOrigin Origin,
    ModelAvailability Availability, bool IsRegistered, ResourceArchiveStatus ArchiveStatus,
    IReadOnlyList<ResourceUsage> Uses, IReadOnlyList<ResourceIssue> Issues, string? Detail = null)
{
    public bool UsageIsKnown => Category == FileCategory.Mesh;
    public bool BlocksPacking => ArchiveStatus is ResourceArchiveStatus.Blocked or ResourceArchiveStatus.NotRegistered;
}

public sealed record ResourceCatalogDiagnostic(string Record, string Detail);
public sealed record ProjectResourceCatalog(IReadOnlyList<ProjectResourceEntry> Entries,
    IReadOnlyList<ResourceCatalogDiagnostic> Diagnostics)
{
    public bool CanPack => Diagnostics.Count == 0 && Entries.All(e => !e.BlocksPacking);
}
