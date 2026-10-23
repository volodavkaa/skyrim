using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty] private string _projectFilePath = string.Empty;

    [RelayCommand]
    private async Task OpenProjectAsync(string? path)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFileDialog { Title = Text("Weapons_OpenProject", "Open workspace mod"), Filter = "Weapon project (mod-project.json)|mod-project.json" };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        var selectedPath = path;
        await RunOperationAsync(async token =>
        {
            GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
            var result = await _projects.OpenAsync(selectedPath, GameDataPath, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
            await ActivateProjectAsync(result.Value, token);
            StatusMessage = Text("Weapons_ProjectOpened", "Workspace mod opened. Saved changes are loaded from its plugin.");
        });
    }

    [RelayCommand]
    private async Task ImportProjectAsync(string? path)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFileDialog { Title = Text("Weapons_ImportProject", "Create working copy"), Filter = "Skyrim plugins (*.esp;*.esl)|*.esp;*.esl" };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        var selectedPath = path;
        await RunOperationAsync(async token =>
        {
            GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
            var result = await _projects.ImportAsync(selectedPath, OutputDirectory.Trim(), GameDataPath, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
            await ActivateProjectAsync(result.Value, token);
            StatusMessage = Text("Weapons_ProjectImported", "Working copy created. The source plugin was not changed.");
        });
    }

    private async Task ActivateProjectAsync(WeaponProject project, CancellationToken token)
    {
        var result = await _builder.GetWeaponsAsync(project.PluginPath, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await LoadOptionsCoreAsync(token);
        ProjectFilePath = project.ProjectFilePath;
        PluginPath = project.PluginPath;
        ShowCreationPanel = false;
        Weapons = new ObservableCollection<WeaponRecord>(result.Value);
        SelectedWeapon = Weapons.FirstOrDefault();
        if (SelectedWeapon == null) ResetDraft();
        await RefreshProjectResourcesAsync(token);
    }
}
