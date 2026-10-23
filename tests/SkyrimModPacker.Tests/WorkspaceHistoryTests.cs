using System.Text.Json;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class WorkspaceHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkspaceHistoryTests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceHistoryService _service;
    private readonly WorkspaceContext _context;
    private readonly string _asset;
    public WorkspaceHistoryTests()
    {
        var project = Path.Combine(_root, "project"); Directory.CreateDirectory(Path.Combine(project, "meshes"));
        _context = new(Path.Combine(project, "Sword.esp"), Path.Combine(project, "mod-project.json"), Path.Combine(_root, "Game", "Data"));
        _asset = Path.Combine(project, "meshes", "sword.nif");
        File.WriteAllText(_context.PluginPath, "original plugin"); File.WriteAllText(_asset, "original asset");
        File.WriteAllText(_context.ProjectFilePath, JsonSerializer.Serialize(new { Version = 1, Resources = new[] { "meshes/sword.nif" } }));
        _service = new(Path.Combine(_root, "appdata"));
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task WorkspaceHistory_RestoresAndRedoesPluginMetadataAndResources()
    {
        var before = await _service.CaptureAsync(_context, true);
        File.WriteAllText(_context.PluginPath, "new plugin"); File.WriteAllText(_asset, "new asset");
        var after = await _service.CaptureAsync(_context, true);
        await _service.RestoreAsync(before, after);
        Assert.Equal("original plugin", File.ReadAllText(_context.PluginPath)); Assert.Equal("original asset", File.ReadAllText(_asset));
        await _service.RestoreAsync(after, before);
        Assert.Equal("new plugin", File.ReadAllText(_context.PluginPath)); Assert.Equal("new asset", File.ReadAllText(_asset));
    }

    [Theory]
    [InlineData("plugin")]
    [InlineData("metadata")]
    [InlineData("resource")]
    public async Task WorkspaceHistory_ExternalChanges_BlockUndoWithoutOverwriting(string changed)
    {
        var before = await _service.CaptureAsync(_context, true);
        File.WriteAllText(_context.PluginPath, "new"); var after = await _service.CaptureAsync(_context, true);
        var path = changed == "plugin" ? _context.PluginPath : changed == "metadata" ? _context.ProjectFilePath : _asset;
        File.AppendAllText(path, "external");
        var bytes = File.ReadAllBytes(path);
        await Assert.ThrowsAnyAsync<Exception>(() => _service.RestoreAsync(before, after));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WorkspaceHistory_CancelDuringRestore_RollsBackPublishedFiles()
    {
        var before = await _service.CaptureAsync(_context, true);
        File.WriteAllText(_context.PluginPath, "new plugin"); File.WriteAllText(_asset, "new asset");
        var after = await _service.CaptureAsync(_context, true);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress<EditorOperationProgress>(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.RestoreAsync(before, after, progress, cancellation.Token));
        Assert.Equal("new plugin", File.ReadAllText(_context.PluginPath)); Assert.Equal("new asset", File.ReadAllText(_asset));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_context.PluginPath)!, "*.restore.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WorkspaceHistory_UnknownCheckpointAndUnsafeResource_AreRejected()
    {
        var before = await _service.CaptureAsync(_context, true);
        await Assert.ThrowsAnyAsync<IOException>(() => _service.RestoreAsync(before with { Id = Guid.NewGuid() }, before));
        File.WriteAllText(_context.ProjectFilePath, "{\"Resources\":[\"../outside.nif\"]}");
        await Assert.ThrowsAnyAsync<Exception>(() => _service.CaptureAsync(_context, true));
    }

    [Fact]
    public async Task WorkspaceHistory_CorruptBackup_RollsBackEarlierReplacements()
    {
        var before = await _service.CaptureAsync(_context, true);
        File.WriteAllText(_context.PluginPath, "new plugin"); File.WriteAllText(_asset, "new asset");
        var after = await _service.CaptureAsync(_context, true);
        var index = before.Revision.Files.Keys.ToList().IndexOf("Sword.esp");
        File.WriteAllText(Path.Combine(_root, "appdata", "EditorHistory", before.Id.ToString("N"), index + ".bin"), "corrupt");
        await Assert.ThrowsAnyAsync<IOException>(() => _service.RestoreAsync(before, after));
        Assert.Equal("new plugin", File.ReadAllText(_context.PluginPath)); Assert.Equal("new asset", File.ReadAllText(_asset));
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
