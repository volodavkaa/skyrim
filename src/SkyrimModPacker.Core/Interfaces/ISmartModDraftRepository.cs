using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Core.Interfaces;

public interface ISmartModDraftRepository
{
    Task<Result<SmartModDraft?>> LoadAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    // Reading a backup never replaces the primary file or edits the plugin.
    Task<Result<SmartModDraft?>> LoadBackupAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<SmartModDraft>> RestoreBackupAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<DraftWriteReceipt>> SaveAsync(SmartModDraft draft, string gameDataPath, long expectedRevision, CancellationToken cancellationToken = default);
}
