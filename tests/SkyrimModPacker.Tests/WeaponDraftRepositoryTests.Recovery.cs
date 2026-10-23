using System.Diagnostics;
using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed partial class WeaponDraftRepositoryTests
{
    [Theory]
    [InlineData("current")]
    [InlineData("active")]
    [InlineData("clean")]
    [InlineData("discarded")]
    [InlineData("noChanges")]
    public async Task RecoveryScan_ExcludesCurrentActiveAndCompletedDrafts(string kind)
    {
        var draft = Draft;
        using var process = Process.GetCurrentProcess();
        draft = kind switch
        {
            "active" => draft with { WriterProcessId = Environment.ProcessId, WriterProcessStartedUtc = process.StartTime.ToUniversalTime() },
            "clean" => draft with { State = EditorDraftState.Clean },
            "discarded" => draft with { State = EditorDraftState.Discarded },
            "noChanges" => draft with { WeaponIsDirty = false },
            _ => draft
        };
        Assert.True((await Repository.SaveAsync(draft, 0)).IsSuccess);
        var scan = await Repository.FindPendingAsync(draft.PluginPath, Data, kind == "current" ? draft.WriterSessionId : Guid.NewGuid());
        Assert.True(scan.IsSuccess); Assert.Empty(scan.Value.Candidates); Assert.Empty(scan.Value.Errors);
    }

    [Fact]
    public async Task RecoveryScan_LegacyAndDeadOrReusedProcessDrafts_AreOfferedNewestFirst()
    {
        var draft = Draft;
        Assert.True((await Repository.SaveAsync(draft, 0)).IsSuccess);
        using var process = Process.GetCurrentProcess();
        Assert.True((await Repository.SaveAsync(draft with { WriterSessionId = Guid.NewGuid(), WriterProcessId = Environment.ProcessId,
            WriterProcessStartedUtc = process.StartTime.ToUniversalTime().AddDays(-1) }, 0)).IsSuccess);
        var scan = await Repository.FindPendingAsync(draft.PluginPath, Data, Guid.NewGuid());
        Assert.Equal(2, scan.Value.Candidates.Count); Assert.Empty(scan.Value.Errors);
        Assert.True(scan.Value.Candidates[0].Draft.SavedAtUtc >= scan.Value.Candidates[1].Draft.SavedAtUtc);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2}")]
    public async Task RecoveryScan_CorruptDraft_IsReported_AndBackupIsNotAutomaticallyRestored(string bad)
    {
        var draft = Draft;
        var receipt = (await Repository.SaveAsync(draft, 0)).Value;
        Assert.True((await Repository.SaveAsync(draft, 1)).IsSuccess);
        File.WriteAllText(receipt.FilePath, bad);
        var bytes = File.ReadAllBytes(receipt.FilePath + ".bak");
        var scan = await Repository.FindPendingAsync(draft.PluginPath, Data, Guid.NewGuid());
        Assert.True(scan.IsSuccess); Assert.Empty(scan.Value.Candidates); Assert.Single(scan.Value.Errors);
        Assert.Equal(bad, File.ReadAllText(receipt.FilePath)); Assert.Equal(bytes, File.ReadAllBytes(receipt.FilePath + ".bak"));
    }
}
