using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>Localized read-only projection; archive inclusion is controlled by the saved project.</summary>
public sealed class ProjectResourceRow
{
    public ProjectResourceEntry Entry { get; }
    public string RelativePath => Entry.RelativePath;
    public string TypeText { get; }
    public string OriginText { get; }
    public string AvailabilityText { get; }
    public string RegistrationText { get; }
    public string ArchiveText { get; }
    public string UsageText { get; }
    public string IssueText { get; }
    public string? Detail => Entry.Detail;
    public bool HasIssues => Entry.Issues.Count > 0;

    public ProjectResourceRow(ProjectResourceEntry entry, Func<string, string, string> text)
    {
        Entry = entry;
        TypeText = text(entry.Category == SkyrimModPacker.Core.Models.FileCategory.Mesh ? "Items_ModelFile" : "Items_TextureFile", entry.Category.ToString());
        OriginText = text("Models_Origin_" + entry.Origin, entry.Origin.ToString());
        AvailabilityText = text("Models_Availability_" + entry.Availability, entry.Availability.ToString());
        RegistrationText = text(entry.IsRegistered ? "Resources_Registered" : "Resources_Unregistered", entry.IsRegistered ? "Registered" : "Not registered");
        ArchiveText = text("Resources_Archive_" + entry.ArchiveStatus, entry.ArchiveStatus.ToString());
        UsageText = !entry.UsageIsKnown ? text("Resources_TextureUsageUnknown", "Texture references inside NIF have not been analyzed.") :
            entry.Uses.Count == 0 ? text("Resources_NoSavedUses", "No uses in the saved WEAP/STAT records; the registered resource remains in ZIP.") :
            string.Join(Environment.NewLine, entry.Uses.Select(use =>
                $"{use.EditorId} [{use.Record}] — {text("Resources_Use_" + use.Kind, use.Kind.ToString())}" +
                (use.ViaRecord == null ? "" : " → STAT [" + use.ViaRecord + "]")));
        IssueText = entry.Issues.Count == 0 ? text("Resources_NoConflicts", "No resource issues detected") :
            string.Join(Environment.NewLine, entry.Issues.Select(issue => text("Resources_Issue_" + issue, issue.ToString())));
    }
}
