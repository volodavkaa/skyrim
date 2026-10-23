namespace SkyrimModPacker.Core.Models;

public enum DiagnosticSeverity { Error, Warning, Unsupported }

public sealed record PluginDiagnostic(string Code, DiagnosticSeverity Severity, string FilePath,
    string? Record, string Reason, string RecommendedAction)
{
    public override string ToString() => $"[{Severity}] {Code}: {FilePath}{(Record == null ? "" : " / " + Record)}: {Reason} {RecommendedAction}";
}
