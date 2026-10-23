using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty] private ObservableCollection<string> _projectResources = [];
    [ObservableProperty] private string _textureSubdirectory = string.Empty;
    [ObservableProperty] private string? _resourceStatus;

    partial void OnModelPathChanged(string value) => ResourceStatus = null;

    partial void OnProjectFilePathChanged(string value)
    {
        ProjectResources = [];
        ResourceStatus = null;
        SelectedPackingProfile = null;
        EnableProjectEsl = false;
        LastProjectArchivePath = null;
    }

    private void RequireProject()
    {
        if (string.IsNullOrWhiteSpace(ProjectFilePath))
            throw new InvalidOperationException(Text("Weapons_RequireProject", "Create or open a workspace mod before importing resources."));
    }

    private async Task RefreshProjectResourcesAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(ProjectFilePath)) return;
        var result = await _projects.GetResourcesAsync(ProjectFilePath, GameDataPath, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        ProjectResources = new ObservableCollection<string>(result.Value);
    }

    [RelayCommand]
    private async Task ImportModelAsync(string? path)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFileDialog { Title = Text("Weapons_ImportModel", "Import NIF model"), Filter = "NIF model (*.nif)|*.nif" };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        var selectedPath = path;
        await RunOperationAsync(async token =>
        {
            RequireProject();
            GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
            var result = await _projects.ImportModelAsync(ProjectFilePath, selectedPath, GameDataPath, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
            ModelPath = result.Value;
            await RefreshProjectResourcesAsync(token);
            ResourceStatus = Text("Weapons_ModelImported", "NIF copied and assigned to the editor. Save the sword on Summary to write this path to the plugin.");
        });
    }

    [RelayCommand]
    private async Task ImportTexturesAsync(string[]? paths)
    {
        if (IsLoading) return;
        if (paths == null || paths.Length == 0)
        {
            var dialog = new OpenFileDialog { Title = Text("Weapons_ImportTextures", "Add DDS textures"), Filter = "DDS textures (*.dds)|*.dds", Multiselect = true };
            if (dialog.ShowDialog() != true) return;
            paths = dialog.FileNames;
        }
        await ImportTexturePathsAsync(paths);
    }

    [RelayCommand]
    private async Task ImportTextureFolderAsync(string? path)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFolderDialog { Title = Text("Weapons_ImportTextureFolder", "Add texture folder") };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FolderName;
        }
        await ImportTexturePathsAsync([path]);
    }

    private Task ImportTexturePathsAsync(string[] paths) => RunOperationAsync(async token =>
    {
        RequireProject();
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        var result = await _projects.ImportTexturesAsync(ProjectFilePath, paths, GameDataPath, TextureSubdirectory.Trim(), token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshProjectResourcesAsync(token);
        ResourceStatus = Text("Weapons_TexturesImported", "DDS textures added. Check the paths below against the paths used by your NIF.");
    });

    [RelayCommand]
    private Task CheckResourcesAsync() => RunOperationAsync(async token =>
    {
        RequireProject();
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        await RefreshProjectResourcesAsync(token);
        var assets = await _projects.ValidateResourcesAsync(ProjectFilePath, GameDataPath, token);
        if (assets.IsFailure) { ResourceStatus = null; throw new InvalidOperationException(assets.Error); }
        var model = await _projects.ValidateModelAsync(ProjectFilePath, ModelPath, GameDataPath, token);
        if (model.IsFailure) { ResourceStatus = null; throw new InvalidOperationException(model.Error); }
        ResourceStatus = Text("Weapons_ResourcesValid", "Model and added resources exist in the project. NIF contents and texture references have not been checked.");
    });

    private async Task EnsureImportedModelAvailableAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(ProjectFilePath)) return;
        var resources = await _projects.GetResourcesAsync(ProjectFilePath, GameDataPath, token);
        if (resources.IsFailure) throw new InvalidOperationException(resources.Error);
        var relative = ModelPath.Replace('\\', '/');
        if (!relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) relative = "meshes/" + relative;
        if (!resources.Value.Contains(relative, StringComparer.OrdinalIgnoreCase)) return;
        var result = await _projects.ValidateModelAsync(ProjectFilePath, ModelPath, GameDataPath, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
    }
}
