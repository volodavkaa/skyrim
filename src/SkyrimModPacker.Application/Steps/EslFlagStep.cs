using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Application.Steps;

/// <summary>Optionally flags compatible staged plugins. Sources in the game directory are never written.</summary>
public sealed class EslFlagStep : IPackStep
{
    private readonly IEslService _eslService;
    private readonly ILogger<EslFlagStep>? _logger;

    public string Name => "Apply ESL Flag";
    public int Order => 450;

    public EslFlagStep(IEslService eslService, ILogger<EslFlagStep>? logger = null)
    {
        _eslService = eslService;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Profile.EnableEsl)
            return true;

        var completed = false;
        var ownsStagingDirectory = false;
        try
        {
            if (string.IsNullOrEmpty(context.StagingDirectory) || !Directory.Exists(context.StagingDirectory))
                throw new InvalidOperationException("ESL flagging requires a staging directory.");
            PackingPathGuard.EnsureStagingDirectory(context.StagingDirectory, context.Settings.SkyrimDataPath);
            ownsStagingDirectory = true;

            var plugins = context.SelectedFiles.Where(f => f.Category == FileCategory.Plugin).ToList();
            if (plugins.Count == 0)
                throw new InvalidOperationException("No plugin selected for ESL flagging.");

            var backupDirectory = context.OutputArchivePath + ".backups";
            var buildId = Guid.NewGuid().ToString("N");
            foreach (var plugin in plugins)
            {
                var stagedPath = PackingPathGuard.ResolveUnderRoot(context.StagingDirectory, plugin.RelativePath);

                var backupPath = Path.Combine(backupDirectory, buildId, Path.GetFileName(stagedPath) + ".bak");
                var result = await _eslService.SetFlagAsync(stagedPath, context.Settings.SkyrimDataPath, backupPath, cancellationToken);
                if (result.IsFailure)
                    throw new InvalidOperationException($"ESL: {plugin.RelativePath}: {result.Error}");

                var message = string.IsNullOrEmpty(result.Value)
                    ? $"ESL: {plugin.RelativePath} already flagged."
                    : $"ESL: {plugin.RelativePath} flagged; backup: {result.Value}";
                context.LogMessages.Add(message);
                _logger?.LogInformation("{Message}", message);
            }

            completed = true;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Errors.Add(ex.Message);
            _logger?.LogError(ex, "ESL packing step failed");
            return false;
        }
        finally
        {
            // ArchiveStep is not reached after failure or cancellation, so release our staged copies here.
            if (!completed && ownsStagingDirectory && !string.IsNullOrEmpty(context.StagingDirectory))
            {
                try
                {
                    PackingPathGuard.DeleteStagingDirectory(context.StagingDirectory, context.Settings.SkyrimDataPath);
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "Failed to clean ESL staging directory"); }
            }
        }
    }
}
