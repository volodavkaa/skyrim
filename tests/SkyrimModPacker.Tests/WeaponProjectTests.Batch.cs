using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    [Fact]
    public async Task Batch_PreviewWritesNothing_AndImportsMixedFilesInOneCommit()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var nif = await AssetAsync("batch/blade.nif", "nif"); var dds = await AssetAsync("textures/Custom/blade.dds", "dds");
        var before = File.ReadAllBytes(project.ProjectFilePath); var plugin = File.ReadAllBytes(project.PluginPath); var master = File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm"));
        var preview = await Service.PreviewBatchImportAsync(project.ProjectFilePath, [nif, dds], Data);
        Assert.True(preview.IsSuccess, preview.Error); Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath));
        Assert.Equal(2, preview.Value.Items.Count); Assert.All(preview.Value.Items, i => Assert.Null(i.Conflict));
        Assert.Equal("textures/Custom/blade.dds", preview.Value.Items[1].RelativePath);
        var imported = await Service.ImportBatchAsync(preview.Value, Data); Assert.True(imported.IsSuccess, imported.Error);
        Assert.Equal(2, (await Service.GetResourcesAsync(project.ProjectFilePath, Data)).Value.Count);
        foreach (var item in preview.Value.Items) Assert.Equal(File.ReadAllBytes(item.SourcePath), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(project.PluginPath)!, item.RelativePath)));
        Assert.Equal(plugin, File.ReadAllBytes(project.PluginPath)); Assert.Equal(master, File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm")));
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetDirectoryName(project.PluginPath)!), d => Path.GetFileName(d).StartsWith(".assets-"));
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("sourceChanged")] [InlineData("projectChanged")] [InlineData("destinationExists")]
    [InlineData("twoFirst")] [InlineData("twoThird")] [InlineData("textureAssignment")] [InlineData("traversal")] [InlineData("cancel")]
    public async Task Batch_ConflictsAndStaleOrCancelledPlansPreserveTheWholeProject(string scenario)
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var first = await AssetAsync("batch/first.nif", "first");
        var second = await AssetAsync(scenario == "duplicate" ? "other/first.nif" : scenario == "textureAssignment" ? "batch/second.dds" : "batch/second.nif", "second");
        var plan = (await Service.PreviewBatchImportAsync(project.ProjectFilePath, [first, second], Data)).Value;
        if (scenario == "duplicate") Assert.All(plan.Items, i => Assert.NotNull(i.Conflict));
        if (scenario == "sourceChanged") await File.WriteAllTextAsync(second, "changed");
        if (scenario == "projectChanged") await File.AppendAllTextAsync(project.ProjectFilePath, "\n");
        if (scenario == "destinationExists")
        {
            var target = Path.Combine(Path.GetDirectoryName(project.PluginPath)!, plan.Items[1].RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllTextAsync(target, "keep existing");
        }
        if (scenario is "twoFirst" or "twoThird") plan = plan with { Items = plan.Items.Select(i => i with { Assignment = scenario == "twoFirst" ? ModelAssignment.FirstPerson : ModelAssignment.ThirdPerson }).ToArray() };
        if (scenario == "textureAssignment") plan = plan with { Items = plan.Items.Select(i => i with { Assignment = ModelAssignment.FirstPerson }).ToArray() };
        if (scenario == "traversal") plan = plan with { Items = [plan.Items[0] with { RelativePath = "meshes/../../escape.nif" }] };
        var before = File.ReadAllBytes(project.ProjectFilePath); var plugin = File.ReadAllBytes(project.PluginPath);
        using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ImportBatchAsync(plan, Data, cts.Token));
        else Assert.True((await Service.ImportBatchAsync(plan, Data, cts.Token)).IsFailure);
        Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath)); Assert.Equal(plugin, File.ReadAllBytes(project.PluginPath));
        Assert.Empty((await Service.GetResourcesAsync(project.ProjectFilePath, Data)).Value);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(project.PluginPath)!, plan.Items[0].RelativePath)));
        if (scenario == "sourceChanged")
        {
            var retry = await Service.PreviewBatchImportAsync(project.ProjectFilePath, [first, second], Data);
            Assert.True((await Service.ImportBatchAsync(retry.Value, Data)).IsSuccess);
        }
    }

    [Theory]
    [InlineData("bad.txt")] [InlineData("missing.nif")] [InlineData("empty.dds")]
    public async Task Batch_InvalidSourcesFailPreviewWithoutMetadataChanges(string filename)
    {
        var project = (await Service.CreateAsync(Request())).Value; var before = File.ReadAllBytes(project.ProjectFilePath);
        var source = Path.Combine(_root, filename); if (filename != "missing.nif") await File.WriteAllTextAsync(source, filename == "empty.dds" ? "" : "text");
        Assert.True((await Service.PreviewBatchImportAsync(project.ProjectFilePath, [source], Data)).IsFailure);
        Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Batch_ReadOrMetadataPublishFailureRollsBackAllStagedOrPublishedFiles(bool metadataFailure)
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var first = await AssetAsync("batch/first.nif", "first"); var second = await AssetAsync("batch/second.nif", "second");
        var plan = (await Service.PreviewBatchImportAsync(project.ProjectFilePath, [first, second], Data)).Value;
        var before = File.ReadAllBytes(project.ProjectFilePath);
        using (var locked = new FileStream(metadataFailure ? project.ProjectFilePath : second, FileMode.Open, FileAccess.Read, metadataFailure ? FileShare.Read : FileShare.None))
        {
            var result = await Service.ImportBatchAsync(plan, Data); Assert.True(result.IsFailure);
        }
        Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath));
        Assert.Empty((await Service.GetResourcesAsync(project.ProjectFilePath, Data)).Value);
        foreach (var item in plan.Items) Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(project.PluginPath)!, item.RelativePath)));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(project.PluginPath)!, ".assets-*"));
        Assert.True((await Service.ImportBatchAsync(plan, Data)).IsSuccess);
    }
}
