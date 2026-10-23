using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Interfaces;

public interface IPluginValidationService
{
    Task<IReadOnlyList<PluginDiagnostic>> ValidateAsync(string pluginPath, string gameDataPath,
        bool requireEsl = false, Version? targetRuntime = null, CancellationToken cancellationToken = default);
}
