using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    [Fact]
    public async Task Bc7_AvailableRealUserTexturesImportAndPackWithoutChangingBytes()
    {
        var root = Environment.GetEnvironmentVariable("SKYRIM_LE_PROJECT"); if (string.IsNullOrWhiteSpace(root)) return;
        var p = (await Service.CreateAsync(Request())).Value; var sources = Directory.GetFiles(Path.Combine(root, "textures"), "*.dds", SearchOption.AllDirectories);
        Assert.Contains(sources, s => DdsResourceInspector.Inspect(s).Format == "BC7");
        var hashes = sources.ToDictionary(s => s, s => SHA256.HashData(File.ReadAllBytes(s)));
        var imported = await Service.ImportTexturesAsync(p.ProjectFilePath, sources, Data); Assert.True(imported.IsSuccess, imported.Error);
        Assert.True((await Service.ValidateResourcesAsync(p.ProjectFilePath, Data)).IsSuccess);
        Assert.True((await Service.GetResourceCatalogAsync(p.ProjectFilePath, Data)).Value.CanPack);
        var archivePath = Path.Combine(_root, "real-bc7.zip");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create)) foreach (var resource in imported.Value) zip.CreateEntryFromFile(Path.Combine(Path.GetDirectoryName(p.ProjectFilePath)!, resource), resource);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var source in sources)
        {
            var relative = imported.Value.Single(p => Path.GetFileName(p).Equals(Path.GetFileName(source), StringComparison.OrdinalIgnoreCase));
            using var stream = archive.GetEntry(relative)!.Open(); Assert.Equal(hashes[source], SHA256.HashData(stream)); Assert.Equal(hashes[source], SHA256.HashData(File.ReadAllBytes(source)));
        }
    }
    [Theory]
    [InlineData(98u)] [InlineData(99u)]
    public async Task Bc7_ImportsValidatesAndPacksUnchanged(uint format)
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var bytes = PreviewFixtures.Bc7(format); var source = Path.Combine(_root, "test-bc7.dds"); File.WriteAllBytes(source, bytes);
        Assert.Equal("BC7", DdsResourceInspector.Inspect(source).Format);
        var imported = await Service.ImportTexturesAsync(project.ProjectFilePath, [source], Data); Assert.True(imported.IsSuccess, imported.Error);
        Assert.True((await Service.ValidateResourcesAsync(project.ProjectFilePath, Data)).IsSuccess);
        Assert.True((await Service.GetResourceCatalogAsync(project.ProjectFilePath, Data)).Value.CanPack);
        Assert.True((await _builder.CloneSwordAsync(new() { PluginPath = project.PluginPath, GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "Bc7Sword", Name = "BC7 sword" })).IsSuccess);
        var files = await Service.GetPackingFilesAsync(project.ProjectFilePath, Data); Assert.True(files.IsSuccess, files.Error);
        var zipPath = Path.Combine(_root, "bc7.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) foreach (var file in files.Value) zip.CreateEntryFromFile(file.AbsolutePath, file.RelativePath.Replace('\\', '/'));
        using var archive = ZipFile.OpenRead(zipPath); using var input = archive.GetEntry(Assert.Single(imported.Value))!.Open();
        using var result = new MemoryStream(); await input.CopyToAsync(result); Assert.Equal(bytes, result.ToArray()); Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("header")] [InlineData("truncated")] [InlineData("format")] [InlineData("mode")] [InlineData("mips")]
    public async Task Bc7_BadResourceReportsFilenameAndReasonWithoutPublishing(string scenario)
    {
        var p = (await Service.CreateAsync(Request())).Value; var bytes = PreviewFixtures.Bc7();
        if (scenario == "header") bytes[0] = 0;
        if (scenario == "truncated") bytes = bytes[..^1];
        if (scenario == "format") BitConverter.GetBytes(97u).CopyTo(bytes, 128);
        if (scenario == "mode") bytes[148] = 0;
        if (scenario == "mips") BitConverter.GetBytes(3u).CopyTo(bytes, 28);
        var source = Path.Combine(_root, "broken-bc7.dds"); File.WriteAllBytes(source, bytes); var before = File.ReadAllBytes(p.ProjectFilePath);
        var result = await Service.ImportTexturesAsync(p.ProjectFilePath, [source], Data); Assert.True(result.IsFailure); Assert.Contains("broken-bc7.dds:", result.Error);
        Assert.Equal(before, File.ReadAllBytes(p.ProjectFilePath)); Assert.Empty((await Service.GetResourcesAsync(p.ProjectFilePath, Data)).Value);
    }
}

