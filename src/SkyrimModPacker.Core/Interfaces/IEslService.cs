using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.Esl;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>Analyzes plugins and applies the ESL flag only to files outside the game directory.</summary>
public interface IEslService
{
    Task<Result<EslAnalysisResult>> AnalyzeAsync(string pluginPath, CancellationToken cancellationToken = default);

    /// <summary>Returns the backup path, or an empty string when the flag was already set.</summary>
    Task<Result<string>> SetFlagAsync(string pluginPath, string gameDataPath, string? backupPath = null, CancellationToken cancellationToken = default);
}
