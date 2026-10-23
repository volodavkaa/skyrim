using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Esl;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    private void AddVanillaFirstPerson()
    {
        var masterPath = Path.Combine(Data, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(masterPath).Mutable().Construct();
        var stat = new Static(new FormKey(master.ModKey, 0x12345), SkyrimRelease.SkyrimSE)
        {
            EditorID = "FirstPersonIronSword", Model = new Model { File = @"Weapons\Iron\First.nif" }, MaxAngle = 45
        };
        master.Statics.Add(stat);
        master.Weapons.First().FirstPersonModel.SetTo(stat.FormKey);
        master.WriteToBinary(masterPath);
    }

    private Task<SkyrimModPacker.Core.Common.Result<WeaponRecord>> CloneModelAsync(WeaponProject p, string id = "ModelSword", string? first = null, string? third = null) =>
        _builder.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = p.PluginPath, ProjectFilePath = p.ProjectFilePath, GameDataPath = Data,
            TemplateFormId = 0x12EB7, EditorId = id, Name = id,
            Details = new WeaponDetails { ModelPath = third ?? @"Weapons\Iron\Sword.nif", FirstPersonModelPath = first }
        });

    private Task<SkyrimModPacker.Core.Common.Result<WeaponRecord>> UpdateModelAsync(WeaponProject p, uint id, string? first,
        PluginRecordReference? original = null, bool clear = false) => _builder.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = p.PluginPath, GameDataPath = Data, FormId = id, EditorId = "ModelSword", Name = "Edited",
            Damage = 8, Weight = 9, Value = 25,
            Details = new WeaponDetails { ModelPath = @"Weapons\Iron\Sword.nif", FirstPersonModelPath = first,
                FirstPersonModel = original, ClearFirstPersonModel = clear }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Models_CustomFirst_ReusesOwnedStat_PreservesIdsAndSource(bool esl)
    {
        AddVanillaFirstPerson();
        var master = File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm"));
        var p = (await Service.CreateAsync(Request(esl ? ".esl" : ".esp"))).Value;
        var clone = await CloneModelAsync(p, first: @"Custom\First.nif");
        Assert.True(clone.IsSuccess, clone.Error);
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE))
        {
            Assert.Equal(0x800u, mod.Weapons.First().FormKey.ID);
            Assert.Equal(0x801u, Assert.Single(mod.Statics).FormKey.ID);
            Assert.Equal(0x802u, mod.ModHeader.Stats.NextFormID);
            Assert.Equal(mod.Statics.First().FormKey, mod.Weapons.First().FirstPersonModel.FormKey);
            Assert.Equal(45f, mod.Statics.First().MaxAngle);
        }
        var update = await UpdateModelAsync(p, clone.Value.FormId, @"Custom\Other.nif");
        Assert.True(update.IsSuccess, update.Error);
        var preserve = await UpdateModelAsync(p, clone.Value.FormId, null);
        Assert.True(preserve.IsSuccess, preserve.Error);
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE))
        {
            Assert.Equal(0x801u, Assert.Single(mod.Statics).FormKey.ID);
            Assert.Equal(@"Custom\Other.nif", mod.Statics.First().Model!.File.GivenPath);
            Assert.Equal(0x802u, mod.ModHeader.Stats.NextFormID);
        }
        Assert.Equal(master, File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm")));
        Assert.True((await new EslService().AnalyzeAsync(p.PluginPath)).Value.IsCompatible);
        Assert.Equal(0x12EB7u, (await Service.GetTemplateOriginAsync(p.ProjectFilePath, Data, clone.Value.FormId)).Value);
    }

    [Fact]
    public async Task Models_SharedStat_CopyOnWrite_LeavesOtherSwordAndSharedNifIntact()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var first = (await CloneModelAsync(p, first: @"Custom\Shared.nif")).Value;
        var second = (await CloneModelAsync(p, "SecondSword")).Value;
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        var old = mod.Weapons.First(w => w.FormKey.ID == first.FormId).FirstPersonModel.FormKey;
        mod.Weapons.First(w => w.FormKey.ID == second.FormId).FirstPersonModel.SetTo(old);
        mod.WriteToBinary(p.PluginPath);
        // Reassigning the same path does not allocate even when shared.
        Assert.True((await UpdateModelAsync(p, first.FormId, @"Custom\Shared.nif")).IsSuccess);
        using (var unchanged = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE)) Assert.Single(unchanged.Statics);
        Assert.True((await UpdateModelAsync(p, first.FormId, @"Custom\Replacement.nif")).IsSuccess);
        using var actual = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(2, actual.Statics.Count);
        Assert.Equal(old, actual.Weapons.First(w => w.FormKey.ID == second.FormId).FirstPersonModel.FormKey);
        Assert.NotEqual(old, actual.Weapons.First(w => w.FormKey.ID == first.FormId).FirstPersonModel.FormKey);
        Assert.Equal(@"Custom\Shared.nif", actual.Statics.First(s => s.FormKey == old).Model!.File.GivenPath);
        Assert.Equal(actual.EnumerateMajorRecords().Max(r => r.FormKey.ID) + 1, actual.ModHeader.Stats.NextFormID);
    }

    [Fact]
    public async Task Models_RestoreOriginal_AfterMoveAndReopen_PreservesExistingRecords()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var clone = (await CloneModelAsync(p, first: @"Custom\First.nif")).Value;
        var moved = Path.Combine(_root, "moved-models");
        Directory.Move(Path.GetDirectoryName(p.ProjectFilePath)!, moved);
        p = (await Service.OpenAsync(Path.Combine(moved, WeaponProjectService.ProjectFileName), Data)).Value;
        var template = (await Service.GetTemplateOriginAsync(p.ProjectFilePath, Data, clone.FormId)).Value;
        Assert.Equal(0x12EB7u, template);
        var baseline = (await _builder.GetWeaponModelsAsync(Path.Combine(Data, "Skyrim.esm"), Data, template!.Value)).Value;
        Assert.Equal(@"Weapons\Iron\First.nif", baseline.FirstPersonPath);
        Assert.True((await UpdateModelAsync(p, clone.FormId, null, baseline.FirstPersonReference)).IsSuccess);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x12345), mod.Weapons.First().FirstPersonModel.FormKey);
        Assert.Equal(0x800u, mod.Weapons.First().FormKey.ID);
        Assert.Equal(0x802u, mod.ModHeader.Stats.NextFormID);
        Assert.Single(mod.Statics); // No destructive automatic cleanup of potentially reusable resources.
    }

    [Theory]
    [InlineData("../bad.nif")]
    [InlineData("C:\\bad.nif")]
    [InlineData("a//bad.nif")]
    [InlineData("a/bad.dds")]
    [InlineData("a/bad.nif.")]
    [InlineData("")]
    public async Task Models_UnsafeFirstPath_PreservesPluginAndMetadata(string model)
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var original = File.ReadAllBytes(p.PluginPath);
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        var result = await CloneModelAsync(p, first: model);
        Assert.True(result.IsFailure);
        Assert.Equal(original, File.ReadAllBytes(p.PluginPath));
        Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
    }

    [Fact]
    public async Task Models_LastId_CannotAllocateStat_LeavesPluginAndMetadataUnchanged()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        mod.ModHeader.Stats.NextFormID = 0xFFF;
        mod.BeginWrite.ToPath(p.PluginPath).WithNoLoadOrder().NoNextFormIDProcessing().Write();
        var bytes = File.ReadAllBytes(p.PluginPath);
        var json = File.ReadAllBytes(p.ProjectFilePath);
        Assert.True((await CloneModelAsync(p, first: @"Custom\First.nif")).IsFailure);
        Assert.Equal(bytes, File.ReadAllBytes(p.PluginPath));
        Assert.Equal(json, File.ReadAllBytes(p.ProjectFilePath));
    }

    [Fact]
    public async Task Models_MetadataFailure_PreventsPluginPublication_AndPluginFailureRestoresMetadata()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var original = File.ReadAllBytes(p.PluginPath);
        Directory.CreateDirectory(p.ProjectFilePath + ".bak");
        Assert.True((await CloneModelAsync(p, first: @"Custom\First.nif")).IsFailure);
        Assert.Equal(original, File.ReadAllBytes(p.PluginPath));
        Directory.Delete(p.ProjectFilePath + ".bak");
        var metadata = File.ReadAllText(p.ProjectFilePath);
        Directory.CreateDirectory(p.PluginPath + ".bak");
        Assert.True((await CloneModelAsync(p, first: @"Custom\First.nif")).IsFailure);
        Assert.Equal(original, File.ReadAllBytes(p.PluginPath));
        Assert.Equal(metadata, File.ReadAllText(p.ProjectFilePath));
    }

    [Fact]
    public async Task Models_LegacyProject_UnknownOrigin_AndAbsentWnamRemainSupported()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        File.WriteAllText(p.ProjectFilePath, "{\"Version\":1,\"Name\":\"MySword\",\"PluginFileName\":\"MySword.esp\",\"Resources\":[]}");
        Assert.Null((await Service.GetTemplateOriginAsync(p.ProjectFilePath, Data, 0x800)).Value);
        var cloned = await CloneModelAsync(p);
        Assert.True(cloned.IsSuccess, cloned.Error);
        Assert.True((await UpdateModelAsync(p, cloned.Value.FormId, @"Custom\First.nif")).IsSuccess);
        Assert.True((await UpdateModelAsync(p, cloned.Value.FormId, null, clear: true)).IsSuccess);
        var model = (await _builder.GetWeaponModelsAsync(p.PluginPath, Data, cloned.Value.FormId)).Value;
        Assert.Null(model.FirstPersonReference);
        Assert.True((await Service.OpenAsync(p.ProjectFilePath, Data)).IsSuccess);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("wrong")]
    [InlineData("null")]
    public async Task Models_InvalidWnam_IsDiagnosed(string state)
    {
        var p = (await Service.CreateAsync(Request())).Value;
        var sword = (await CloneModelAsync(p)).Value;
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        var key = new FormKey(mod.ModKey, 0x900);
        if (state == "deleted") mod.Statics.Add(new Static(key, SkyrimRelease.SkyrimSE) { IsDeleted = true });
        if (state == "wrong") mod.Keywords.Add(new Keyword(key, SkyrimRelease.SkyrimSE));
        mod.Weapons.First().FirstPersonModel.SetTo(state == "null" ? FormKey.Null : key);
        mod.WriteToBinary(p.PluginPath);
        var diagnostics = await new PluginValidationService().ValidateAsync(p.PluginPath, Data);
        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Record?.Contains("FirstPersonModel") == true);
        if (state != "null")
        {
            var models = (await _builder.GetWeaponModelsAsync(p.PluginPath, Data, sword.FormId)).Value;
            Assert.NotNull(models.Error);
            Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsFailure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Models_VanillaAvailability_DistinguishesMissingAndUnreadableBsa(bool corrupt)
    {
        var p = (await Service.CreateAsync(Request())).Value;
        if (corrupt) File.WriteAllText(Path.Combine(Data, "Skyrim - Meshes.bsa"), "broken archive");
        var result = await Service.InspectModelAsync(p.ProjectFilePath, @"Weapons\Iron\First.nif", Data, true);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(corrupt ? ModelAvailability.CheckFailed : ModelAvailability.Missing, result.Value.Availability);
        Assert.False(result.Value.IncludedInZip);
        var loose = Path.Combine(Data, "meshes", "Weapons", "Iron", "First.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllText(loose, "vanilla loose");
        Assert.Equal(ModelAvailability.Found, (await Service.InspectModelAsync(p.ProjectFilePath, @"Weapons\Iron\First.nif", Data, true)).Value.Availability);
    }

    public static IEnumerable<object[]> IndependentModelsCases() =>
        from scenario in Enumerable.Range(0, 5)
        from staging in new[] { false, true }
        from esl in new[] { false, true }
        select new object[] { scenario, staging, esl };

    [Theory]
    [MemberData(nameof(IndependentModelsCases))]
    public async Task Models_IndependentAssignments_RoundTripAndVerifiedZip(int scenario, bool staging, bool esl)
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        string? third = null, first = null;
        if (scenario is 1 or 3 or 4)
            third = (await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Custom\Third.nif", "third bytes"), Data)).Value;
        if (scenario is 2 or 3)
            first = (await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Custom\First.nif", "first bytes"), Data)).Value;
        if (scenario == 4) first = third;
        var source = File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm"));
        Assert.True((await CloneModelAsync(p, first: first, third: third)).IsSuccess);
        Assert.True((await Service.OpenAsync(p.ProjectFilePath, Data)).IsSuccess);
        var paths = (await _builder.GetWeaponModelsAsync(p.PluginPath, Data, 0x800)).Value;
        Assert.Equal(third ?? @"Weapons\Iron\Sword.nif", paths.ThirdPersonPath);
        Assert.Equal(first ?? @"Weapons\Iron\First.nif", paths.FirstPersonPath);
        using var provider = PackingProvider();
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(PackingProfile(p, esl), new AppSettings
        { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives"), UseTempDirectory = staging });
        Assert.True(packed.IsSuccess, string.Join("; ", packed.Errors));
        using var zip = ZipFile.OpenRead(packed.OutputArchivePath);
        Assert.Equal(1 + (scenario == 0 ? 0 : scenario == 3 ? 2 : 1), zip.Entries.Count);
        Assert.Equal(zip.Entries.Count, zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".nif")))
        {
            using var input = entry.Open(); using var expected = File.OpenRead(Path.Combine(Path.GetDirectoryName(p.PluginPath)!, entry.FullName));
            using var a = new MemoryStream(); using var b = new MemoryStream(); input.CopyTo(a); expected.CopyTo(b);
            Assert.Equal(b.ToArray(), a.ToArray());
        }
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm")));
    }

    [Fact]
    public async Task Models_MissingOrUnregisteredCustomFirst_BlocksZip()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        Assert.True((await CloneModelAsync(p, first: @"Custom\Missing.nif")).IsSuccess);
        Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsFailure);
        var source = await AssetAsync(@"meshes\Custom\Missing.nif", "model");
        var target = Path.Combine(Path.GetDirectoryName(p.PluginPath)!, "meshes", "Custom", "Missing.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsFailure);
        Assert.True((await Service.ImportModelAsync(p.ProjectFilePath, target, Data)).IsSuccess);
        Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsSuccess);
        File.Delete(target);
        Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsFailure);
    }

    [Fact]
    public async Task Models_RestoreThenCustomize_ReusesPreviousUnsharedManagedStat()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var sword = (await CloneModelAsync(p, first: @"Custom\First.nif")).Value;
        var baseline = (await _builder.GetWeaponModelsAsync(Path.Combine(Data, "Skyrim.esm"), Data, 0x12EB7)).Value;
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await UpdateModelAsync(p, sword.FormId, null, baseline.FirstPersonReference)).IsSuccess);
            Assert.True((await UpdateModelAsync(p, sword.FormId, @"Custom\Changed.nif")).IsSuccess);
        }
        using var mod = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(0x801u, Assert.Single(mod.Statics).FormKey.ID);
        Assert.Equal(0x802u, mod.ModHeader.Stats.NextFormID);
    }

    [Fact]
    public async Task Models_ImportIntoVanillaPath_IsRelocated_WithoutChangingOtherWeapons()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var source = await AssetAsync(@"meshes\Weapons\Iron\First.nif", "custom bytes");
        var imported = await Service.ImportModelAsync(p.ProjectFilePath, source, Data);
        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Equal(@"MySword\Imported\First.nif", imported.Value);
        Assert.Equal("custom bytes", File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(p.PluginPath)!, "meshes", "Weapons", "Iron", "First.nif")));
    }

    [Fact]
    public async Task Models_CustomAvailability_AndCancelledImport_PreserveSharedResource()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        var source = await AssetAsync(@"meshes\Custom\First.nif", "old bytes");
        var imported = (await Service.ImportModelAsync(p.ProjectFilePath, source, Data)).Value;
        var info = (await Service.InspectModelAsync(p.ProjectFilePath, imported, Data, false)).Value;
        Assert.Equal(ModelOrigin.Project, info.Origin);
        Assert.Equal(ModelAvailability.Found, info.Availability);
        Assert.True(info.IncludedInZip);
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        File.WriteAllText(source, "new bytes");
        Assert.True((await Service.ImportModelAsync(p.ProjectFilePath, source, Data)).IsFailure);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ImportModelAsync(p.ProjectFilePath, source, Data, cancelled.Token));
        Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
        Assert.Equal("old bytes", File.ReadAllText(Path.Combine(Path.GetDirectoryName(p.PluginPath)!, info.Path)));
    }

    [Fact]
    public async Task Models_ExactOriginalSnapshot_DetectsChangedVanillaMaster()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var sword = (await CloneModelAsync(p, first: @"Custom\First.nif")).Value;
        var original = await Service.GetOriginalModelsAsync(p.ProjectFilePath, Data, sword.FormId);
        Assert.True(original.IsSuccess, original.Error);
        Assert.Equal(@"Weapons\Iron\First.nif", original.Value!.FirstPersonPath);
        var masterPath = Path.Combine(Data, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(masterPath).Mutable().Construct();
        master.Statics.First().Model!.File = @"Weapons\Changed.nif";
        master.WriteToBinary(masterPath);
        var plugin = File.ReadAllBytes(p.PluginPath);
        var changed = await Service.GetOriginalModelsAsync(p.ProjectFilePath, Data, sword.FormId);
        Assert.True(changed.IsFailure);
        Assert.Contains("changed externally", changed.Error);
        Assert.Equal(plugin, File.ReadAllBytes(p.PluginPath));
    }
}
