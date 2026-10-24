using NSubstitute;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task Stage3_CancelDuringBackup_PreventsPluginWrite_AndReleasesEditor()
    {
        var (source, _) = await PrepareGuardedEditorAsync();
        var real = new WorkspaceHistoryService(Path.Combine(_root, "cancel-history"));
        var history = Substitute.For<IWorkspaceHistoryService>();
        history.InspectAsync(Arg.Any<WorkspaceContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(c => real.InspectAsync(c.Arg<WorkspaceContext>(), c.Arg<bool>(), c.Arg<CancellationToken>()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        history.CaptureAsync(Arg.Any<WorkspaceContext>(), Arg.Any<bool>(), Arg.Any<IProgress<EditorOperationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async c => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, c.Arg<CancellationToken>()); return null!; });
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), workspaceHistory: history);
        await vm.InitializeCommand.ExecuteAsync(null); await vm.OpenProjectCommand.ExecuteAsync(source.ProjectFilePath);
        var bytes = File.ReadAllBytes(vm.PluginPath);
        var saving = vm.CloneSwordCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsLoading); vm.CancelOperationCommand.Execute(null);
        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.IsLoading); Assert.Null(vm.ErrorMessage); Assert.Null(vm.OperationProgress);
        Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath)); Assert.NotNull(vm.StatusMessage);
    }

    [Fact]
    public async Task Stage3_MovedProject_RebindsExistingProfilePreservingSettings_AndOpensCurrentFolder()
    {
        var (source, _) = await PrepareGuardedEditorAsync();
        var profile = new ModProfile { Name = "Existing", Version = "2.7", PluginFileName = Path.GetFileName(source.PluginPath),
            PluginSourcePath = source.PluginPath, WeaponProjectPath = source.ProjectFilePath, EnableEsl = true, Keywords = ["preserved"] };
        await _profiles.SaveAsync(profile);
        var moved = Path.Combine(_root, "moved-workspace"); Directory.Move(Path.GetDirectoryName(source.PluginPath)!, moved);
        var opener = Substitute.For<IWorkspaceFolderOpener>();
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), folderOpener: opener);
        await vm.InitializeCommand.ExecuteAsync(null);
        await vm.OpenProjectCommand.ExecuteAsync(Path.Combine(moved, "mod-project.json"));
        Assert.Null(vm.ErrorMessage); Assert.Equal(Path.Combine(moved, Path.GetFileName(source.PluginPath)), vm.PluginPath);
        vm.SelectedPackingProfile = Assert.Single(vm.PackingProfiles);
        await vm.LinkPackingProfileCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var updated = Assert.Single(await _profiles.LoadAllAsync());
        Assert.Equal(profile.Id, updated.Id); Assert.Equal(vm.PluginPath, updated.PluginSourcePath); Assert.Equal(vm.ProjectFilePath, updated.WeaponProjectPath);
        Assert.Equal("2.7", updated.Version); Assert.Equal("preserved", Assert.Single(updated.Keywords)); Assert.True(updated.EnableEsl);
        vm.OpenProjectFolderCommand.Execute(null); opener.Received(1).Open(moved);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Stage3_OpenMissingProjectFolder_ReportsErrorWithoutLaunchingShell()
    {
        var opener = Substitute.For<IWorkspaceFolderOpener>();
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), folderOpener: opener)
            { ProjectFilePath = Path.Combine(_root, "missing", "mod-project.json") };
        vm.OpenProjectFolderCommand.Execute(null);
        Assert.NotNull(vm.ErrorMessage); opener.DidNotReceiveWithAnyArgs().Open(default!);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Stage3_ResourceCopyUndoRedo_AndReplacementDeletionVersions_StayConsistent()
    {
        var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        var source = Path.Combine(_root, "versioned.nif"); File.WriteAllText(source, "first");
        var oldPath = vm.ModelPath;
        await vm.ReplaceThirdPersonModelCommand.ExecuteAsync(source);
        Assert.Null(vm.ErrorMessage); Assert.NotEqual(oldPath, vm.ModelPath); var path = vm.ModelPath;
        await vm.UndoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Equal(oldPath, vm.ModelPath);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Equal(path, vm.ModelPath);
        vm.SelectedResource = Assert.Single(vm.ResourceCatalog, r => r.RelativePath.EndsWith("versioned.nif"));
        var target = Path.Combine(Path.GetDirectoryName(vm.PluginPath)!, "meshes", path);
        File.WriteAllText(source, "second");
        await vm.ReplaceResourceCommand.ExecuteAsync(source);
        Assert.Null(vm.ErrorMessage); Assert.Equal("second", File.ReadAllText(target)); Assert.Single(vm.ResourceVersions);
        await vm.UndoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Equal("first", File.ReadAllText(target)); Assert.Empty(vm.ResourceVersions);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Equal("second", File.ReadAllText(target)); Assert.Single(vm.ResourceVersions);
        vm.SelectedResource = vm.ResourceCatalog.Single(r => r.RelativePath.EndsWith("versioned.nif"));
        await vm.DeleteResourceCommand.ExecuteAsync(null); Assert.NotNull(vm.ErrorMessage); Assert.True(File.Exists(target));
        vm.ModelPath = oldPath; // Explicitly unassign the unsaved model before deleting it.
        await vm.DeleteResourceCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.False(File.Exists(target));
        await vm.RestoreResourceVersionCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.True(File.Exists(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage3_UsedModelVersions_AssignOnlySelectedView_AndSurviveReopen(bool first)
    {
        var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        var source = Path.Combine(_root, "assigned.nif"); File.WriteAllText(source, "original model");
        var replace = first ? vm.ReplaceFirstPersonModelCommand : vm.ReplaceThirdPersonModelCommand;
        await replace.ExecuteAsync(source); Assert.Null(vm.ErrorMessage);
        var originalPath = first ? vm.FirstPersonModelPath : vm.ModelPath;
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var otherView = first ? vm.ModelPath : vm.FirstPersonModelPath;
        File.WriteAllText(source, "new model");
        await replace.ExecuteAsync(source); Assert.Null(vm.ErrorMessage);
        var version = Assert.Single(vm.ResourceVersions);
        Assert.Equal("meshes/" + originalPath.Replace('\\', '/'), version.RelativePath);
        await vm.SaveSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var projectPath = vm.ProjectFilePath;
        await vm.OpenProjectCommand.ExecuteAsync(projectPath); Assert.Null(vm.ErrorMessage);
        vm.SelectedResourceVersion = vm.ResourceVersions.Single(v => v.Id == version.Id);
        await (first ? vm.RestoreFirstPersonVersionCommand : vm.RestoreThirdPersonVersionCommand).ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(otherView, first ? vm.ModelPath : vm.FirstPersonModelPath);
        var restored = first ? vm.FirstPersonModelPath : vm.ModelPath;
        Assert.Equal("original model", File.ReadAllText(Path.Combine(Path.GetDirectoryName(vm.PluginPath)!, "meshes", restored)));
        await vm.SaveSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var models = (await _builder.GetWeaponModelsAsync(vm.PluginPath, DataPath, vm.SelectedWeapon!.FormId)).Value;
        Assert.Equal(restored, first ? models.FirstPersonPath : models.ThirdPersonPath);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("resource")]
    public async Task Stage3_ExternalMetadataOrResourceChange_RequiresReviewBeforeWriting(string changed)
    {
        var (vm, _, _, prompt) = await PrepareHistoryEditorAsync();
        var source = Path.Combine(_root, "changed.nif"); File.WriteAllText(source, "initial");
        await vm.ImportModelCommand.ExecuteAsync(source); Assert.Null(vm.ErrorMessage);
        var path = changed == "metadata" ? vm.ProjectFilePath : Path.Combine(Path.GetDirectoryName(vm.PluginPath)!, "meshes", vm.ModelPath);
        File.AppendAllText(path, changed == "metadata" ? " " : "external");
        var plugin = File.ReadAllBytes(vm.PluginPath); var external = File.ReadAllBytes(path);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath)); Assert.Equal(external, File.ReadAllBytes(path));
        await prompt.Received(1).ConfirmAsync(Arg.Any<string>());
    }
}
