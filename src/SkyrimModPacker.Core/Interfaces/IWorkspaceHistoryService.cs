using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Core.Interfaces;

public interface IWorkspaceHistoryService
{
    Task<WorkspaceRevision> InspectAsync(WorkspaceContext context, bool includeResources = true, CancellationToken token = default);
    Task<WorkspaceCheckpoint> CaptureAsync(WorkspaceContext context, bool includeResources, IProgress<EditorOperationProgress>? progress = null, CancellationToken token = default);
    Task RestoreAsync(WorkspaceCheckpoint desired, WorkspaceCheckpoint expected, IProgress<EditorOperationProgress>? progress = null, CancellationToken token = default);
}
