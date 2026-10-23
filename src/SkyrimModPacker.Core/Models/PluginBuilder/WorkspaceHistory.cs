namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record WorkspaceContext(string PluginPath, string ProjectFilePath, string GameDataPath);
public sealed record WorkspaceRevision(WorkspaceContext Context, bool IncludesResources, IReadOnlyDictionary<string, string?> Files)
{
    public bool Matches(WorkspaceRevision other) => Context == other.Context && IncludesResources == other.IncludesResources && Files.Count == other.Files.Count &&
        Files.All(p => other.Files.TryGetValue(p.Key, out var hash) && hash == p.Value);
}
public sealed record WorkspaceCheckpoint(Guid Id, WorkspaceRevision Revision);
public sealed record EditorOperationProgress(int Completed, int Total, string Path);
public sealed record DraftRecoveryCandidate(string FilePath, WeaponEditorDraft Draft)
{
    public string DisplayName => $"{Draft.SavedAtUtc.LocalDateTime:g} — {Draft.PluginPath} — {Draft.Weapon.Name}";
}
public sealed record DraftRecoveryScan(IReadOnlyList<DraftRecoveryCandidate> Candidates, IReadOnlyList<string> Errors);
