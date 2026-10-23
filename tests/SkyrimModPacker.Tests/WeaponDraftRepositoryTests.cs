using System.Text.Json;
using System.Text.Json.Serialization;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed partial class WeaponDraftRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeaponDraftRepositoryTests", Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    private string Data => Path.Combine(_root, "Game", "Data");
    private JsonWeaponDraftRepository Repository => new(Path.Combine(_root, "appdata"));
    private WeaponEditorDraft Draft => new()
    {
        WriterSessionId = Guid.NewGuid(), PluginPath = Path.Combine(_root, "project", "Sword.esp"), GameDataPath = Data,
        State = EditorDraftState.Pending, WeaponIsDirty = true, Weapon = new() { Name = "Чернетка", Damage = "invalid", FirstPersonPath = @"Custom\First.nif" }
    };
    public WeaponDraftRepositoryTests() => Directory.CreateDirectory(Data);
    public void Dispose() => Directory.Delete(_root, true);
    private static WeaponEditorDraft Read(string path) => JsonSerializer.Deserialize<WeaponEditorDraft>(File.ReadAllText(path), Options)!;

    [Fact]
    public async Task DraftRepository_AtomicUpdates_PreserveRawFieldsAndPreviousGeneration()
    {
        var draft = Draft;
        var first = await Repository.SaveAsync(draft, 0);
        Assert.True(first.IsSuccess, first.Error);
        var bytes = File.ReadAllBytes(first.Value.FilePath);
        var second = await Repository.SaveAsync(draft with { State = EditorDraftState.Clean, WeaponIsDirty = false }, 1);
        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(2, second.Value.Revision);
        Assert.Equal(bytes, File.ReadAllBytes(first.Value.FilePath + ".bak"));
        var actual = Read(first.Value.FilePath);
        Assert.Equal("invalid", actual.Weapon.Damage); Assert.Equal("Чернетка", actual.Weapon.Name);
        Assert.Equal(EditorDraftState.Clean, actual.State); Assert.False(actual.WeaponIsDirty);
        Assert.Equal(draft.WriterSessionId, actual.WriterSessionId);
        Assert.NotEqual(default, actual.SavedAtUtc);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(_root, "project")));
    }

    [Fact]
    public async Task DraftRepository_ConcurrentClients_OnlyOneExpectedRevisionWins()
    {
        var draft = Draft;
        var first = (await Repository.SaveAsync(draft, 0)).Value;
        var attempts = await Task.WhenAll(Repository.SaveAsync(draft with { Weapon = draft.Weapon with { Name = "A" } }, 1),
            Repository.SaveAsync(draft with { Weapon = draft.Weapon with { Name = "B" } }, 1));
        Assert.Single(attempts, r => r.IsSuccess); Assert.Single(attempts, r => r.IsFailure);
        var current = Read(first.FilePath);
        Assert.Equal(2, current.Revision); Assert.Contains(current.Weapon.Name, new[] { "A", "B" });
        Assert.Equal(1, Read(first.FilePath + ".bak").Revision);
    }

    [Fact]
    public async Task DraftRepository_SeparateSessionsAndPlugins_DoNotOverwriteExistingDrafts()
    {
        var draft = Draft;
        var first = (await Repository.SaveAsync(draft, 0)).Value;
        var bytes = File.ReadAllBytes(first.FilePath);
        var otherSession = (await Repository.SaveAsync(draft with { WriterSessionId = Guid.NewGuid() }, 0)).Value;
        var otherPlugin = (await Repository.SaveAsync(draft with { PluginPath = Path.Combine(_root, "other", "Sword.esp") }, 0)).Value;
        Assert.Equal(3, new[] { first.FilePath, otherSession.FilePath, otherPlugin.FilePath }.Distinct().Count());
        Assert.Equal(bytes, File.ReadAllBytes(first.FilePath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2}")]
    public async Task DraftRepository_CorruptOrUnsupportedCurrentFile_IsNeverSilentlyReset(string content)
    {
        var draft = Draft;
        var first = (await Repository.SaveAsync(draft, 0)).Value;
        File.WriteAllText(first.FilePath, content);
        var before = File.ReadAllBytes(first.FilePath);
        var result = await Repository.SaveAsync(draft, 1);
        Assert.True(result.IsFailure); Assert.Equal(before, File.ReadAllBytes(first.FilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DraftRepository_ReplacementFailure_PreservesCurrentAndCanRetry()
    {
        var draft = Draft;
        var first = (await Repository.SaveAsync(draft, 0)).Value;
        var bytes = File.ReadAllBytes(first.FilePath);
        using (var held = new FileStream(first.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True((await Repository.SaveAsync(draft, 1)).IsFailure);
        Assert.Equal(bytes, File.ReadAllBytes(first.FilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.True((await Repository.SaveAsync(draft, 1)).IsSuccess);
    }

    [Fact]
    public async Task DraftRepository_ExclusiveExternalLock_CanCancelWaitWithoutChangingJson()
    {
        var draft = Draft;
        var first = (await Repository.SaveAsync(draft, 0)).Value;
        var bytes = File.ReadAllBytes(first.FilePath);
        using var held = new FileStream(first.FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repository.SaveAsync(draft, 1, cancel.Token));
        Assert.Equal(bytes, File.ReadAllBytes(first.FilePath));
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("plugin")]
    [InlineData("relative")]
    [InlineData("session")]
    [InlineData("revision")]
    public async Task DraftRepository_UnsafeIdentityOrDestination_IsRejectedBeforeWrites(string problem)
    {
        var draft = Draft;
        var repo = problem == "storage" ? new JsonWeaponDraftRepository(Data) : Repository;
        draft = problem switch
        {
            "plugin" => draft with { PluginPath = Path.Combine(Data, "Sword.esp") },
            "relative" => draft with { PluginPath = "Sword.esp" },
            "session" => draft with { WriterSessionId = Guid.Empty },
            _ => draft
        };
        Assert.True((await repo.SaveAsync(draft, problem == "revision" ? -1 : 0)).IsFailure);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DraftRepository_DeletedFileOrStaleRevision_IsNotRecreated()
    {
        var draft = Draft;
        var first = (await Repository.SaveAsync(draft, 0)).Value;
        Assert.True((await Repository.SaveAsync(draft, 0)).IsFailure);
        File.Delete(first.FilePath);
        Assert.True((await Repository.SaveAsync(draft, 1)).IsFailure);
        Assert.False(File.Exists(first.FilePath));
    }
}
