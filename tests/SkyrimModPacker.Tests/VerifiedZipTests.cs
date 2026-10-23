using System.IO.Compression;
using NSubstitute;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.Persistence;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class VerifiedZipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(VerifiedZipTests), Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(_root, "result.zip");
    private string Source => Path.Combine(_root, "source.esp");
    private readonly byte[] _old = "previous completed result"u8.ToArray();
    public VerifiedZipTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Source, "plugin-v1");
    }
    public void Dispose() => Directory.Delete(_root, true);
    private string Temporary => Assert.Single(Directory.GetFiles(_root, ".zip-*.tmp"));
    private void Clean() => Assert.Empty(Directory.GetFiles(_root, ".zip-*.tmp"));
    private sealed class Callback(Action<double> action) : IProgress<double> { public void Report(double value) => action(value); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulPublication_HasExactManifestAndUnchangedSources(bool existing)
    {
        if (existing) await File.WriteAllBytesAsync(Output, _old);
        var mesh = Path.Combine(_root, "blade.nif");
        var texture = Path.Combine(_root, "blade.dds");
        await File.WriteAllTextAsync(mesh, "mesh-bytes");
        await File.WriteAllTextAsync(texture, "dds-bytes");
        await new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp"), (mesh, "meshes/Custom/blade.nif"), (texture, "textures/Custom/blade.dds")]);
        using var zip = ZipFile.OpenRead(Output);
        Assert.Equal(3, zip.Entries.Count);
        foreach (var (source, entry) in new[] { (Source, "Sword.esp"), (mesh, "meshes/Custom/blade.nif"), (texture, "textures/Custom/blade.dds") })
        {
            await using var content = zip.GetEntry(entry)!.Open();
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes);
            Assert.Equal(await File.ReadAllBytesAsync(source), bytes.ToArray());
        }
        Assert.Equal("plugin-v1", await File.ReadAllTextAsync(Source));
        Clean();
    }

    [Theory]
    [InlineData(0.6, false)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1, false)]
    [InlineData(0.6, true)]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1, true)]
    public async Task CancellationBeforeCommit_PreservesPreviousResult(double phase, bool existing)
    {
        if (existing) await File.WriteAllBytesAsync(Output, _old);
        using var token = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")],
            new Callback(p => { if (p == phase) token.Cancel(); }), token.Token));
        if (existing) Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        else Assert.False(File.Exists(Output));
        Clean();
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("content")]
    [InlineData("unsafe")]
    [InlineData("late")]
    public async Task TamperedTemporaryZip_NeverReplacesPreviousArchive(string fault)
    {
        await File.WriteAllBytesAsync(Output, _old);
        var callback = new Callback(p =>
        {
            if (p != (fault == "late" ? 0.85 : 0.65)) return;
            if (fault is "corrupt" or "late") { File.WriteAllText(Temporary, "corrupt ZIP"); return; }
            using var zip = ZipFile.Open(Temporary, ZipArchiveMode.Update);
            if (fault is "missing" or "content") zip.GetEntry("Sword.esp")!.Delete();
            if (fault == "missing") return;
            var name = fault switch { "extra" => "textures/unselected.dds", "unsafe" => "../escape.txt", _ => "Sword.esp" };
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(fault == "content" ? "plugin-v2" : "extra");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")], callback));
        Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        Assert.Equal("plugin-v1", await File.ReadAllTextAsync(Source));
        Clean();
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(0.85)]
    [InlineData(1)]
    public async Task ChangedSource_AfterCopyOrVerification_BlocksPublication(double phase)
    {
        await File.WriteAllBytesAsync(Output, _old);
        await Assert.ThrowsAsync<IOException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")],
            new Callback(p => { if (p == phase) File.WriteAllText(Source, "plugin-v2"); })));
        Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        Clean();
    }

    [Theory]
    [InlineData("Sword.esp.bak")]
    [InlineData("meshes/blade.nif.tmp")]
    [InlineData("Sword.esp.lock")]
    [InlineData("mod-project.json")]
    [InlineData(".assets-owned/blade.nif")]
    [InlineData(".backups/Sword.esp")]
    public async Task ServiceFiles_AreRejectedBeforePublication(string entry)
    {
        await File.WriteAllBytesAsync(Output, _old);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, entry)]));
        Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        Clean();
    }

    [Fact]
    public async Task ReplacementDenied_PreservesOldResultAndAllowsRetry()
    {
        await File.WriteAllBytesAsync(Output, _old);
        using (var held = new FileStream(Output, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")]));
        Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        Clean();
        await new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")]);
        using var zip = ZipFile.OpenRead(Output);
        Assert.Equal("Sword.esp", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public async Task ExternalDestinationChange_IsPreserved()
    {
        await File.WriteAllBytesAsync(Output, _old);
        await Assert.ThrowsAsync<IOException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")],
            new Callback(p => { if (p == 0.65) File.WriteAllText(Output, "another writer's result"); })));
        Assert.Equal("another writer's result", await File.ReadAllTextAsync(Output));
        Clean();
    }

    [Fact]
    public async Task CallbackFailure_PreservesOriginalExceptionAndArchive()
    {
        await File.WriteAllBytesAsync(Output, _old);
        var original = new InvalidOperationException("Progress callback failed");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")],
            new Callback(p => { if (p == 0.65) throw original; })));
        Assert.Same(original, failure);
        Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        Clean();
    }

    [Fact]
    public async Task ConcurrentWriters_SerializeAndPublishOneCompleteResult()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword" + i + ".esp")])));
        using var zip = ZipFile.OpenRead(Output);
        Assert.StartsWith("Sword", Assert.Single(zip.Entries).FullName);
        using var reader = new StreamReader(zip.Entries[0].Open());
        Assert.Equal("plugin-v1", await reader.ReadToEndAsync());
        Clean();
    }

    [Fact]
    public async Task CancelWhileWaitingForOutputLock_DoesNotWrite()
    {
        await File.WriteAllBytesAsync(Output, _old);
        using var token = new CancellationTokenSource();
        using (await WorkspaceFileGate.AcquireAsync(Output, CancellationToken.None))
        {
            var pending = new ZipArchiver().CreateArchiveAsync(Output, [(Source, "Sword.esp")], cancellationToken: token.Token);
            await Task.Delay(50);
            Assert.False(pending.IsCompleted);
            token.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        Clean();
    }

    [Fact]
    public async Task DeclaredDependencies_ExcludePackagedPluginsAndDeduplicateExternalMasters()
    {
        var validator = Substitute.For<IPluginValidationService>();
        IReadOnlyList<PluginDiagnostic> requirements = new[]
        {
            new PluginDiagnostic("MASTER_DEPENDENCY", DiagnosticSeverity.Warning, Source, "Base.esm", "Required master", "Install it"),
            new PluginDiagnostic("MASTER_DEPENDENCY", DiagnosticSeverity.Warning, Source, "Included.esp", "Required master", "Install it")
        };
        validator.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>()).Returns(requirements);
        var context = new PackingContext { Profile = new ModProfile { Name = "Sword" }, Settings = new AppSettings { SkyrimDataPath = Path.Combine(_root, "Game", "Data") } };
        context.SelectedFiles.Add(new ScannedFile { AbsolutePath = Source, RelativePath = "Sword.esp", Category = FileCategory.Plugin });
        context.SelectedFiles.Add(new ScannedFile { AbsolutePath = Source, RelativePath = "Included.esp", Category = FileCategory.Plugin });
        Assert.True(await new PluginValidationStep(validator).ExecuteAsync(context));
        Assert.Equal("Base.esm", Assert.Single(context.ExternalDependencies));
    }

    [Fact]
    public async Task VerificationFailure_DoesNotRecordSuccessfulHistory()
    {
        await File.WriteAllBytesAsync(Output, _old);
        var data = Path.Combine(_root, "Game", "Data");
        Directory.CreateDirectory(data);
        var history = Substitute.For<IBuildHistoryRepository>();
        var tampered = false;
        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "Sword", PluginFileName = "source.esp", PluginSourcePath = Source },
            Settings = new AppSettings { SkyrimDataPath = data }, OutputArchivePath = Output,
            Progress = new Callback(p => { if (p == 0.65) { tampered = true; File.WriteAllText(Temporary, "broken ZIP"); } })
        };
        context.SelectedFiles.Add(new ScannedFile { AbsolutePath = Source, RelativePath = "source.esp", Category = FileCategory.Plugin });
        Assert.False(await new ArchiveStep(new ZipArchiver(), history).ExecuteAsync(context));
        Assert.True(tampered);
        Assert.False(context.ArchiveCreated);
        Assert.Equal(_old, await File.ReadAllBytesAsync(Output));
        await history.DidNotReceive().AddAsync(Arg.Any<BuildRecord>(), Arg.Any<CancellationToken>());
        Clean();
    }
}
