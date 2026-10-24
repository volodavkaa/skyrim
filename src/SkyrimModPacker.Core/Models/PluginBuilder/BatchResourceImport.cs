namespace SkyrimModPacker.Core.Models.PluginBuilder;

public enum ModelAssignment { ResourceOnly, ThirdPerson, FirstPerson }
public sealed record BatchResourceItem(string SourcePath, string RelativePath, string Sha256, ModelAssignment Assignment, string? Conflict = null);
public sealed record BatchResourcePlan(Guid Id, string ProjectFilePath, string ProjectSha256, IReadOnlyList<BatchResourceItem> Items);
