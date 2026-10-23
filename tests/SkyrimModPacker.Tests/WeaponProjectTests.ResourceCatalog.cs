using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    private async Task<ProjectResourceCatalog> CatalogAsync(WeaponProject project)
    {
        var result = await Service.GetResourceCatalogAsync(project.ProjectFilePath, Data);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value;
    }

    private static void SetCatalogResources(WeaponProject project, params string[] resources)
    {
        var doc = JsonNode.Parse(File.ReadAllText(project.ProjectFilePath))!;
        doc["Resources"] = new JsonArray(resources.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        File.WriteAllText(project.ProjectFilePath, doc.ToJsonString());
    }

    [Fact]
    public async Task ResourceCatalog_ListsEveryRegisteredFile_WithoutInferringTextureUseOrWritingMetadata()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        var nif = (await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Custom\unused.nif"), Data)).Value;
        Assert.True((await Service.ImportTexturesAsync(p.ProjectFilePath, [await AssetAsync(@"textures\Custom\blade.dds")], Data)).IsSuccess);
        var json = File.ReadAllBytes(p.ProjectFilePath); var plugin = File.ReadAllBytes(p.PluginPath);
        var files = Directory.GetFiles(Path.GetDirectoryName(p.ProjectFilePath)!, "*", SearchOption.AllDirectories).Order().ToArray();
        var catalog = await CatalogAsync(p);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.All(catalog.Entries, e =>
        {
            Assert.Equal(ModelOrigin.Project, e.Origin); Assert.Equal(ModelAvailability.Found, e.Availability);
            Assert.Equal(ResourceArchiveStatus.Included, e.ArchiveStatus); Assert.True(e.IsRegistered);
            Assert.Empty(e.Uses); Assert.Empty(e.Issues);
        });
        Assert.True(catalog.Entries.Single(e => e.Category == FileCategory.Mesh).UsageIsKnown);
        Assert.False(catalog.Entries.Single(e => e.Category == FileCategory.Texture).UsageIsKnown);
        Assert.True(catalog.CanPack);
        Assert.Equal(json, File.ReadAllBytes(p.ProjectFilePath)); Assert.Equal(plugin, File.ReadAllBytes(p.PluginPath));
        Assert.Equal(files, Directory.GetFiles(Path.GetDirectoryName(p.ProjectFilePath)!, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task ResourceCatalog_SharedNifAndStat_ReportAllSavedUsers_OncePerResource()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var nif = (await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Custom\shared.nif"), Data)).Value;
        var first = (await CloneModelAsync(p, first: nif, third: nif)).Value;
        var second = (await CloneModelAsync(p, "SecondSword", third: nif)).Value;
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        var statKey = mod.Weapons.First(w => w.FormKey.ID == first.FormId).FirstPersonModel.FormKey;
        mod.Weapons.First(w => w.FormKey.ID == second.FormId).FirstPersonModel.SetTo(statKey);
        mod.WriteToBinary(p.PluginPath);
        var catalog = await CatalogAsync(p);
        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("meshes/Custom/shared.nif", entry.RelativePath);
        Assert.Equal(5, entry.Uses.Count); // Two third-person + two indirect first-person + one direct STAT.
        Assert.Equal(2, entry.Uses.Count(u => u.Kind == ResourceUseKind.FirstPerson));
        Assert.All(entry.Uses.Where(u => u.Kind == ResourceUseKind.FirstPerson), u => Assert.Equal(statKey.ID, u.ViaRecord!.FormId));
        Assert.Empty(entry.Issues); Assert.True(catalog.CanPack);
    }

    [Fact]
    public async Task ResourceCatalog_VanillaModels_ShowPathsAndRecords_AndAreNeverAddedToArchive()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        Assert.True((await CloneModelAsync(p)).IsSuccess);
        var catalog = await CatalogAsync(p);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.All(catalog.Entries, e =>
        {
            Assert.Equal(ModelOrigin.Vanilla, e.Origin); Assert.False(e.IsRegistered);
            Assert.Equal(ResourceArchiveStatus.VanillaExcluded, e.ArchiveStatus);
            Assert.Equal(ModelAvailability.Missing, e.Availability); // Synthetic master has no meshes/BSA.
            Assert.False(e.BlocksPacking); Assert.NotEmpty(e.Uses);
        });
        Assert.Contains(catalog.Entries, e => e.Uses.Any(u => u.Record.PluginName == "Skyrim.esm" && u.Kind == ResourceUseKind.Static));
        Assert.Single((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).Value);
        Assert.Empty((await Service.GetResourcesAsync(p.ProjectFilePath, Data)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceCatalog_UnregisteredCustomModel_IsVisible_AndCannotDisappearFromZip(bool fileExists)
    {
        var p = (await Service.CreateAsync(Request())).Value;
        Assert.True((await CloneModelAsync(p, third: @"Custom\missing.nif")).IsSuccess);
        if (fileExists)
        {
            var target = Path.Combine(Path.GetDirectoryName(p.PluginPath)!, "meshes", "Custom", "missing.nif");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "not registered");
        }
        var catalog = await CatalogAsync(p);
        var entry = Assert.Single(catalog.Entries);
        Assert.False(entry.IsRegistered); Assert.True(entry.BlocksPacking);
        Assert.Contains(ResourceIssue.UnregisteredCustom, entry.Issues);
        Assert.Equal(fileExists ? ModelAvailability.Found : ModelAvailability.Missing, entry.Availability);
        Assert.Single(entry.Uses); Assert.False(catalog.CanPack);
        Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsFailure);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("locked")]
    public async Task ResourceCatalog_RequiredDds_MissingEmptyOrUnreadable_BlocksPacking(string state)
    {
        var p = (await Service.CreateAsync(Request())).Value;
        await CloneModelAsync(p);
        var source = await AssetAsync(@"textures\Custom\blade.dds");
        Assert.True((await Service.ImportTexturesAsync(p.ProjectFilePath, [source], Data)).IsSuccess);
        var target = Path.Combine(Path.GetDirectoryName(p.PluginPath)!, "textures", "Custom", "blade.dds");
        if (state == "missing") File.Delete(target);
        if (state == "empty") File.WriteAllText(target, "");
        using var locked = state == "locked" ? new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var catalog = await CatalogAsync(p);
        var entry = catalog.Entries.Single(e => e.Category == FileCategory.Texture);
        Assert.False(entry.UsageIsKnown); Assert.True(entry.IsRegistered); Assert.True(entry.BlocksPacking);
        Assert.Equal(state == "locked" ? ModelAvailability.CheckFailed : ModelAvailability.Missing, entry.Availability);
        Assert.Contains(state == "locked" ? ResourceIssue.ReadFailed : ResourceIssue.MissingOrEmpty, entry.Issues);
        Assert.False(catalog.CanPack);
        Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsFailure);
    }

    [Fact]
    public async Task ResourceCatalog_DuplicateAliases_AreReportedAndDeduplicated_WhileDistinctDirectoriesRemainDistinct()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\One\blade.nif"), Data);
        await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Two\blade.nif"), Data);
        SetCatalogResources(p, "meshes/One/blade.nif", @"MESHES\ONE\BLADE.NIF", "meshes/Two/blade.nif");
        var catalog = await CatalogAsync(p);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Single(catalog.Entries, e => e.Issues.Contains(ResourceIssue.DuplicateRegistration));
        Assert.True(catalog.CanPack);
        Assert.All(catalog.Entries, e => Assert.Equal(ResourceArchiveStatus.Included, e.ArchiveStatus));
    }

    [Fact]
    public async Task ResourceCatalog_FileDirectoryCollision_IsExplicit_AndBlocksArchive()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        SetCatalogResources(p, "meshes/A.nif", "meshes/A.nif/child.nif");
        var catalog = await CatalogAsync(p);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.All(catalog.Entries, e => Assert.Contains(ResourceIssue.ArchivePathConflict, e.Issues));
        Assert.False(catalog.CanPack);
    }

    [Theory]
    [InlineData("meshes/../../outside.nif")]
    [InlineData("C:/outside.nif")]
    [InlineData("meshes/Custom/wrong.dds")]
    [InlineData("textures/Custom/wrong.nif")]
    [InlineData("meshes/a./file.nif")]
    public async Task ResourceCatalog_UnsafeRegistration_IsDisplayedWithoutReadingItsTarget(string path)
    {
        var p = (await Service.CreateAsync(Request())).Value;
        var outside = Path.Combine(_root, "outside.nif"); File.WriteAllText(outside, "unchanged");
        SetCatalogResources(p, path);
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        var catalog = await CatalogAsync(p);
        var entry = Assert.Single(catalog.Entries);
        Assert.Contains(ResourceIssue.InvalidPath, entry.Issues);
        Assert.Equal(ModelAvailability.CheckFailed, entry.Availability); Assert.True(entry.BlocksPacking);
        Assert.False(catalog.CanPack);
        Assert.Equal("unchanged", File.ReadAllText(outside)); Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
    }

    [Fact]
    public async Task ResourceCatalog_CorruptBsa_IsCheckFailed_NotMissing_AndNotIncluded()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        await CloneModelAsync(p);
        File.WriteAllText(Path.Combine(Data, "Skyrim - Meshes.bsa"), "corrupt");
        var catalog = await CatalogAsync(p);
        Assert.All(catalog.Entries, e =>
        {
            Assert.Equal(ModelAvailability.CheckFailed, e.Availability);
            Assert.Contains(ResourceIssue.ReadFailed, e.Issues);
            Assert.DoesNotContain(ResourceIssue.MissingOrEmpty, e.Issues);
            Assert.Equal(ResourceArchiveStatus.VanillaExcluded, e.ArchiveStatus);
        });
    }

    [Fact]
    public async Task ResourceCatalog_LocalOverrideOfVanillaStat_IsResolvedAsCustom_AndCannotHideBehindMasterFormKey()
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        await CloneModelAsync(p);
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        mod.Statics.Add(new Static(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x12345), SkyrimRelease.SkyrimSE)
        { EditorID = "OverrideFirst", Model = new Model { File = @"Custom\override.nif" } });
        mod.WriteToBinary(p.PluginPath);
        var catalog = await CatalogAsync(p);
        var custom = catalog.Entries.Single(e => e.RelativePath == "meshes/Custom/override.nif");
        Assert.Equal(ModelOrigin.Unknown, custom.Origin); Assert.True(custom.BlocksPacking);
        Assert.Equal(2, custom.Uses.Count); Assert.False(catalog.CanPack);
    }

    [Fact]
    public async Task ResourceCatalog_ProjectFileAtVanillaPath_IsFlaggedAsShadow_NotMislabelledVanilla()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        await CloneModelAsync(p);
        var resource = "meshes/Weapons/Iron/Sword.nif";
        var target = Path.Combine(Path.GetDirectoryName(p.PluginPath)!, resource);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "legacy custom file");
        SetCatalogResources(p, resource);
        var entry = Assert.Single((await CatalogAsync(p)).Entries);
        Assert.Equal(ModelOrigin.Project, entry.Origin);
        Assert.Contains(ResourceIssue.VanillaShadow, entry.Issues);
        Assert.Equal(ResourceArchiveStatus.Included, entry.ArchiveStatus); // Explicitly registered legacy assets remain supported.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceCatalog_UnresolvedOrDeletedFirstPersonStat_IsAnExplicitBlockingDiagnostic(bool deleted)
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        await CloneModelAsync(p);
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        var key = new FormKey(mod.ModKey, 0x900);
        if (deleted) mod.Statics.Add(new Static(key, SkyrimRelease.SkyrimSE) { IsDeleted = true });
        mod.Weapons.First().FirstPersonModel.SetTo(key); mod.WriteToBinary(p.PluginPath);
        var catalog = await CatalogAsync(p);
        Assert.Contains("WNAM", Assert.Single(catalog.Diagnostics).Record);
        Assert.False(catalog.CanPack);
        Assert.True((await Service.GetPackingFilesAsync(p.ProjectFilePath, Data)).IsFailure);
    }

    [Fact]
    public async Task ResourceCatalog_CancelledRead_HasNoFileSideEffects()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        var json = File.ReadAllBytes(p.ProjectFilePath); var plugin = File.ReadAllBytes(p.PluginPath);
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.GetResourceCatalogAsync(p.ProjectFilePath, Data, token.Token));
        Assert.Equal(json, File.ReadAllBytes(p.ProjectFilePath)); Assert.Equal(plugin, File.ReadAllBytes(p.PluginPath));
    }

    [Fact]
    public async Task ResourceCatalog_UnicodeRegisteredPaths_AreListedWithoutLoss()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        var model = await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Власні\клинок.nif"), Data);
        var texture = await Service.ImportTexturesAsync(p.ProjectFilePath, [await AssetAsync(@"textures\Власні\клинок.dds")], Data);
        Assert.True(model.IsSuccess, model.Error); Assert.True(texture.IsSuccess, texture.Error);
        var catalog = await CatalogAsync(p);
        Assert.Contains(catalog.Entries, e => e.RelativePath == "meshes/Власні/клинок.nif");
        Assert.Contains(catalog.Entries, e => e.RelativePath == "textures/Власні/клинок.dds");
        Assert.All(catalog.Entries, e => { Assert.Equal(ModelAvailability.Found, e.Availability); Assert.Empty(e.Issues); });
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("deleted")]
    [InlineData("wrong-type")]
    public async Task ResourceCatalog_LastDeclaredMaster_WinsForFirstPersonAssignments(string state)
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        await CloneModelAsync(p);
        var patch = new SkyrimMod(ModKey.FromNameAndExtension("Patch.esm"), SkyrimRelease.SkyrimSE) { IsMaster = true };
        patch.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension("Skyrim.esm") });
        var key = new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x12345);
        if (state == "wrong-type") patch.Keywords.Add(new Keyword(key, SkyrimRelease.SkyrimSE));
        else patch.Statics.Add(new Static(key, SkyrimRelease.SkyrimSE)
        { EditorID = "PatchFirstPerson", IsDeleted = state == "deleted", Model = new Model { File = @"Custom\patch-first.nif" } });
        patch.BeginWrite.ToPath(Path.Combine(Data, "Patch.esm")).WithLoadOrderFromHeaderMasters()
            .WithDataFolder(Data).NoMastersListContentCheck().Write();
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = patch.ModKey });
        mod.BeginWrite.ToPath(p.PluginPath).WithLoadOrderFromHeaderMasters().WithDataFolder(Data).NoMastersListContentCheck().Write();
        var catalog = await CatalogAsync(p);
        if (state == "changed")
        {
            Assert.Empty(catalog.Diagnostics);
            Assert.Contains(catalog.Entries, e => e.RelativePath == "meshes/Custom/patch-first.nif" && e.BlocksPacking);
        }
        else Assert.Single(catalog.Diagnostics);
        Assert.DoesNotContain(catalog.Entries, e => e.RelativePath == "meshes/Weapons/Iron/First.nif");
        Assert.False(catalog.CanPack);
    }

    [Fact]
    public async Task ResourceCatalog_ExplicitNullWnam_IsNotMisreportedAsAbsentAssignment()
    {
        var p = (await Service.CreateAsync(Request())).Value;
        await CloneModelAsync(p);
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
        mod.Weapons.First().FirstPersonModel.SetTo(FormKey.Null); mod.WriteToBinary(p.PluginPath);
        var catalog = await CatalogAsync(p);
        Assert.Contains("NULL", Assert.Single(catalog.Diagnostics).Detail);
        Assert.False(catalog.CanPack);
    }
}
