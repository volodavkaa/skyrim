using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed class BatchImportRow : ObservableObject
{
    public required BatchResourceItem Item { get; init; }
    public string SourcePath => Item.SourcePath;
    public string RelativePath => Item.RelativePath;
    public string? Conflict { get; init; }
    private ModelAssignment _assignment;
    public ModelAssignment Assignment { get => _assignment; set => SetProperty(ref _assignment, value); }
    public required IReadOnlyList<BatchAssignmentChoice> Assignments { get; init; }
}
public sealed record BatchAssignmentChoice(ModelAssignment Value, string Label);

public sealed partial class WeaponsViewModel
{
    private BatchResourcePlan? _batchPlan;
    [ObservableProperty] private ObservableCollection<BatchImportRow> _batchItems = [];
    [ObservableProperty] private string? _batchStatus;

    [RelayCommand]
    private async Task PreviewBatchAsync(string[]? paths)
    {
        if (IsLoading) return;
        if (paths == null)
        {
            var dialog = new OpenFileDialog { Multiselect = true, Filter = "NIF / DDS|*.nif;*.dds" };
            if (dialog.ShowDialog() != true) return;
            paths = dialog.FileNames;
        }
        await RunOperationAsync(async token =>
        {
            _batchPlan = null; BatchItems = []; BatchStatus = null; RequireProject();
            var result = await _projects.PreviewBatchImportAsync(ProjectFilePath, paths, GameDataPath, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
            _batchPlan = result.Value;
            BatchItems = RenderBatchRows(result.Value.Items);
            BatchStatus = Text("Stage5_BatchPreviewHint", "Choose assignments. ResourceOnly imports without assigning a model. Conflicts block the entire batch.");
        });
    }

    private ObservableCollection<BatchImportRow> RenderBatchRows(IEnumerable<BatchResourceItem> items)
    {
        var assignments = new BatchAssignmentChoice[] {
                new(ModelAssignment.ResourceOnly, Text("Stage5_ResourceOnly", "Resource only")),
                new(ModelAssignment.ThirdPerson, Text("Models_Third", "Third-person model")),
                new(ModelAssignment.FirstPerson, Text("Models_First", "First-person model")) };
        return new(items.Select(i => new BatchImportRow { Item = i, Assignment = i.Assignment,
                Assignments = i.SourcePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ? assignments : assignments.Take(1).ToArray(),
                Conflict = i.Conflict == null ? null : Text("Stage5_BatchConflict", "Destination already exists or is duplicated in this batch.") }));
    }

    private void RefreshBatchLabels() => BatchItems = RenderBatchRows(BatchItems.Select(r => r.Item with { Assignment = r.Assignment }).ToArray());

    [RelayCommand]
    private async Task ImportBatchAsync()
    {
        if (IsLoading || _batchPlan == null) return;
        var plan = _batchPlan with { Items = BatchItems.Select(r => r.Item with { Assignment = r.Assignment }).ToArray() };
        if (!plan.ProjectFilePath.Equals(ProjectFilePath, StringComparison.OrdinalIgnoreCase))
        { BatchStatus = Text("Stage5_BatchStale", "The workspace changed. Preview the batch again."); return; }
        BatchStatus = Text("Stage5_BatchWorking", "Importing the complete batch…");
        var committed = false;
        await RunOperationAsync(async token =>
        {
            var result = await _projects.ImportBatchAsync(plan, GameDataPath, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
            committed = true;
            foreach (var item in plan.Items)
            {
                if (item.Assignment == ModelAssignment.ThirdPerson) ModelPath = item.RelativePath[7..].Replace('/', '\\');
                if (item.Assignment == ModelAssignment.FirstPerson) FirstPersonModelPath = item.RelativePath[7..].Replace('/', '\\');
            }
            // The transaction is committed; a late cancellation must not imply rollback.
            _batchPlan = null;
            await RefreshProjectResourcesAsync(CancellationToken.None);
        }, write: true, resourceWrite: true);
        BatchStatus = committed ? Text("Stage5_BatchDone", "All files imported. Model assignments are draft changes; save the item to confirm them.")
            : Text("Stage5_BatchFailed", "The batch was not committed. Review the error and preview again before retrying.") + Environment.NewLine + ErrorMessage;
    }

    [RelayCommand]
    private void ClearBatch()
    {
        if (IsLoading) return;
        _batchPlan = null; BatchItems = []; BatchStatus = null;
    }
}
