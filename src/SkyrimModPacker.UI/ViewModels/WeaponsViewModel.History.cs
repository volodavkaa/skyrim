using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private sealed record EditorMemento(WeaponEditorDraft Snapshot, WeaponDraftState? WeaponBaseline, string? RecipeBaseline,
        bool RecoveredWeapon, bool RecoveredRecipe);
    private sealed record EditorHistoryEntry(EditorMemento Before, EditorMemento After,
        WorkspaceCheckpoint? BeforeFiles = null, WorkspaceCheckpoint? AfterFiles = null);
    private readonly List<EditorHistoryEntry> _undo = [], _redo = [];
    private EditorMemento? _lastEditorMemento;
    private long _historyEpoch;
    private bool _replayingHistory;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    private EditorMemento CaptureMemento() => new(CaptureEditorDraft(), _savedWeaponDraft, _savedRecipeDraft, _recoveredWeaponPending, _recoveredRecipePending);
    private static string MemoryKey(EditorMemento m) => JsonSerializer.Serialize(new { m.Snapshot.Weapon, m.Snapshot.Recipe,
        m.Snapshot.NewWeapon, m.Snapshot.NewRecipe, m.Snapshot.WeaponFormId, m.Snapshot.RecipeFormId, m.RecoveredWeapon, m.RecoveredRecipe });

    private void TrackEditorHistory()
    {
        if (_applyingEditorState) return;
        var current = CaptureMemento();
        if (!IsEditorBusy && _lastEditorMemento != null && MemoryKey(_lastEditorMemento) != MemoryKey(current))
            PushEditorHistory(new(_lastEditorMemento, current));
        _lastEditorMemento = current;
    }

    private void PushEditorHistory(EditorHistoryEntry entry)
    {
        _undo.Add(entry); if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear(); UpdateHistoryButtons();
    }
    private void UpdateHistoryButtons() { CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0; }
    private void ResetEditorHistory() { _historyEpoch++; _undo.Clear(); _redo.Clear(); _lastEditorMemento = CaptureMemento(); UpdateHistoryButtons(); }

    private async Task ApplyMementoAsync(EditorMemento memento, CancellationToken token)
    {
        _applyingEditorState = true;
        try
        {
            await ApplyEditorSnapshotAsync(memento.Snapshot, token, useCachedRecipe: true);
            _savedWeaponDraft = memento.WeaponBaseline; _savedRecipeDraft = memento.RecipeBaseline;
            _recoveredWeaponPending = memento.RecoveredWeapon; _recoveredRecipePending = memento.RecoveredRecipe;
            UpdateUnsavedChanges();
        }
        finally { _applyingEditorState = false; _lastEditorMemento = CaptureMemento(); }
    }

    [RelayCommand]
    private Task UndoAsync() => ChangeHistoryAsync(redo: false);
    [RelayCommand]
    private Task RedoAsync() => ChangeHistoryAsync(redo: true);
    private Task ChangeHistoryAsync(bool redo) => RunOperationAsync(async token =>
    {
        var source = redo ? _redo : _undo; var destination = redo ? _undo : _redo;
        if (source.Count == 0) return;
        _replayingHistory = true;
        var entry = source[^1];
        var files = redo ? entry.AfterFiles : entry.BeforeFiles;
        var expected = redo ? entry.BeforeFiles : entry.AfterFiles;
        var previous = CaptureMemento();
        var restored = false;
        try
        {
            if (files != null)
            {
                if (_workspaceHistory == null || expected == null) throw new IOException("File history is not available.");
                await _workspaceHistory.RestoreAsync(files, expected, EditorProgress, token);
                restored = true;
                var selected = (redo ? entry.After : entry.Before).Snapshot.WeaponFormId;
                await RefreshWeaponsAsync(selected, token);
                _confirmedRevision = await _workspaceHistory.InspectAsync(Workspace, true, token);
                _requireExternalReview = false;
            }
            await ApplyMementoAsync(redo ? entry.After : entry.Before, token);
        }
        catch (Exception original)
        {
            if (restored)
            {
                try
                {
                    await _workspaceHistory!.RestoreAsync(expected!, files!, EditorProgress, CancellationToken.None);
                    await RefreshWeaponsAsync(previous.Snapshot.WeaponFormId, CancellationToken.None);
                    _confirmedRevision = await _workspaceHistory.InspectAsync(Workspace, true, CancellationToken.None);
                }
                catch (Exception rollback) { original.Data["HistoryFileRollback"] = rollback.Message; }
            }
            try { await ApplyMementoAsync(previous, CancellationToken.None); }
            catch (Exception rollback) { original.Data["HistoryEditorRollback"] = rollback.Message; }
            throw;
        }
        source.RemoveAt(source.Count - 1); destination.Add(entry); UpdateHistoryButtons();
        StatusMessage = Text(redo ? "History_Redone" : "History_Undone", redo ? "Change redone." : "Change undone.");
    });
}
