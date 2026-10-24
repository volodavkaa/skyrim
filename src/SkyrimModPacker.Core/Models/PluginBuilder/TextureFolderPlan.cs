namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record TextureFolderItem(string SourcePath, string RelativePath, long Bytes, string Sha256, bool Identical, string? Conflict);
public sealed record TextureFolderPlan(string ProjectPath, string ProjectSha256, string SourceFolder, string TargetSubdirectory,
    IReadOnlyList<TextureFolderItem> Items, int SkippedFiles)
{
    public bool CanImport => Items.Count > 0 && Items.All(i => i.Conflict == null);
}
