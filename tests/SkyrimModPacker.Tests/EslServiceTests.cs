using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Infrastructure.Esl;
using Xunit;

namespace SkyrimModPacker.Tests;

public class EslServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "EslServiceTests", Guid.NewGuid().ToString("N"));

    public EslServiceTests() => Directory.CreateDirectory(_testRoot);

    public void Dispose() => Directory.Delete(_testRoot, recursive: true);

    private string WritePlugin(float headerVersion, IEnumerable<uint> ownedIds, bool addOverride = false, bool isEsl = false)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Test.esp"), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Stats.Version = headerVersion;
        mod.IsSmallMaster = isEsl;
        foreach (var id in ownedIds)
            mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE));
        if (addOverride)
            mod.Keywords.Add(new Keyword(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x123456), SkyrimRelease.SkyrimSE));

        var path = Path.Combine(_testRoot, "Test.esp");
        // Preserve deliberately incompatible IDs for negative fixtures; never compact them.
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            FormIDCompaction = FormIDCompactionOption.NoCheck,
            LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed()
        });
        return path;
    }

    [Theory]
    [InlineData(1.70f, 0x800u, true)]
    [InlineData(1.70f, 0xFFFu, true)]
    [InlineData(1.70f, 0x7FFu, false)]
    [InlineData(1.70f, 0x1000u, false)]
    [InlineData(1.71f, 0x000u, false)]
    [InlineData(1.71f, 0x001u, true)]
    [InlineData(1.71f, 0x7FFu, true)]
    [InlineData(1.71f, 0xFFFu, true)]
    [InlineData(1.71f, 0x1000u, false)]
    public async Task AnalyzeAsync_ChecksHeaderSpecificRange(float version, uint id, bool compatible)
    {
        var path = WritePlugin(version, [id]);
        var before = await File.ReadAllBytesAsync(path);

        var result = await new EslService().AnalyzeAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(compatible, result.Value.IsCompatible);
        Assert.Equal(1, result.Value.OwnRecordCount);
        Assert.Equal(compatible ? 0 : 1, result.Value.ProblematicRecordCount);
        Assert.Equal(version, result.Value.HeaderVersion);
        Assert.Equal(!compatible, result.Value.Reasons.Count > 0);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_testRoot));
    }

    [Fact]
    public async Task AnalyzeAsync_IgnoresMasterOverrides()
    {
        var path = WritePlugin(1.70f, [0x800], addOverride: true);
        var result = await new EslService().AnalyzeAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value.IsCompatible);
        Assert.Equal(1, result.Value.OwnRecordCount);
        Assert.Equal(0, result.Value.ProblematicRecordCount);
    }

    [Fact]
    public async Task AnalyzeAsync_CountsAllProblematicOwnedRecords()
    {
        var path = WritePlugin(1.70f, [0x001, 0x7FF, 0x800, 0xFFF, 0x1000], addOverride: true);
        var result = await new EslService().AnalyzeAsync(path);
        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.Value.IsCompatible);
        Assert.Equal(5, result.Value.OwnRecordCount);
        Assert.Equal(3, result.Value.ProblematicRecordCount);
    }

    [Theory]
    [InlineData(4095, true, 0)]
    [InlineData(4096, false, 1)]
    [InlineData(4097, false, 2)]
    public async Task AnalyzeAsync_ChecksCountAndRangeTogether(int count, bool compatible, int problems)
    {
        var path = WritePlugin(1.71f, Enumerable.Range(1, count).Select(i => (uint)i));
        var result = await new EslService().AnalyzeAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(compatible, result.Value.IsCompatible);
        Assert.Equal(count, result.Value.OwnRecordCount);
        Assert.Equal(problems, result.Value.ProblematicRecordCount);
        if (count > 4096)
            Assert.Contains(result.Value.Reasons, r => r.Contains("limit of 4096"));
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsExistingEslFlag()
    {
        var result = await new EslService().AnalyzeAsync(WritePlugin(1.70f, [0x800], isEsl: true));
        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value.IsAlreadyEsl);
        Assert.True(result.Value.IsCompatible);
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyPlugin_IsCompatible()
    {
        var result = await new EslService().AnalyzeAsync(WritePlugin(1.70f, []));
        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value.IsCompatible);
        Assert.Equal(0, result.Value.OwnRecordCount);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingOrCorruptPlugin_ReturnsFailure()
    {
        var path = Path.Combine(_testRoot, "broken.esp");
        Assert.True((await new EslService().AnalyzeAsync(path)).IsFailure);
        await File.WriteAllTextAsync(path, "invalid plugin");
        Assert.True((await new EslService().AnalyzeAsync(path)).IsFailure);
    }

    [Fact]
    public async Task AnalyzeAsync_Cancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EslService().AnalyzeAsync("unused.esp", cts.Token));
    }

    [Theory]
    [InlineData(1.70f, 0x800u)]
    [InlineData(1.71f, 0x001u)]
    public async Task SetFlagAsync_SetsHeaderBit_PreservesRecordsAndBackup(float version, uint id)
    {
        var path = WritePlugin(version, [id], addOverride: true);
        var original = await File.ReadAllBytesAsync(path);
        using var before = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var keys = before.EnumerateMajorRecords().Select(r => r.FormKey).ToArray();
        before.Dispose();

        var service = new EslService();
        var result = await service.SetFlagAsync(path, Path.Combine(_testRoot, "Game", "Data"));
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(path + ".bak"));
        using var after = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        Assert.True(after.IsSmallMaster);
        Assert.Equal(version, after.ModHeader.Stats.Version);
        Assert.Equal(keys, after.EnumerateMajorRecords().Select(r => r.FormKey));
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(0x200u, BitConverter.ToUInt32(bytes, 8) & 0x200u);
        Assert.Equal(BitConverter.ToUInt32(original, 8) | 0x200u, BitConverter.ToUInt32(bytes, 8));
        Assert.Empty(Directory.GetDirectories(_testRoot, ".esl-*"));
        after.Dispose();
        var flagged = await File.ReadAllBytesAsync(path);
        var repeated = await service.SetFlagAsync(path, Path.Combine(_testRoot, "Game", "Data"));
        Assert.True(repeated.IsSuccess, repeated.Error);
        Assert.Empty(repeated.Value);
        Assert.Equal(flagged, await File.ReadAllBytesAsync(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path + ".bak"));
    }

    [Fact]
    public async Task SetFlagAsync_Incompatible_DoesNotWriteOrBackup()
    {
        var path = WritePlugin(1.70f, [0x1000]);
        var original = await File.ReadAllBytesAsync(path);
        var result = await new EslService().SetFlagAsync(path, Path.Combine(_testRoot, "Game", "Data"));
        Assert.True(result.IsFailure);
        Assert.Contains("outside", result.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task SetFlagAsync_ProtectsGameAndBackup()
    {
        var path = WritePlugin(1.70f, [0x800]);
        var original = await File.ReadAllBytesAsync(path);
        var service = new EslService();
        Assert.True((await service.SetFlagAsync(path, _testRoot)).IsFailure);
        Assert.True((await service.SetFlagAsync(path, "")).IsFailure);
        var gameData = Path.Combine(_testRoot, "Game", "Data");
        Assert.True((await service.SetFlagAsync(path, gameData, Path.Combine(gameData, "Bad.bak"))).IsFailure);
        Assert.True((await service.SetFlagAsync(path, gameData, Path.Combine(_testRoot, "Game", "Bad.bak"))).IsFailure);
        await File.WriteAllTextAsync(path + ".bak", "previous backup");
        Assert.True((await service.SetFlagAsync(path, gameData)).IsFailure);
        Assert.Equal("previous backup", await File.ReadAllTextAsync(path + ".bak"));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SetFlagAsync_Cancelled_DoesNotWriteOrBackup()
    {
        var path = WritePlugin(1.70f, [0x800]);
        var original = await File.ReadAllBytesAsync(path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EslService().SetFlagAsync(
            path, Path.Combine(_testRoot, "Game", "Data"), cancellationToken: cts.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_testRoot));
    }
}
