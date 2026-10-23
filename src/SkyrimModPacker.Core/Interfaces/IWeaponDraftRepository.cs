using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Core.Interfaces;

public interface IWeaponDraftRepository
{
    Task<Result<DraftRecoveryScan>> FindPendingAsync(string pluginPath, string gameDataPath, Guid excludedSession, CancellationToken cancellationToken = default);
    Task<Result<DraftWriteReceipt>> SaveAsync(WeaponEditorDraft draft, long expectedRevision, CancellationToken cancellationToken = default);
}
