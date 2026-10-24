using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private readonly IWorkspaceFolderOpener? _folderOpener;
    [ObservableProperty] private ProjectResourceRow? _selectedResource;
    [ObservableProperty] private ObservableCollection<ResourceVersion> _resourceVersions = [];
    [ObservableProperty] private ResourceVersion? _selectedResourceVersion;

    private async Task RefreshResourceVersionsAsync(CancellationToken token)
    {
        var versions = await _projects.GetResourceVersionsAsync(ProjectFilePath, GameDataPath, token);
        ResourceVersions = versions?.IsSuccess == true ? new(versions.Value) : [];
        SelectedResourceVersion = ResourceVersions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ReplaceResourceAsync(string? source)
    {
        if (IsLoading || SelectedResource == null) return;
        var relative = SelectedResource.RelativePath;
        if (string.IsNullOrWhiteSpace(source))
        {
            var extension = Path.GetExtension(relative);
            var dialog = new OpenFileDialog { Filter = $"Resource (*{extension})|*{extension}" };
            if (dialog.ShowDialog() != true) return;
            source = dialog.FileName;
        }
        await RunOperationAsync(async token =>
        {
            RequireProject();
            var result = await _projects.ReplaceResourceAsync(ProjectFilePath, relative, source, GameDataPath, token);
            if (result.IsFailure) throw new IOException(result.Error);
            await RefreshProjectResourcesAsync(token);
            ResourceStatus = Text("ResourceEdit_Replaced", "Resource replaced; its previous bytes are available in versions.");
        }, write: true, resourceWrite: true);
    }

    [RelayCommand]
    private Task DeleteResourceAsync() => RunOperationAsync(async token =>
    {
        RequireProject();
        if (SelectedResource == null) throw new IOException(Text("ResourceEdit_Select", "Select a resource."));
        static string MeshResource(string path) => (path.Replace('\\', '/').StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? path : "meshes/" + path).Replace('\\', '/');
        if (new[] { ModelPath, FirstPersonModelPath }.Any(p => !string.IsNullOrWhiteSpace(p) && MeshResource(p).Equals(SelectedResource.RelativePath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException(Text("Items_DraftResourceUsed", "This model is assigned in the editor. Change the assignment before deleting the resource."));
        var result = await _projects.DeleteResourceAsync(ProjectFilePath, SelectedResource.RelativePath, GameDataPath, token);
        if (result.IsFailure) throw new IOException(result.Error);
        await RefreshProjectResourcesAsync(token);
        ResourceStatus = Text("ResourceEdit_Deleted", "Unused resource deleted. Its backup can be restored from versions.");
    }, write: true, resourceWrite: true);

    [RelayCommand]
    private Task RestoreResourceVersionAsync() => RunOperationAsync(async token =>
    {
        RequireProject();
        if (SelectedResourceVersion == null) throw new IOException(Text("ResourceEdit_SelectVersion", "Select a version."));
        var result = await _projects.RestoreResourceVersionAsync(ProjectFilePath, SelectedResourceVersion.Id, GameDataPath, token);
        if (result.IsFailure) throw new IOException(result.Error);
        await RefreshProjectResourcesAsync(token);
        ResourceStatus = Text("ResourceEdit_Restored", "Resource version restored.");
    }, write: true, resourceWrite: true);

    [RelayCommand]
    private Task ReplaceThirdPersonModelAsync(string? path) => ReplaceAssignedModelAsync(path, false);
    [RelayCommand]
    private Task ReplaceFirstPersonModelAsync(string? path) => ReplaceAssignedModelAsync(path, true);
    [RelayCommand]
    private Task RestoreThirdPersonVersionAsync() => RestoreAssignedVersionAsync(false);
    [RelayCommand]
    private Task RestoreFirstPersonVersionAsync() => RestoreAssignedVersionAsync(true);

    private async Task BackupAssignedModelAsync(bool firstPerson, CancellationToken token)
    {
        var path = (firstPerson ? FirstPersonModelPath : ModelPath).Replace('\\', '/');
        if (!path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) path = "meshes/" + path;
        var resources = await _projects.GetResourcesAsync(ProjectFilePath, GameDataPath, token);
        if (resources.IsFailure) throw new IOException(resources.Error);
        if (!resources.Value.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        var backup = await _projects.BackupResourceAsync(ProjectFilePath, path, GameDataPath, token);
        if (backup.IsFailure) throw new IOException(backup.Error);
    }

    private Task RestoreAssignedVersionAsync(bool firstPerson) => RunOperationAsync(async token =>
    {
        RequireProject();
        if (SelectedResourceVersion == null) throw new IOException(Text("ResourceEdit_SelectVersion", "Select a version."));
        var selectedId = SelectedResourceVersion.Id;
        if (!SelectedResourceVersion.RelativePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) throw new IOException("Select a NIF version for a sword model.");
        await BackupAssignedModelAsync(firstPerson, token);
        var result = await _projects.ImportModelVersionCopyAsync(ProjectFilePath, selectedId, GameDataPath, token);
        if (result.IsFailure) throw new IOException(result.Error);
        AssignSeparateModel(result.Value, firstPerson);
        await RefreshProjectResourcesAsync(token);
        ResourceStatus = Text("ResourceEdit_ModelCopy", "A separate NIF was assigned to the editor. Save the sword to apply it.");
    }, write: true, resourceWrite: true);

    private void AssignSeparateModel(string path, bool firstPerson)
    {
        if (firstPerson) { FirstPersonModelPath = path; _firstPersonEdited = true; _restoreFirstPerson = false; }
        else ModelPath = path;
        IsWizardSaved = false; UpdateUnsavedChanges();
    }

    private async Task ReplaceAssignedModelAsync(string? source, bool firstPerson)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(source))
        {
            var dialog = new OpenFileDialog { Filter = "NIF (*.nif)|*.nif" };
            if (dialog.ShowDialog() != true) return;
            source = dialog.FileName;
        }
        await RunOperationAsync(async token =>
        {
            RequireProject();
            await BackupAssignedModelAsync(firstPerson, token);
            var result = await _projects.ImportModelCopyAsync(ProjectFilePath, source, GameDataPath, token);
            if (result.IsFailure) throw new IOException(result.Error);
            AssignSeparateModel(result.Value, firstPerson);
            await RefreshProjectResourcesAsync(token);
            ResourceStatus = Text("ResourceEdit_ModelCopy", "A separate NIF was assigned to the editor. Save the sword to apply it. Undo restores the previous assignment and resource state.");
        }, write: true, resourceWrite: true);
    }

    [RelayCommand]
    private void OpenProjectFolder()
    {
        try
        {
            RequireProject();
            var folder = Path.GetDirectoryName(Path.GetFullPath(ProjectFilePath))!;
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            if (_folderOpener == null) throw new InvalidOperationException("Folder opener is unavailable.");
            _folderOpener.Open(folder);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }
}
