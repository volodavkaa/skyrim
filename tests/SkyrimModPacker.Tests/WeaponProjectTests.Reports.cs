using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Stage5_ReportContainsRecordsMastersModelsTexturesAndVerifiedPayload(bool temp, bool esl)
    {
        var project = await PreparePackingProjectAsync(); var profile = PackingProfile(project, esl); profile.IncludeExportReport = true;
        var before = File.ReadAllBytes(project.PluginPath); var master = File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm"));
        using var provider = PackingProvider();
        var context = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile, new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives"), UseTempDirectory = temp });
        Assert.True(context.IsSuccess, string.Join("; ", context.Errors));
        using var zip = ZipFile.OpenRead(context.OutputArchivePath); using var stream = zip.GetEntry("mod-report.json")!.Open();
        Assert.Equal(zip.Entries.Count, Assert.Single(await provider.GetRequiredService<IBuildHistoryRepository>().LoadAllAsync()).FileCount);
        using var report = await JsonDocument.ParseAsync(stream); var root = report.RootElement;
        Assert.NotEmpty(root.GetProperty("Records").EnumerateArray()); Assert.Contains(root.GetProperty("Masters").EnumerateArray(), m => m.GetString() == "Skyrim.esm");
        Assert.NotEmpty(root.GetProperty("Models").EnumerateArray()); Assert.Equal("Passed", root.GetProperty("PluginValidation").GetProperty("Status").GetString());
        Assert.Equal("Passed", root.GetProperty("ZipValidation").GetProperty("PayloadStatus").GetString());
        Assert.Contains(root.GetProperty("SkippedChecks").EnumerateArray(), m => m.GetString() == "Skyrim runtime");
        foreach (var item in root.GetProperty("Files").EnumerateArray())
        {
            await using var asset = zip.GetEntry(item.GetProperty("Path").GetString()!)!.Open();
            Assert.Equal(item.GetProperty("Sha256").GetString(), Convert.ToHexString(await SHA256.HashDataAsync(asset)));
        }
        Assert.Contains(root.GetProperty("Files").EnumerateArray(), f => f.GetProperty("Path").GetString()!.EndsWith(".dds"));
        Assert.Equal(before, File.ReadAllBytes(project.PluginPath)); Assert.Equal(master, File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm")));
        Assert.Contains(context.LogMessages, m => m.Contains("embedded mod-report.json"));
    }

    private sealed class InterruptedReportArchiver(int failOnCall, bool cancel = false, bool corrupt = false) : IArchiver
    {
        private int _calls;
        public string Extension => ".zip";
        public async Task CreateArchiveAsync(string path, IReadOnlyList<(string SourcePath, string EntryPath)> files, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (++_calls == failOnCall)
            {
                if (cancel) throw new OperationCanceledException();
                if (corrupt) { await File.WriteAllTextAsync(path, "not a ZIP", cancellationToken); return; }
                throw new IOException("Injected archive failure");
            }
            await new ZipArchiver().CreateArchiveAsync(path, files, progress, cancellationToken);
        }
    }

    [Theory]
    [InlineData(1, false, false)] [InlineData(2, false, false)] [InlineData(1, true, false)] [InlineData(2, true, false)]
    [InlineData(1, false, true)] [InlineData(2, false, true)]
    public async Task Stage5_ReportFailureAndCancellationPreservePreviousArchive(int call, bool cancel, bool corrupt)
    {
        var project = await PreparePackingProjectAsync(); var profile = PackingProfile(project); profile.IncludeExportReport = true;
        var files = (await Service.GetPackingFilesAsync(project.ProjectFilePath, Data)).Value.Select(f => (f.AbsolutePath, f.RelativePath)).ToArray();
        var output = Path.Combine(_root, "existing.zip"); await File.WriteAllTextAsync(output, "previous archive");
        var context = new PackingContext { Profile = profile, Settings = new AppSettings { SkyrimDataPath = Data }, OutputArchivePath = output };
        var service = new ReportedArchiveService(new PluginValidationService());
        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(new InterruptedReportArchiver(call, cancel, corrupt), context, files));
        Assert.Equal("previous archive", File.ReadAllText(output)); Assert.Empty(Directory.GetDirectories(_root, ".report-*"));
    }

    [Fact]
    public async Task Stage5_FullWizardCreatesIndependentModelsAndZipWithReport()
    {
        using var provider = PackingProvider();
        await provider.GetRequiredService<ISettingsRepository>().SaveAsync(new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives") });
        var vm = ActivatorUtilities.CreateInstance<WeaponsViewModel>(provider);
        vm.PluginName = "Wizard"; vm.OutputDirectory = Output;
        var master = File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm"));
        await vm.InitializeCommand.ExecuteAsync(null); await vm.CreatePluginCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsTemplateStep);
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsModelStep);
        var third = await AssetAsync("batch/third.nif", "third"); var first = await AssetAsync("batch/first.nif", "first"); var dds = await AssetAsync("textures/Custom/blade.dds", "dds");
        await vm.PreviewBatchCommand.ExecuteAsync(new[] { third, first, dds }); Assert.Null(vm.ErrorMessage);
        vm.BatchItems[0].Assignment = ModelAssignment.ThirdPerson; vm.BatchItems[1].Assignment = ModelAssignment.FirstPerson;
        await vm.ImportBatchCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsPropertiesStep);
        vm.WeaponName = "Wizard sword"; vm.Damage = "42";
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsRecipeStep);
        await vm.SaveWizardSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsSummaryStep);
        await vm.NextStepCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.True(vm.IsZipStep);
        await vm.PackProjectCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.True(File.Exists(vm.LastProjectArchivePath));
        using var zip = ZipFile.OpenRead(vm.LastProjectArchivePath!); Assert.Equal(5, zip.Entries.Count); Assert.NotNull(zip.GetEntry("mod-report.json"));
        Assert.Equal(master, File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm")));
        var saved = Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value); Assert.Equal((ushort)42, saved.Damage); Assert.NotNull(saved.Details.FirstPersonModel);
        Assert.NotEqual(saved.Details.ModelPath, saved.Details.FirstPersonModelPath);
        Assert.True(Assert.Single(await provider.GetRequiredService<IProfileRepository>().LoadAllAsync()).IncludeExportReport);
    }

    [Fact]
    public async Task Stage5_ReportDescribesRecipeIngredientsPerksAndConditions()
    {
        var path = Path.Combine(Data, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct();
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1100), SkyrimRelease.SkyrimSE) { EditorID = "CraftingSmithingForge" });
        master.MiscItems.Add(new MiscItem(new FormKey(master.ModKey, 0x1101), SkyrimRelease.SkyrimSE) { EditorID = "IngotIron" });
        master.Perks.Add(new Perk(new FormKey(master.ModKey, 0x1102), SkyrimRelease.SkyrimSE) { EditorID = "SteelSmithing" }); master.WriteToBinary(path);
        var project = await PreparePackingProjectAsync(); var weapon = Assert.Single((await _builder.GetWeaponsAsync(project.PluginPath)).Value);
        var recipe = await _builder.SaveRecipeAsync(new() { PluginPath = project.PluginPath, GameDataPath = Data, Recipe = new()
        {
            EditorId = "MakeSword", WeaponFormId = weapon.FormId, Workbench = new("Skyrim.esm", 0x1100),
            Ingredients = [new() { Reference = new("Skyrim.esm", 0x1101), Count = 2 }], RequiredPerk = new("Skyrim.esm", 0x1102)
        } }); Assert.True(recipe.IsSuccess, recipe.Error);
        using var provider = PackingProvider(); var profile = PackingProfile(project); profile.IncludeExportReport = true;
        var context = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile, new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives") });
        Assert.True(context.IsSuccess, string.Join("; ", context.Errors)); using var zip = ZipFile.OpenRead(context.OutputArchivePath);
        using var report = await JsonDocument.ParseAsync(zip.GetEntry("mod-report.json")!.Open());
        var actual = Assert.Single(report.RootElement.GetProperty("Recipes").EnumerateArray());
        Assert.Equal("MakeSword", actual.GetProperty("EditorID").GetString());
        Assert.Equal(2, Assert.Single(actual.GetProperty("Ingredients").EnumerateArray()).GetProperty("Count").GetInt32());
        Assert.Contains("001102", Assert.Single(actual.GetProperty("Conditions").EnumerateArray()).GetProperty("Perk").GetString());
    }

    [Fact]
    public async Task Stage5_ReportRefusesInvalidPluginAndReservedNameWithoutPublishing()
    {
        var project = await PreparePackingProjectAsync(); var files = (await Service.GetPackingFilesAsync(project.ProjectFilePath, Data)).Value.Select(f => (f.AbsolutePath, f.RelativePath)).ToArray();
        var output = Path.Combine(_root, "rejected.zip"); var context = new PackingContext { Profile = PackingProfile(project), Settings = new AppSettings { SkyrimDataPath = Data }, OutputArchivePath = output };
        var validator = Substitute.For<IPluginValidationService>();
        validator.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new PluginDiagnostic("INVALID", DiagnosticSeverity.Error, project.PluginPath, null, "Injected failure", "Fix") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReportedArchiveService(validator).CreateAsync(new ZipArchiver(), context, files));
        Assert.False(File.Exists(output));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReportedArchiveService(new PluginValidationService()).CreateAsync(new ZipArchiver(), context, [(project.PluginPath, "MOD-REPORT.JSON")]));
        Assert.False(File.Exists(output)); Assert.Empty(Directory.GetDirectories(_root, ".report-*"));
    }
}
