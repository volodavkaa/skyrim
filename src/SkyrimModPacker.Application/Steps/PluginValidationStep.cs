using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Application.Steps;

public sealed class PluginValidationStep(IPluginValidationService validator, ILogger<PluginValidationStep>? logger = null) : IPackStep
{
    public string Name => "Validate plugins";
    public int Order => 350;

    public async Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default)
    {
        Version? runtime = null;
        var executable = Path.Combine(Path.GetDirectoryName(context.Settings.SkyrimDataPath) ?? "", "SkyrimSE.exe");
        if (File.Exists(executable))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(executable);
                if (info.FileMajorPart > 0) runtime = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            }
            catch (Exception ex) { logger?.LogWarning(ex, "Unable to identify target Skyrim runtime"); }
        }
        foreach (var plugin in context.SelectedFiles.Where(f => f.Category == FileCategory.Plugin))
        {
            var diagnostics = await validator.ValidateAsync(plugin.AbsolutePath, context.Settings.SkyrimDataPath,
                context.Profile.EnableEsl, runtime, cancellationToken);
            context.PluginDiagnostics.AddRange(diagnostics);
            foreach (var diagnostic in diagnostics)
            {
                var message = diagnostic.ToString();
                context.LogMessages.Add(message);
                if (diagnostic.Severity == DiagnosticSeverity.Error) { context.Errors.Add(message); logger?.LogError("{Diagnostic}", message); }
                else { context.Warnings.Add(message); logger?.LogWarning("{Diagnostic}", message); }
            }
        }
        return !context.PluginDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }
}