public partial class WeaponsViewModelTests
{
    [Theory]
    [InlineData("crafting")] [InlineData("tempering")]
    public async Task SmartRequest_IngredientsTenIronAndLeatherPersistInCobjAndReopen(string kind)
    {
        using var vm = await PrepareSmartAsync(); var path = Path.Combine(DataPath, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct();
        master.MiscItems.Add(new MiscItem(new FormKey(master.ModKey, 0x6F00), SkyrimRelease.SkyrimSE) { EditorID = "LeatherStrips", Name = "Leather strips" }); master.WriteToBinary(path);
        await vm.InitializeAsync(vm.ProjectPath); vm.AddRecipeCommand.Execute(kind);
        var iron = vm.Options.Ingredients.First(o => o.EditorId == "IngotIron"); var leather = vm.Options.Ingredients.First(o => o.EditorId == "LeatherStrips");
        vm.AddOrIncrementIngredient(iron); vm.SelectedRecipe!.Ingredients[0].Count = "9"; vm.AddOrIncrementIngredient(iron);
        Assert.Equal("10", Assert.Single(vm.SelectedRecipe.Ingredients).Count); vm.AddOrIncrementIngredient(leather);
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        var recipe = Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, vm.SelectedItem!.ConfirmedFormId!.Value)).Value);
        Assert.Equal(10, recipe.Ingredients.Single(i => i.Reference == iron.Reference).Count); Assert.Equal(1, recipe.Ingredients.Single(i => i.Reference == leather.Reference).Count);
        using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal("", reopened.Error);
        Assert.Equal("10", reopened.SelectedRecipe!.Ingredients.Single(i => i.Reference == iron.Reference).Count); Assert.Equal(2, reopened.SelectedRecipe.Ingredients.Count);
    }

    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("1.5")] [InlineData("1,5")] [InlineData("2147483648")]
    public async Task SmartRequest_InvalidIngredientCountCannotWrite(string count)
    {
        using var vm = await PrepareSmartAsync(); vm.AddRecipeCommand.Execute("crafting"); vm.AddOrIncrementIngredient(vm.Options.Ingredients.First());
        var before = File.ReadAllBytes(vm.PluginPath); vm.SelectedRecipe!.Ingredients[0].Count = count; Assert.NotEmpty(vm.SelectedRecipe.Ingredients[0].QuantityError);
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.NotEmpty(vm.Error); Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
    }

    [Fact]
    public async Task SmartRequest_SharedFirstAndThirdModelProducesOneNifInZip()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplicationServices(); services.AddInfrastructureServices(Path.Combine(_root, "shared-pipeline"));
        using var provider = services.BuildServiceProvider(); using var vm = await PrepareSmartAsync(provider.GetRequiredService<PackingPipeline>());
        var source = Path.Combine(_root, "one-model.nif"); var bytes = PreviewFixtures.Nif(diffuse: ""); File.WriteAllBytes(source, bytes);
        await vm.ImportModelAsync(source, false); Assert.Equal("", vm.Error); var model = vm.SelectedItem!.ModelPath;
        foreach (var item in vm.Items) { item.ModelPath = model; item.UseSharedModel = true; Assert.Equal(model, item.FirstPersonPath); }
        await vm.PackCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.NotNull(vm.LastArchivePath);
        foreach (var item in vm.Items)
        { var models = (await _builder.GetWeaponModelsAsync(vm.PluginPath, DataPath, item.ConfirmedFormId!.Value)).Value; Assert.Equal(models.ThirdPersonPath, models.FirstPersonPath); }
        using var zip = ZipFile.OpenRead(vm.LastArchivePath); Assert.Single(zip.Entries, e => e.FullName.EndsWith(".nif"));
        Assert.Equal(bytes, File.ReadAllBytes(source));
        using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.All(reopened.Items, i => Assert.True(i.UseSharedModel));
    }
}

