namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record WeaponFolderSource(string SourcePath, string RelativePath, long Bytes, string Format, string? Error);
public sealed record WeaponFolderScan(string Root, IReadOnlyList<WeaponFolderSource> Files, int SkippedFiles);
public sealed record WeaponFolderRequest(Guid Id, string ProjectPath, string SourceFolder, string ThirdPersonSource, string? FirstPersonSource,
    bool ShareModel, string TextureRoot = "", bool IncludeUnusedTextures = false, bool AllowIncomplete = false)
{
    public IReadOnlyList<WeaponFolderMapping> TextureMappings { get; init; } = [];
    public string? NewProjectPluginName { get; init; }
    public IReadOnlyList<WeaponFolderItemRequest> Items { get; init; } = [];
}
public sealed record WeaponFolderItemRequest(Guid Id, string Name, WeaponKind Kind, string ThirdPersonSource, string? FirstPersonSource, bool ShareModel);
public sealed record WeaponFolderItemPlan(Guid Id, string Name, WeaponKind Kind, string ThirdPersonPath, string? FirstPersonPath);
public sealed record WeaponFolderMapping(string SourcePath, string RelativePath);
public sealed record WeaponFolderFile(string SourcePath, string RelativePath, string Sha256, long Bytes, bool Identical, string? Conflict);
public sealed record WeaponFolderDependency(string Path, string Status, IReadOnlyList<string> Candidates);
public sealed record WeaponFolderPlan(WeaponFolderRequest Request, string ProjectSha256, IReadOnlyList<WeaponFolderFile> Files,
    string ThirdPersonPath, string? FirstPersonPath, IReadOnlyList<WeaponFolderDependency> Dependencies,
    IReadOnlyList<string> Warnings, int SkippedFiles, bool Incomplete)
{
    public IReadOnlyList<WeaponFolderItemPlan> Items { get; init; } = [];
    public bool CanImport => Files.Count > 0 && Files.All(f => f.Conflict == null) && (!Incomplete || Request.AllowIncomplete);
}
public sealed record WeaponFolderImport(Guid PlanId, string ThirdPersonPath, string? FirstPersonPath, bool Incomplete)
{ public IReadOnlyList<WeaponFolderItemPlan> Items { get; init; } = []; }
