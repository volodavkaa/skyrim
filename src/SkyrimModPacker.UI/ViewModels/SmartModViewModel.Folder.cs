using System.IO;
using System.Text.Json;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    public bool CanStartFromResources => !IsBusy && Types.Count > 0 && !string.IsNullOrWhiteSpace(Name) && Path.GetFileName(Name.Trim()) == Name.Trim() && Name.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Path.IsPathFullyQualified(OutputDirectory.Trim());
    public async Task<WeaponArchiveSource?> ExtractWeaponArchiveAsync(string path)
    {
        WeaponArchiveSource? result = null;
        await RunAsync(async token => { try { result = await WeaponArchiveSource.OpenAsync(path, token); } catch (OperationCanceledException) { throw; } catch (Exception ex) { throw new IOException(Path.GetFileName(path) + ": " + ex.Message, ex); } }); return result;
    }
    public async Task<WeaponFolderScan> ScanWeaponFolderAsync(string folder, CancellationToken cancellationToken = default)
    { using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken); return Require(await _projects.ScanWeaponFolderAsync(folder, linked.Token)); }
    public async Task<WeaponFolderPlan> PreviewWeaponFolderAsync(WeaponFolderRequest request, CancellationToken cancellationToken = default)
    { using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken); return Require(await _projects.PreviewWeaponFolderAsync(request, _gameData, linked.Token)); }
    public Task ImportWeaponFolderAsync(WeaponFolderPlan plan, bool createNew, WeaponKind kind) => RunAsync(async token =>
        await ImportWeaponFolderCoreAsync(plan, createNew, kind, token));
    public string ProposedProjectPath => Path.Combine(OutputDirectory.Trim(), Name.Trim(), "mod-project.json");
    public Task CreateFromWeaponFolderAsync(WeaponFolderPlan plan, WeaponKind kind) => RunAsync(async token =>
    {
        if (HasProject || plan.Request.NewProjectPluginName != Name.Trim() + ".esp" || !Path.GetFullPath(plan.Request.ProjectPath).Equals(Path.GetFullPath(ProposedProjectPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException(Label("Smart_TuningStale"));
        var fresh = Require(await _projects.PreviewWeaponFolderAsync(plan.Request, _gameData, token));
        if (!fresh.CanImport || JsonSerializer.Serialize(fresh) != JsonSerializer.Serialize(plan)) throw new IOException(Label("Smart_TuningStale"));
        await CreateProjectCoreAsync(true, token);
        // Recheck against the newly created empty workspace; never create a placeholder weapon.
        var actual = Require(await _projects.PreviewWeaponFolderAsync(plan.Request with { NewProjectPluginName = null }, _gameData, token));
        if (JsonSerializer.Serialize(plan with { Request = actual.Request, ProjectSha256 = actual.ProjectSha256 }) != JsonSerializer.Serialize(actual)) throw new IOException(Label("Smart_TuningStale"));
        await ImportWeaponFolderCoreAsync(actual, true, kind, token);
    });
    private async Task ImportWeaponFolderCoreAsync(WeaponFolderPlan plan, bool createNew, WeaponKind kind, CancellationToken token)
    {
        if (!HasProject || !Path.GetFullPath(plan.Request.ProjectPath).Equals(Path.GetFullPath(ProjectPath), StringComparison.OrdinalIgnoreCase)) throw new IOException(Label("Smart_TuningStale"));
        if (Items.Any(i => i.Capture().LastFolderPlanId == plan.Request.Id)) { Status = Label("Smart_FolderAlreadyImported"); return; }
        if (!createNew && SelectedItem == null) throw new IOException(Label("Smart_SelectTemplate"));
        var before = Capture(); var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        var target = SelectedItem;
        try
        {
            var result = await _projects.ImportWeaponFolderAsync(plan, _gameData, async (imported, ct) =>
            {
                _applying = true;
                try
                {
                    if (createNew) { target = AddItemCore(kind); await ResolveFirstPersonAsync(target, ct); }
                    var item = target ?? throw new IOException(Label("Smart_SelectTemplate"));
                    item.Replace(item.Capture() with { LastFolderPlanId = imported.PlanId, ValidateFolderDependencies = true,
                        State = imported.Incomplete ? SmartModItemState.NeedsCorrection : SmartModItemState.InProgress,
                        Weapon = item.Fields with { ModelPath = imported.ThirdPersonPath,
                            FirstPersonPath = imported.FirstPersonPath ?? item.FirstPersonPath, FirstPersonEdited = imported.FirstPersonPath != null || item.Fields.FirstPersonEdited,
                            UseSharedModel = imported.FirstPersonPath != null && imported.FirstPersonPath.Equals(imported.ThirdPersonPath, StringComparison.OrdinalIgnoreCase),
                            RestoreFirstPerson = imported.FirstPersonPath == null && item.Fields.RestoreFirstPerson } });
                    SelectedItem = item; SelectedRecipe = item.Recipes.FirstOrDefault(); Section = SmartModSection.Weapons; item.Step = SmartModItemStep.Appearance;
                    _tuningUndo.Remove(item.Id); await RecordSuccessfulWriteAsync(ct);
                }
                finally { _applying = false; }
            }, token);
            if (result.IsFailure)
            {
                if (result.Error.Contains("RollbackFailure", StringComparison.OrdinalIgnoreCase)) _writeBlocked = true;
                throw new IOException(result.Error);
            }
        }
        catch { _applying = false; ApplyDocument(before with { Revision = _document.Revision }); throw; }
        await RefreshResourcesAsync(token); PreviewChanged?.Invoke(); NotifyStep(); Status = Label("Smart_FolderImported");
    }
}
