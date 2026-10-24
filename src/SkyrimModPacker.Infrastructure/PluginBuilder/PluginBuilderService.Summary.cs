using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    public Task<Result<IReadOnlyList<string>>> GetMastersAsync(string pluginPath, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<string>>(() =>
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
            return mod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToArray();
        }, cancellationToken);
}
