namespace SkyrimModPacker.Core.Models.Esl;

/// <summary>ESL compatibility information for display and packing diagnostics.</summary>
public sealed class EslAnalysisResult
{
    public bool IsCompatible { get; init; }
    public float HeaderVersion { get; init; }
    public bool IsAlreadyEsl { get; init; }
    public int OwnRecordCount { get; init; }
    public int ProblematicRecordCount { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
}
