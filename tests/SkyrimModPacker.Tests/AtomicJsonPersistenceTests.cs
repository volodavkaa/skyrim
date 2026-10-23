using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Core.Models;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class AtomicJsonPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AtomicJsonPersistenceTests", Guid.NewGuid().ToString("N"));
    private readonly Guid _profileId = Guid.NewGuid();

    public AtomicJsonPersistenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "AtomicJsonPersistenceTests") + Path.DirectorySeparatorChar,
            Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(_root, recursive: true);
    }

    private string FilePath(string kind) => Path.Combine(_root, kind switch
    {
        "settings" => "settings.json", "profiles" => "profiles.json", _ => "build_history.json"
    });

    private Task SaveValue(string kind, string value, CancellationToken token = default) => kind switch
    {
        "settings" => new JsonSettingsRepository(_root).SaveAsync(new AppSettings { Theme = value, Language = value, OutputDirectory = value }, token),
        "profiles" => new JsonProfileRepository(_root).SaveAsync(new ModProfile { Id = _profileId, Name = value, PluginFileName = "Sword.esp" }, token),
        _ => new JsonBuildHistoryRepository(_root).AddAsync(new BuildRecord { ProfileName = value, ArchivePath = value }, token)
    };

    private async Task LoadValue(string kind)
    {
        if (kind == "settings") await new JsonSettingsRepository(_root).LoadAsync();
        else if (kind == "profiles") await new JsonProfileRepository(_root).LoadAllAsync();
        else await new JsonBuildHistoryRepository(_root).LoadAllAsync();
    }

    private void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

    [Theory]
    [InlineData("settings")]
    [InlineData("profiles")]
    [InlineData("history")]
    public async Task Save_ReplacesCompleteJsonAndKeepsPreviousGeneration(string kind)
    {
        var path = FilePath(kind);
        await SaveValue(kind, "first");
        Assert.False(File.Exists(path + ".bak"));
        var first = await File.ReadAllBytesAsync(path);
        await SaveValue(kind, "second");
        Assert.Equal(first, await File.ReadAllBytesAsync(path + ".bak"));
        var second = await File.ReadAllBytesAsync(path);
        await SaveValue(kind, "third");

        Assert.Equal(second, await File.ReadAllBytesAsync(path + ".bak"));
        using var current = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        using var backup = JsonDocument.Parse(await File.ReadAllBytesAsync(path + ".bak"));
        await LoadValue(kind);
        AssertNoTemporaryFiles();
        Assert.True(File.Exists(path + ".lock"));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("profiles")]
    [InlineData("history")]
    public async Task ReplacementDenied_PreservesMainAndBackupAndCleansTemporaryFile(string kind)
    {
        await SaveValue(kind, "first");
        await SaveValue(kind, "second");
        var path = FilePath(kind);
        var original = await File.ReadAllBytesAsync(path);
        var backup = await File.ReadAllBytesAsync(path + ".bak");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => SaveValue(kind, "must not commit"));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(backup, await File.ReadAllBytesAsync(path + ".bak"));
        AssertNoTemporaryFiles();
        await SaveValue(kind, "retry");
        await LoadValue(kind);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("profiles")]
    [InlineData("history")]
    public async Task CancelledSave_PreservesExistingFiles(string kind)
    {
        await SaveValue(kind, "first");
        await SaveValue(kind, "second");
        var path = FilePath(kind);
        var original = await File.ReadAllBytesAsync(path);
        var backup = await File.ReadAllBytesAsync(path + ".bak");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SaveValue(kind, "cancelled", cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(backup, await File.ReadAllBytesAsync(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("profiles")]
    [InlineData("history")]
    public async Task CorruptMain_IsReportedWithoutOverwritingItOrValidBackup(string kind)
    {
        await SaveValue(kind, "first");
        await SaveValue(kind, "second");
        var path = FilePath(kind);
        var backup = await File.ReadAllBytesAsync(path + ".bak");
        await File.WriteAllTextAsync(path, "{broken json");

        var error = await Assert.ThrowsAsync<JsonException>(() => SaveValue(kind, "must not erase evidence"));
        Assert.Contains(path, error.Message);
        await Assert.ThrowsAsync<JsonException>(() => LoadValue(kind));

        Assert.Equal("{broken json", await File.ReadAllTextAsync(path));
        Assert.Equal(backup, await File.ReadAllBytesAsync(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ConcurrentProfileCreates_AcrossRepositoryInstances_KeepEveryProfile()
    {
        var profiles = Enumerable.Range(0, 40).Select(index => new ModProfile { Name = "Sword" + index, PluginFileName = "Sword.esp" }).ToArray();

        await Task.WhenAll(profiles.Select(profile => new JsonProfileRepository(_root).SaveAsync(profile)));

        var saved = await new JsonProfileRepository(_root).LoadAllAsync();
        Assert.Equal(profiles.Select(profile => profile.Id).Order(), saved.Select(profile => profile.Id).Order());
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ConcurrentProfileDeleteUpdateAndCreate_DoNotLoseUnrelatedChanges()
    {
        var repo = new JsonProfileRepository(_root);
        var old = Enumerable.Range(0, 20).Select(index => new ModProfile { Name = "Old" + index }).ToArray();
        foreach (var profile in old) await repo.SaveAsync(profile);
        var added = Enumerable.Range(0, 10).Select(index => new ModProfile { Name = "New" + index }).ToArray();
        var deletes = old.Take(10).Select(profile => new JsonProfileRepository(_root).DeleteAsync(profile.Id));
        var updates = old.Skip(10).Select(profile => new JsonProfileRepository(_root).SaveAsync(new ModProfile { Id = profile.Id, Name = "Updated" + profile.Name }));

        await Task.WhenAll(deletes.Concat(updates).Concat(added.Select(profile => new JsonProfileRepository(_root).SaveAsync(profile))));

        var saved = await repo.LoadAllAsync();
        Assert.Equal(20, saved.Count);
        Assert.DoesNotContain(saved, profile => old.Take(10).Any(deleted => deleted.Id == profile.Id));
        Assert.All(old.Skip(10), profile => Assert.Equal("Updated" + profile.Name, saved.Single(value => value.Id == profile.Id).Name));
        Assert.All(added, profile => Assert.Contains(saved, value => value.Id == profile.Id));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ConcurrentHistoryAdds_AcrossRepositoryInstances_KeepEveryBuild()
    {
        await Task.WhenAll(Enumerable.Range(0, 60).Select(index => new JsonBuildHistoryRepository(_root).AddAsync(new BuildRecord { ProfileName = "Build" + index })));

        var records = await new JsonBuildHistoryRepository(_root).LoadAllAsync();
        Assert.Equal(60, records.Count);
        Assert.Equal(60, records.Select(record => record.ProfileName).Distinct().Count());
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task HistoryClear_PreservesPriorListInBackupAndAllowsNewBuilds()
    {
        var repo = new JsonBuildHistoryRepository(_root);
        await repo.AddAsync(new BuildRecord { ProfileName = "old" });
        var before = await File.ReadAllBytesAsync(FilePath("history"));

        await repo.ClearAsync();

        Assert.Empty(await repo.LoadAllAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(FilePath("history") + ".bak"));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => new JsonBuildHistoryRepository(_root).AddAsync(new BuildRecord { ProfileName = "New" + index })));
        Assert.Equal(10, (await repo.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task ConcurrentSettingsReadsAndWrites_OnlyReturnCompleteSnapshots()
    {
        await SaveValue("settings", "seed");

        await Task.WhenAll(Enumerable.Range(0, 30).Select(async index =>
        {
            await SaveValue("settings", index.ToString());
            var value = await new JsonSettingsRepository(_root).LoadAsync();
            Assert.Equal(value.Theme, value.Language);
            Assert.Equal(value.Theme, value.OutputDirectory);
        }));

        var final = await new JsonSettingsRepository(_root).LoadAsync();
        Assert.InRange(int.Parse(final.Theme), 0, 29);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ConcurrentInitialSettingsLoads_DoNotOverwriteExplicitSaveWithDefaults()
    {
        var loads = Enumerable.Range(0, 20).Select(_ => new JsonSettingsRepository(_root).LoadAsync()).ToArray();
        var save = SaveValue("settings", "explicit user preference");

        await Task.WhenAll(loads.Cast<Task>().Append(save));

        Assert.Equal("explicit user preference", (await new JsonSettingsRepository(_root).LoadAsync()).Theme);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SerializationFailureOrCancellation_NeverPublishesTemporaryJson(bool existing, bool cancel)
    {
        var path = Path.Combine(_root, "payload.json");
        var plainOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var plain = new AtomicJsonFile<State>(path, () => new State(), plainOptions);
        if (existing) await plain.UpdateAsync(_ => new State { Value = "old" });
        var before = existing ? await File.ReadAllBytesAsync(path) : null;
        using var cancellation = new CancellationTokenSource();
        var injected = new IOException("Injected serializer failure");
        var options = new JsonSerializerOptions(plainOptions);
        options.Converters.Add(new StateConverter(() => { if (cancel) cancellation.Cancel(); else throw injected; }));
        var file = new AtomicJsonFile<State>(path, () => new State(), options);

        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.UpdateAsync(_ => new State { Value = "partial" }, cancellation.Token));
        else Assert.Same(injected, await Assert.ThrowsAsync<IOException>(() => file.UpdateAsync(_ => new State { Value = "partial" })));

        if (existing) Assert.Equal(before, await File.ReadAllBytesAsync(path));
        else Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
        AssertNoTemporaryFiles();
        await plain.UpdateAsync(_ => new State { Value = "retry" });
        Assert.Equal("retry", (await plain.LoadAsync()).Value);
    }

    [Fact]
    public async Task CancelWhileWaitingForSameProcessGate_ReleasesWaiterWithoutWriting()
    {
        await SaveValue("settings", "old");
        using var cancellation = new CancellationTokenSource();
        using (await WorkspaceFileGate.AcquireAsync(FilePath("settings"), CancellationToken.None))
        {
            var pending = SaveValue("settings", "must not write", cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal("old", (await new JsonSettingsRepository(_root).LoadAsync()).Theme);
        await SaveValue("settings", "retry");
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CrossProcessLock_BlocksWritingAndSupportsCancellationAndRetry()
    {
        await SaveValue("settings", "old");
        var ready = Path.Combine(_root, "child-ready");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$lock = [System.IO.File]::Open($env:SKYRIM_JSON_LOCK, 'OpenOrCreate', 'ReadWrite', 'None'); try { [System.IO.File]::WriteAllText($env:SKYRIM_JSON_READY, 'ready'); [Console]::In.ReadLine() | Out-Null } finally { $lock.Dispose() }");
        start.Environment["SKYRIM_JSON_LOCK"] = FilePath("settings") + ".lock";
        start.Environment["SKYRIM_JSON_READY"] = ready;
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready) && !process.HasExited) await Task.Delay(10, timeout.Token);
            Assert.True(File.Exists(ready), process.HasExited ? await stderr : "Child lock did not become ready");
            using var cancellation = new CancellationTokenSource();
            var pending = SaveValue("settings", "must wait", cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(FilePath("settings")));
            Assert.Equal("old", document.RootElement.GetProperty("theme").GetString());
            AssertNoTemporaryFiles();
        }
        finally
        {
            if (!process.HasExited) await process.StandardInput.WriteLineAsync("release");
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Assert.Equal(0, process.ExitCode);
        await SaveValue("settings", "retry after child released lock");
        Assert.Equal("retry after child released lock", (await new JsonSettingsRepository(_root).LoadAsync()).Theme);
    }

    [Fact]
    public async Task InvalidLockPath_IsReportedInsteadOfRetriedForever()
    {
        await SaveValue("settings", "old");
        var lockPath = FilePath("settings") + ".lock";
        File.Delete(lockPath);
        Directory.CreateDirectory(lockPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SaveValue("settings", "must not write"));

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(FilePath("settings")));
        Assert.Equal("old", document.RootElement.GetProperty("theme").GetString());
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task InvalidBackupPath_PreservesMainAndCleansTemporaryFile()
    {
        await SaveValue("settings", "old");
        var path = FilePath("settings");
        var before = await File.ReadAllBytesAsync(path);
        Directory.CreateDirectory(path + ".bak");

        var failure = await Record.ExceptionAsync(() => SaveValue("settings", "must not commit"));

        Assert.NotNull(failure);
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("profiles")]
    [InlineData("history")]
    public async Task NullJson_PreservesExistingFallbackBehavior(string kind)
    {
        await File.WriteAllTextAsync(FilePath(kind), "null");

        if (kind == "settings") Assert.Equal("Dark", (await new JsonSettingsRepository(_root).LoadAsync()).Theme);
        else if (kind == "profiles") Assert.Empty(await new JsonProfileRepository(_root).LoadAllAsync());
        else Assert.Empty(await new JsonBuildHistoryRepository(_root).LoadAllAsync());

        Assert.Equal("null", await File.ReadAllTextAsync(FilePath(kind)));
        AssertNoTemporaryFiles();
    }

    public sealed class State
    {
        public string Value { get; set; } = "default";
    }

    private sealed class StateConverter(Action writeAction) : JsonConverter<State>
    {
        public override State Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return new State { Value = document.RootElement.GetProperty("value").GetString()! };
        }

        public override void Write(Utf8JsonWriter writer, State value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("value", value.Value);
            writeAction();
            writer.WriteEndObject();
        }
    }
}