public partial class AdvancedPluginBuilderTests
{
    [Fact]
    public async Task SmartRequest_AvailableRealGameCatalogHasVerifiedListUses()
    {
        var data = Environment.GetEnvironmentVariable("SKYRIM_SE_DATA"); if (string.IsNullOrWhiteSpace(data)) return;
        var hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "Skyrim.esm")));
        var result = await _builder.GetWeaponOptionsAsync(data); Assert.True(result.IsSuccess, result.Error);
        var lists = result.Value.LeveledLists;
        foreach (var category in new[] { "Containers", "Merchants", "Equipment" }) Assert.Contains(lists, l => l.ListUsages.Any(u => u.Category == category));
        Assert.Contains(lists, l => l.ListUsages.Count == 0); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "Skyrim.esm"))));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/audit")); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "real-leveled-list-catalog.json"), JsonSerializer.Serialize(new
        { total = lists.Count, categories = new[] { "Containers", "Merchants", "Equipment" }.ToDictionary(c => c, c => lists.Count(l => l.ListUsages.Any(u => u.Category == c))),
            unknown = lists.Count(l => l.ListUsages.Count == 0), samples = lists.Where(l => l.ListUsages.Count > 0).Take(10) }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [Fact]
    public async Task SmartRequest_ListPurposeComesFromReferencesAndSaveIsIdempotent()
    {
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(MasterPath).Mutable().Construct();
        var container = new Container(new FormKey(Skyrim, 0x3000), SkyrimRelease.SkyrimSE) { EditorID = "UnrelatedName", Name = "Training chest", Items = [] };
        var contents = new ContainerEntry { Item = new ContainerItem { Count = 1 } }; contents.Item.Item.SetTo(new FormKey(Skyrim, 0x2000)); container.Items.Add(contents); master.Containers.Add(container);
        master.LeveledItems.Add(new LeveledItem(new FormKey(Skyrim, 0x3001), SkyrimRelease.SkyrimSE) { EditorID = "VendorLootEquipmentUnknown", Entries = [] }); master.WriteToBinary(MasterPath, new BinaryWriteParameters { LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed() });
        await PrepareAsync(); var options = (await _builder.GetWeaponOptionsAsync(Data)).Value;
        var list = options.LeveledLists.Single(l => l.EditorId == "LItemSword"); Assert.Equal("Containers", Assert.Single(list.ListUsages).Category); Assert.Contains("Training chest", list.DisplayName);
        Assert.Empty(options.LeveledLists.Single(l => l.EditorId == "VendorLootEquipmentUnknown").ListUsages);
        var weapon = Assert.Single((await _builder.GetWeaponsAsync(Plugin)).Value);
        var request = new LeveledListRequest { PluginPath = Plugin, GameDataPath = Data, WeaponFormId = weapon.FormId, List = list.Reference, Level = 12, Count = 3, UpdateExisting = true };
        Assert.True((await _builder.AddToLeveledListAsync(request)).IsSuccess); var before = File.ReadAllBytes(Plugin);
        Assert.True((await _builder.AddToLeveledListAsync(request)).IsSuccess); Assert.Equal(before, File.ReadAllBytes(Plugin));
        var details = (await _builder.GetLeveledListDetailsAsync(Plugin, Data, list.Reference)).Value;
        var added = Assert.Single(details.Entries, e => e.Reference?.FormId == weapon.FormId && e.Reference.PluginName == "Sword.esp"); Assert.Equal(12, added.Level); Assert.Equal(3, added.Count);
        Assert.True((await _builder.AddToLeveledListAsync(new() { PluginPath = Plugin, GameDataPath = Data, WeaponFormId = weapon.FormId, List = list.Reference, Level = 20, Count = 10, UpdateExisting = true })).IsSuccess);
        details = (await _builder.GetLeveledListDetailsAsync(Plugin, Data, list.Reference)).Value;
        added = Assert.Single(details.Entries, e => e.Reference?.PluginName == "Sword.esp"); Assert.Equal(20, added.Level); Assert.Equal(10, added.Count);
    }
}
