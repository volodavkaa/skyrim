using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>
/// A single step in the packing pipeline.
/// Steps are executed sequentially: Scan → Filter → Validate → Copy → Archive.
/// New functionality is added by implementing new steps without modifying existing ones.
/// </summary>
public interface IPackStep
{
    /// <summary>Display name for logging/UI.</summary>
    string Name { get; }

    /// <summary>Execution order (lower = earlier).</summary>
    int Order { get; }

    /// <summary>
    /// Executes this pipeline step.
    /// </summary>
    /// <param name="context">Shared pipeline context containing all state.</param>
    /// <param name="cancellationToken">Token to support cancellation.</param>
    /// <returns>True if the step succeeded and the pipeline should continue.</returns>
    Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default);
}
