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
    /// <param name="fileSelection">Optional preview selection. New files are excluded and missing selected files fail the build.</param>
    /// <returns>The completed packing context with results.</returns>
    public async Task<PackingContext> ExecuteAsync(
        ModProfile profile,
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ScannedFile>? fileSelection = null)
    {
        var context = new PackingContext
        {
            Profile = profile,
            Settings = settings,
            Progress = progress,
            // Detach the selection from the UI before the first asynchronous step.
            FileSelection = fileSelection?.Select(file => new ScannedFile
            {
                AbsolutePath = file.AbsolutePath,
                RelativePath = file.RelativePath,
                SizeBytes = file.SizeBytes,
                Category = file.Category,
                IsSelected = file.IsSelected
            }).ToList()
        };

        string? activeStep = null;
        context.Status = PackingStatus.Running;
        try
        {
            var stepList = _steps.ToList();
            for (int i = 0; i < stepList.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = stepList[i];
                activeStep = step.Name;
                _logger.LogInformation("Executing step {StepNumber}/{TotalSteps}: {StepName}",
                    i + 1, stepList.Count, step.Name);

                context.LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] Starting: {step.Name}");

                var success = await step.ExecuteAsync(context, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (!success || context.Errors.Count > 0)
                {
                    if (context.Errors.Count == 0) context.Errors.Add($"Packing step failed: {step.Name}.");
                    context.Status = PackingStatus.Failed;
                    _logger.LogWarning("Step {StepName} failed. Aborting pipeline.", step.Name);
                    context.LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] FAILED: {step.Name}");
                    return context;
                }

                context.LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] Completed: {step.Name}");

                // Report overall progress based on step completion
                progress?.Report((double)(i + 1) / stepList.Count);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!context.ArchiveCreated || string.IsNullOrEmpty(context.OutputArchivePath) || !File.Exists(context.OutputArchivePath))
            {
                context.Status = PackingStatus.Failed;
                context.Errors.Add("Packing did not complete a new archive.");
                return context;
            }

            context.Status = PackingStatus.Succeeded;
            _logger.LogInformation("Packing pipeline completed successfully.");
            return context;
        }
        catch (OperationCanceledException)
        {
            context.Status = PackingStatus.Cancelled;
            context.LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] Packing cancelled.");
            _logger.LogInformation("Packing cancelled during or before {StepName}", activeStep);
            throw;
        }
        catch (Exception ex)
        {
            context.Status = PackingStatus.Failed;
            context.Errors.Add($"Packing failed during {activeStep ?? "initialization"}: {ex.Message}");
            _logger.LogError(ex, "Packing failed during {StepName}", activeStep);
            throw;
        }
        finally
        {
            if (!context.TryCleanupStaging())
                _logger.LogWarning("{Warning}", context.Warnings.Last());
        }
    }
}
