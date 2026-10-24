using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty] private ObservableCollection<string> _projectResources = [];
    [ObservableProperty] private string _textureSubdirectory = string.Empty;
    [ObservableProperty] private string? _resourceStatus;
    [ObservableProperty] private ObservableCollection<ProjectResourceRow> _resourceCatalog = [];
    [ObservableProperty] private string? _resourceCatalogError;
    [ObservableProperty] private string? _resourceCatalogDiagnostics;
    private ProjectResourceCatalog? _savedResourceCatalog;
    public IEnumerable<ProjectResourceRow> OwnResourceCatalog => ResourceCatalog.Where(r => r.Entry.Origin != ModelOrigin.Vanilla);
    public IEnumerable<ProjectResourceRow> VanillaResourceCatalog => ResourceCatalog.Where(r => r.Entry.Origin == ModelOrigin.Vanilla);
    partial void OnResourceCatalogChanged(ObservableCollection<ProjectResourceRow> value)
    {
        OnPropertyChanged(nameof(OwnResourceCatalog)); OnPropertyChanged(nameof(VanillaResourceCatalog));
    }

    partial void OnModelPathChanged(string value) { ResourceStatus = null; ThirdPersonModelStatus = null; }

    partial void OnGameDataPathChanged(string value)
    {
        _savedResourceCatalog = null;
        ResourceCatalog = [];
        ResourceCatalogError = ResourceCatalogDiagnostics = null;
    }

    partial void OnProjectFilePathChanged(string value)
    {
        _weaponOrigins.Clear(); _weaponRecipeCounts.Clear(); NotifyInheritance();
        DraftFilePath = DraftSaveStatus = DraftSaveError = null;
        ProjectResources = [];
        ResourceCatalog = [];
        _savedResourceCatalog = null;
        ResourceCatalogError = ResourceCatalogDiagnostics = null;
        ResourceStatus = null;
        SelectedResource = null;
        ResourceVersions = [];
        SelectedResourceVersion = null;
        Variants = []; VariantError = null;
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
        await RefreshResourceCatalogCoreAsync(token);
    }

    [RelayCommand]
    private Task RefreshResourceCatalogAsync() => RunOperationAsync(async token =>
    {
        RequireProject();
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        await RefreshResourceCatalogCoreAsync(token);
    });

    private async Task RefreshResourceCatalogCoreAsync(CancellationToken token)
    {
        // Never leave a previous project's or a stale successful preview on screen after an error.
        ResourceCatalogError = ResourceCatalogDiagnostics = null;
        ResourceCatalog = [];
        _savedResourceCatalog = null;
        var result = await _projects.GetResourceCatalogAsync(ProjectFilePath, GameDataPath, token);
        if (result.IsFailure) { ResourceCatalogError = result.Error; return; }
        _savedResourceCatalog = result.Value;
        ProjectResources = new(result.Value.Entries.Where(e => e.IsRegistered).Select(e => e.RelativePath));
        RenderResourceCatalog();
        await RefreshResourceVersionsAsync(token);
        await RefreshVariantsAsync(token);
    }

    private void RenderResourceCatalog()
    {
        if (_savedResourceCatalog == null) return;
        ResourceCatalog = new(_savedResourceCatalog.Entries.Select(entry => new ProjectResourceRow(entry, Text)));
        ResourceCatalogDiagnostics = _savedResourceCatalog.Diagnostics.Count == 0 ? null :
            string.Join(Environment.NewLine, _savedResourceCatalog.Diagnostics.Select(d => d.Record + ": " + d.Detail));
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
        }, write: true, resourceWrite: true);
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
    }, write: true, resourceWrite: true);

    [RelayCommand]
    private Task CheckResourcesAsync() => RunOperationAsync(async token =>
    {
        RequireProject();
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        await RefreshProjectResourcesAsync(token);
        var assets = await _projects.ValidateResourcesAsync(ProjectFilePath, GameDataPath, token);
        if (assets.IsFailure) { ResourceStatus = null; throw new InvalidOperationException(assets.Error); }
        await InspectDraftModelsAsync(token);
        await EnsureFirstPersonAvailableAsync(token);
        ResourceStatus = Text("Models_Checked", "Model checks completed. See each model's status. NIF contents and texture references have not been checked.");
    });

    private async Task EnsureImportedModelAvailableAsync(CancellationToken token)
    {
        await EnsureFirstPersonAvailableAsync(token);
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
