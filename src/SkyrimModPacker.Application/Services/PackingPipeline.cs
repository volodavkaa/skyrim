using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using Microsoft.Extensions.Logging;

namespace SkyrimModPacker.Application.Services;

/// <summary>
/// Orchestrates the packing pipeline by executing all registered <see cref="IPackStep"/>
/// instances in order.
/// </summary>
public sealed class PackingPipeline
{
    private readonly IEnumerable<IPackStep> _steps;
    private readonly ILogger<PackingPipeline> _logger;

    public PackingPipeline(IEnumerable<IPackStep> steps, ILogger<PackingPipeline> logger)
    {
        _steps = steps.OrderBy(s => s.Order);
        _logger = logger;
    }

    /// <summary>
    /// Executes all pipeline steps sequentially.
    /// </summary>
    /// <param name="profile">The mod profile to pack.</param>
    /// <param name="settings">Application settings.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Token to support cancellation.</param>
    /// <returns>The completed packing context with results.</returns>
    public async Task<PackingContext> ExecuteAsync(
        ModProfile profile,
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var context = new PackingContext
        {
            Profile = profile,
            Settings = settings,
            Progress = progress
        };

        var stepList = _steps.ToList();
        for (int i = 0; i < stepList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = stepList[i];
            _logger.LogInformation("Executing step {StepNumber}/{TotalSteps}: {StepName}",
                i + 1, stepList.Count, step.Name);

            context.LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] Starting: {step.Name}");

            var success = await step.ExecuteAsync(context, cancellationToken);

            if (!success)
            {
                _logger.LogWarning("Step {StepName} failed. Aborting pipeline.", step.Name);
                context.LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] FAILED: {step.Name}");
                return context;
            }

            context.LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] Completed: {step.Name}");

            // Report overall progress based on step completion
            progress?.Report((double)(i + 1) / stepList.Count);
        }

        _logger.LogInformation("Packing pipeline completed successfully.");
        return context;
    }
}
