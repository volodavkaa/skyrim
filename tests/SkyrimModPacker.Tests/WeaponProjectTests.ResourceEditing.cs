using System.Text.Json;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Core.Common;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    private async Task<(WeaponProject Project, string Relative, string Target)> EditableResourceAsync(string extension)
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var source = Path.Combine(_root, "initial" + extension); File.WriteAllText(source, "original");
        var relative = extension == ".nif" ? "meshes/" + (await Service.ImportModelAsync(project.ProjectFilePath, source, Data)).Value.Replace('\\', '/') :
            Assert.Single((await Service.ImportTexturesAsync(project.ProjectFilePath, [source], Data)).Value);
        return (project, relative, Path.Combine(Path.GetDirectoryName(project.PluginPath)!, relative));
    }

    [Theory]
    [InlineData(".nif")]
    [InlineData(".dds")]
    public async Task ResourceEditing_ReplaceRestoreDeleteAndReopen_PreservesVersions(string extension)
    {
        var (p, relative, target) = await EditableResourceAsync(extension);
        var plugin = File.ReadAllBytes(p.PluginPath);
        var source = Path.Combine(_root, "replacement" + extension); File.WriteAllText(source, "replacement");
        var changed = await Service.ReplaceResourceAsync(p.ProjectFilePath, relative, source, Data);
        Assert.True(changed.IsSuccess, changed.Error); Assert.Equal("replacement", File.ReadAllText(target));
        var version = Assert.Single((await Service.GetResourceVersionsAsync(p.ProjectFilePath, Data)).Value);
        Assert.True((await Service.RestoreResourceVersionAsync(p.ProjectFilePath, version.Id, Data)).IsSuccess);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.True((await Service.DeleteResourceAsync(p.ProjectFilePath, relative, Data)).IsSuccess);
        Assert.False(File.Exists(target)); Assert.Empty((await Service.GetResourcesAsync(p.ProjectFilePath, Data)).Value);
        Assert.True((await Service.RestoreResourceVersionAsync(p.ProjectFilePath, version.Id, Data)).IsSuccess);
        Assert.Equal("original", File.ReadAllText(target)); Assert.Equal(plugin, File.ReadAllBytes(p.PluginPath));
        Assert.True((await Service.OpenAsync(p.ProjectFilePath, Data)).IsSuccess);
        Assert.Equal(3, (await Service.GetResourceVersionsAsync(p.ProjectFilePath, Data)).Value.Count);
    }

    [Fact]
    public async Task ResourceEditing_UsedAndSharedNif_RefusesInPlaceEdits_SeparateCopiesPreserveOtherSword()
    {
        var (p, relative, target) = await EditableResourceAsync(".nif");
        foreach (var id in new[] { "FirstSword", "SecondSword" })
            Assert.True((await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, GameDataPath = Data,
                TemplateFormId = 0x12EB7, EditorId = id, Name = id, Details = new() { ModelPath = relative[7..] } })).IsSuccess);
        var source = Path.Combine(_root, "new.nif"); File.WriteAllText(source, "new model");
        Assert.True((await Service.ReplaceResourceAsync(p.ProjectFilePath, relative, source, Data)).IsFailure);
        Assert.True((await Service.DeleteResourceAsync(p.ProjectFilePath, relative, Data)).IsFailure);
        var copy = await Service.ImportModelCopyAsync(p.ProjectFilePath, source, Data);
        Assert.True(copy.IsSuccess, copy.Error);
        var another = await Service.ImportModelCopyAsync(p.ProjectFilePath, source, Data);
        Assert.True(another.IsSuccess); Assert.NotEqual(copy.Value, another.Value);
        var swords = (await _builder.GetWeaponsAsync(p.PluginPath)).Value;
        var first = swords[0];
        Assert.True((await _builder.UpdateSwordAsync(new UpdateSwordRequest { PluginPath = p.PluginPath, GameDataPath = Data,
            FormId = first.FormId, EditorId = first.EditorId, Name = first.Name, Details = new() { ModelPath = copy.Value },
            Damage = 7, Weight = 9, Value = 25 })).IsSuccess);
        swords = (await _builder.GetWeaponsAsync(p.PluginPath)).Value;
        Assert.Equal(relative[7..].Replace('/', '\\'), swords.Single(w => w.FormId != first.FormId).Details.ModelPath.Replace('/', '\\'));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceEditing_DdsUnknownDependencies_BlockReplacementAndDeletion(bool unregistered)
    {
        var (p, relative, target) = await EditableResourceAsync(".dds");
        var nif = Path.Combine(Path.GetDirectoryName(p.PluginPath)!, "meshes", "unknown.nif"); File.WriteAllText(nif, "nif");
        if (!unregistered) Assert.True((await Service.ImportModelAsync(p.ProjectFilePath, nif, Data)).IsSuccess);
        var source = Path.Combine(_root, "new.dds"); File.WriteAllText(source, "new");
        Assert.True((await Service.ReplaceResourceAsync(p.ProjectFilePath, relative, source, Data)).IsFailure);
        Assert.True((await Service.DeleteResourceAsync(p.ProjectFilePath, relative, Data)).IsFailure);
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task ResourceEditing_InvalidBackup_LeavesCurrentFilesUntouched(string mode)
    {
        var (p, relative, target) = await EditableResourceAsync(".nif");
        var source = Path.Combine(_root, "new.nif"); File.WriteAllText(source, "new");
        Assert.True((await Service.ReplaceResourceAsync(p.ProjectFilePath, relative, source, Data)).IsSuccess);
        var version = Assert.Single((await Service.GetResourceVersionsAsync(p.ProjectFilePath, Data)).Value);
        var backup = Path.Combine(Path.GetDirectoryName(p.PluginPath)!, ".resource-history", version.Id.ToString("N") + ".bin");
        if (mode == "corrupt") File.WriteAllText(backup, "bad");
        if (mode == "missing") File.Delete(backup);
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        Assert.True((await Service.RestoreResourceVersionAsync(p.ProjectFilePath, mode == "unknown" ? Guid.NewGuid() : version.Id, Data)).IsFailure);
        Assert.Equal("new", File.ReadAllText(target)); Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
    }

    [Theory]
    [InlineData("../escape.nif")]
    [InlineData("meshes/unregistered.nif")]
    [InlineData("textures/wrong.nif")]
    public async Task ResourceEditing_UnsafeAndUnregisteredTargets_AreRejected(string relative)
    {
        var (p, _, target) = await EditableResourceAsync(".nif");
        Assert.True((await Service.DeleteResourceAsync(p.ProjectFilePath, relative, Data)).IsFailure);
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public async Task ResourceEditing_CancellationBeforePublication_LeavesFilesUntouched()
    {
        var (p, relative, target) = await EditableResourceAsync(".nif");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.DeleteResourceAsync(p.ProjectFilePath, relative, Data, cancellation.Token));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public async Task ResourceEditing_ReviewedFingerprint_IsCheckedAgainByPluginWriter()
    {
        var (p, _, _) = await EditableResourceAsync(".nif");
        var history = new WorkspaceHistoryService(Path.Combine(_root, "history"));
        var revision = await history.InspectAsync(new(p.PluginPath, p.ProjectFilePath, Data));
        Assert.True((await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, GameDataPath = Data,
            TemplateFormId = 0x12EB7, EditorId = "ExternalSword", Name = "External" })).IsSuccess);
        var bytes = File.ReadAllBytes(p.PluginPath);
        using var expected = new WorkspaceWriteExpectation(revision);
        var result = await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, GameDataPath = Data,
            TemplateFormId = 0x12EB7, EditorId = "StaleSword", Name = "Stale" });
        Assert.True(result.IsFailure); Assert.Equal(bytes, File.ReadAllBytes(p.PluginPath)); Assert.False(expected.HasWrites);
    }

    [Fact]
    public async Task ResourceEditing_ReviewedFingerprint_IsCheckedAgainByResourceWriter()
    {
        var (p, relative, target) = await EditableResourceAsync(".nif");
        var revision = await new WorkspaceHistoryService(Path.Combine(_root, "history")).InspectAsync(new(p.PluginPath, p.ProjectFilePath, Data));
        File.AppendAllText(target, "external"); var metadata = File.ReadAllBytes(p.ProjectFilePath);
        var source = Path.Combine(_root, "replacement.nif"); File.WriteAllText(source, "replacement");
        using var expected = new WorkspaceWriteExpectation(revision);
        Assert.True((await Service.ReplaceResourceAsync(p.ProjectFilePath, relative, source, Data)).IsFailure);
        Assert.Equal("originalexternal", File.ReadAllText(target)); Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
        Assert.False(expected.HasWrites);
    }

    [Fact]
    public async Task ResourceEditing_UsedModelVersion_RestoresAsSeparateCopyAfterMove()
    {
        var (p, relative, target) = await EditableResourceAsync(".nif");
        Assert.True((await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, GameDataPath = Data,
            TemplateFormId = 0x12EB7, EditorId = "VersionSword", Name = "Version Sword", Details = new() { ModelPath = relative[7..] } })).IsSuccess);
        var version = await Service.BackupResourceAsync(p.ProjectFilePath, relative, Data);
        Assert.True(version.IsSuccess, version.Error);
        var moved = Path.Combine(_root, "version-move"); Directory.Move(Path.GetDirectoryName(p.PluginPath)!, moved);
        var projectPath = Path.Combine(moved, WeaponProjectService.ProjectFileName);
        var copy = await Service.ImportModelVersionCopyAsync(projectPath, version.Value.Id, Data);
        Assert.True(copy.IsSuccess, copy.Error); Assert.NotEqual(relative[7..], copy.Value.Replace('\\', '/'));
        Assert.Equal("original", File.ReadAllText(Path.Combine(moved, "meshes", copy.Value)));
        Assert.Equal("original", File.ReadAllText(Path.Combine(moved, relative)));
        Assert.Equal(relative[7..].Replace('/', '\\'), Assert.Single((await _builder.GetWeaponsAsync(Path.Combine(moved, "MySword.esp"))).Value).Details.ModelPath.Replace('/', '\\'));
    }

    [Theory]
    [InlineData(".nif")]
    [InlineData(".dds")]
    public async Task ResourceEditing_ModelVersionCopy_RejectsCorruptOrNonNifBackup(string extension)
    {
        var (p, relative, _) = await EditableResourceAsync(extension);
        var version = (await Service.BackupResourceAsync(p.ProjectFilePath, relative, Data)).Value;
        if (extension == ".nif") File.WriteAllText(Path.Combine(Path.GetDirectoryName(p.PluginPath)!, ".resource-history", version.Id.ToString("N") + ".bin"), "corrupt");
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        Assert.True((await Service.ImportModelVersionCopyAsync(p.ProjectFilePath, version.Id, Data)).IsFailure);
        Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
    }

    [Fact]
    public async Task ResourceEditing_MetadataPublicationFailure_RestoresResourceBytes()
    {
        var (p, relative, target) = await EditableResourceAsync(".nif");
        var source = Path.Combine(_root, "new.nif"); File.WriteAllText(source, "new");
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        using (var held = new FileStream(p.ProjectFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True((await Service.ReplaceResourceAsync(p.ProjectFilePath, relative, source, Data)).IsFailure);
        Assert.Equal("original", File.ReadAllText(target)); Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p.PluginPath)!, "*.resource.tmp", SearchOption.AllDirectories));
        Assert.Empty((await Service.GetResourceVersionsAsync(p.ProjectFilePath, Data)).Value);
    }

    [Fact]
    public async Task ResourceEditing_MovedProject_RestoresRelativeVersion_AndPackingExcludesBackups()
    {
        var (p, relative, _) = await EditableResourceAsync(".dds");
        var source = Path.Combine(_root, "new.dds"); File.WriteAllText(source, "new");
        Assert.True((await Service.ReplaceResourceAsync(p.ProjectFilePath, relative, source, Data)).IsSuccess);
        var version = Assert.Single((await Service.GetResourceVersionsAsync(p.ProjectFilePath, Data)).Value);
        var moved = Path.Combine(_root, "moved-resources"); Directory.Move(Path.GetDirectoryName(p.PluginPath)!, moved);
        var projectPath = Path.Combine(moved, WeaponProjectService.ProjectFileName);
        Assert.True((await Service.OpenAsync(projectPath, Data)).IsSuccess);
        Assert.True((await Service.RestoreResourceVersionAsync(projectPath, version.Id, Data)).IsSuccess);
        Assert.Equal("original", File.ReadAllText(Path.Combine(moved, relative)));
        Assert.True((await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = Path.Combine(moved, "MySword.esp"),
            GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "MovedSword", Name = "Moved" })).IsSuccess);
        var files = await Service.GetPackingFilesAsync(projectPath, Data);
        Assert.True(files.IsSuccess, files.Error);
        Assert.Equal(2, files.Value.Count);
        Assert.DoesNotContain(files.Value, f => f.RelativePath.Contains(".resource-history"));
    }
}
