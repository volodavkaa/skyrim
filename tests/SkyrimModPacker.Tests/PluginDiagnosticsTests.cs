using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class PluginDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(PluginDiagnosticsTests), Guid.NewGuid().ToString("N"));
    private string Plugin => Path.Combine(_root, "Test.esp");
    public PluginDiagnosticsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private void Write(float version = 1.70f, uint id = 0x800, bool light = false, bool duplicate = false, bool master = false)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Test.esp"), SkyrimRelease.SkyrimSE) { IsSmallMaster = light };
        mod.ModHeader.Stats.Version = version;
        mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = "SameId" });
        if (duplicate) mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, id + 1), SkyrimRelease.SkyrimSE) { EditorID = "sameid" });
        if (master) mod.Keywords.Add(new Keyword(new FormKey(ModKey.FromNameAndExtension("Missing.esm"), 0x900), SkyrimRelease.SkyrimSE));
        mod.WriteToBinary(Plugin, new BinaryWriteParameters { FormIDCompaction = FormIDCompactionOption.NoCheck, LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed() });
    }

    [Theory]
    [InlineData(1.70f, 0x800u, true)]
    [InlineData(1.70f, 0x7FFu, false)]
    [InlineData(1.71f, 1u, true)]
    [InlineData(1.71f, 0u, false)]
    [InlineData(1.71f, 0x1000u, false)]
    public async Task EslRanges_AreHeaderSpecific(float version, uint id, bool valid)
    {
        Write(version, id, light: true);
        var before = await File.ReadAllBytesAsync(Plugin);
        var diagnostics = await new PluginValidationService().ValidateAsync(Plugin, _root, targetRuntime: new Version(1, 6, 1170));
        Assert.Equal(valid, !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateEditorIds_AndMissingMasters_AreActionableErrors(bool master)
    {
        Write(duplicate: !master, master: master);
        var diagnostics = await new PluginValidationService().ValidateAsync(Plugin, _root);
        var issue = Assert.Single(diagnostics, d => d.Code == (master ? "MASTER_UNREADABLE" : "EDITORID_DUPLICATE"));
        Assert.Equal(DiagnosticSeverity.Error, issue.Severity);
        Assert.Equal(Plugin, issue.FilePath);
        Assert.False(string.IsNullOrWhiteSpace(issue.RecommendedAction));
        Assert.False(string.IsNullOrWhiteSpace(issue.Reason));
    }

    [Theory]
    [InlineData(1, 5, 97, true)]
    [InlineData(1, 6, 640, true)]
    [InlineData(1, 6, 1130, false)]
    [InlineData(1, 6, 1170, false)]
    public async Task Header171_RequiresNativeRuntimeSupport(int major, int minor, int build, bool incompatible)
    {
        Write(1.71f);
        var diagnostics = await new PluginValidationService().ValidateAsync(Plugin, _root, targetRuntime: new Version(major, minor, build));
        Assert.Equal(incompatible, diagnostics.Any(d => d.Code == "RUNTIME_HEADER"));
    }

    [Fact]
    public async Task UnknownRuntimeAndUnsupportedChecks_AreExplicitAndDoNotClaimFullCompatibility()
    {
        Write();
        var diagnostics = await new PluginValidationService().ValidateAsync(Plugin, _root);
        Assert.Contains(diagnostics, d => d.Code == "RUNTIME_UNKNOWN" && d.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(diagnostics, d => d.Code == "REFERENCE_SCOPE" && d.Severity == DiagnosticSeverity.Unsupported);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptPlugin_ReturnsStructuredFailure(bool corrupt)
    {
        if (corrupt) await File.WriteAllTextAsync(Plugin, "broken");
        var diagnostic = Assert.Single(await new PluginValidationService().ValidateAsync(Plugin, _root));
        Assert.Equal("PLUGIN_UNREADABLE", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public async Task ValidationStep_BlocksErrorsAndKeepsStructuredResults()
    {
        Write(duplicate: true);
        var context = new PackingContext { Profile = new ModProfile { Name = "Test" }, Settings = new AppSettings { SkyrimDataPath = _root } };
        context.SelectedFiles.Add(new ScannedFile { AbsolutePath = Plugin, RelativePath = "Test.esp", Category = FileCategory.Plugin });
        Assert.False(await new PluginValidationStep(new PluginValidationService()).ExecuteAsync(context));
        Assert.Contains(context.PluginDiagnostics, d => d.Code == "EDITORID_DUPLICATE");
        Assert.NotEmpty(context.Errors);
        Assert.NotEmpty(context.Warnings);
    }

    [Fact]
    public async Task OrdinaryEsp_HighIdsAreAllowed_UntilEslIsRequested()
    {
        Write(id: 0x1000);
        var validator = new PluginValidationService();
        Assert.DoesNotContain(await validator.ValidateAsync(Plugin, _root), d => d.Code == "ESL_RANGE");
        Assert.Contains(await validator.ValidateAsync(Plugin, _root, requireEsl: true), d => d.Code == "ESL_RANGE");
    }

    [Fact]
    public async Task EditorIdScope_AllowsSameNameInDifferentRecordTypes()
    {
        Write();
        var mod = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
        mod.MiscItems.Add(new MiscItem(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "SameId" });
        mod.WriteToBinary(Plugin);
        Assert.DoesNotContain(await new PluginValidationService().ValidateAsync(Plugin, _root), d => d.Code == "EDITORID_DUPLICATE");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MasterDependencyOrder_IsChecked(bool correct)
    {
        var first = new SkyrimMod(ModKey.FromNameAndExtension("First.esm"), SkyrimRelease.SkyrimSE);
        first.WriteToBinary(Path.Combine(_root, "First.esm"));
        var second = new SkyrimMod(ModKey.FromNameAndExtension("Second.esm"), SkyrimRelease.SkyrimSE);
        second.ModHeader.MasterReferences.Add(new MasterReference { Master = first.ModKey });
        second.BeginWrite.ToPath(Path.Combine(_root, "Second.esm")).WithLoadOrderFromHeaderMasters().WithDataFolder(_root).NoMastersListContentCheck().Write();
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Test.esp"), SkyrimRelease.SkyrimSE);
        foreach (var key in correct ? new[] { first.ModKey, second.ModKey } : new[] { second.ModKey, first.ModKey })
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = key });
        mod.BeginWrite.ToPath(Plugin).WithLoadOrderFromHeaderMasters().WithDataFolder(_root).NoMastersListContentCheck().Write();
        Assert.Equal(!correct, (await new PluginValidationService().ValidateAsync(Plugin, _root)).Any(d => d.Code == "MASTER_ORDER"));
    }

    [Fact]
    public async Task Cancellation_IsNotConvertedToDiagnostic()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PluginValidationService().ValidateAsync(Plugin, _root, cancellationToken: cancellation.Token));
    }
}
