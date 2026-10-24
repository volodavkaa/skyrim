using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SmartMod_SavedRecipeConflictHasBackup_AndFailedPersistenceRestoresPlugin(bool fail)
    {
        using var original = await PrepareSmartAsync(); original.AddRecipeCommand.Execute("crafting"); original.AddIngredientCommand.Execute(null);
        original.SelectedRecipe!.Ingredients[0].Reference = original.Options.Ingredients.First().Reference;
        await original.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", original.Error);
        var weapon = original.SelectedItem!.ConfirmedFormId!.Value;
        var saved = Assert.Single((await _builder.GetRecipesAsync(original.PluginPath, weapon)).Value);
        var added = await _builder.SaveRecipeAsync(new() { PluginPath = original.PluginPath, GameDataPath = DataPath, Recipe = new()
        { WeaponFormId = weapon, EditorId = "ManualLegacySecond", Workbench = saved.Workbench, Ingredients = saved.Ingredients } });
        Assert.True(added.IsSuccess, added.Error);
        var inner = new JsonSmartModDraftRepository(); var repository = Substitute.For<ISmartModDraftRepository>(); var armed = false;
        repository.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c => inner.LoadAsync(c.ArgAt<string>(0), c.ArgAt<string>(1), c.Arg<CancellationToken>()));
        repository.SaveAsync(Arg.Any<SmartModDraft>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            var draft = c.Arg<SmartModDraft>();
            if (armed && draft.Items.Sum(i => i.Recipes.Count) == 1) { armed = false; return Task.FromResult(Result<DraftWriteReceipt>.Failure("Injected draft failure")); }
            return inner.SaveAsync(draft, c.Arg<string>(), c.Arg<long>(), c.Arg<CancellationToken>());
        });
        using var vm = new SmartModViewModel(_builder, new WeaponProjectService(_builder), _settings, repository, new PluginValidationService(),
            history: new WorkspaceHistoryService(Path.Combine(_root, "conflict-history")));
        await vm.InitializeAsync(original.ProjectPath); await vm.ReviewExternalChangesCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
        Assert.True(vm.HasRecipeConflict); vm.SelectedRecipe = vm.SelectedItem!.Recipes.First(r => r.EditorId == saved.EditorId);
        var before = File.ReadAllBytes(vm.PluginPath); armed = fail;
        await vm.ResolveRecipeConflictAsync();
        Assert.Equal(fail, vm.Error.Length > 0); Assert.Equal(fail, vm.HasRecipeConflict);
        Assert.Equal(fail ? 2 : 1, (await _builder.GetRecipesAsync(vm.PluginPath, weapon)).Value.Count);
        if (fail) Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "conflict-history"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SmartMod_TextureFolderCompletesQueueAndZipWithSharedResources(bool esl)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplicationServices(); services.AddInfrastructureServices(Path.Combine(_root, "folder-pipeline"));
        using var provider = services.BuildServiceProvider(); using var vm = await PrepareSmartAsync(provider.GetRequiredService<PackingPipeline>());
        var folder = Path.Combine(_root, "folder-source"); Directory.CreateDirectory(Path.Combine(folder, "nested"));
        var texture = PreviewFixtures.Dds(); File.WriteAllBytes(Path.Combine(folder, "nested", "blade.dds"), texture);
        var plan = await vm.PreviewTextureFolderAsync(folder, "Weapons/Shared"); await vm.ImportTextureFolderAsync(plan); Assert.Equal("", vm.Error);
        plan = await vm.PreviewTextureFolderAsync(folder, "Weapons/Shared"); await vm.ImportTextureFolderAsync(plan); Assert.Equal("", vm.Error);
        foreach (var item in vm.Items.ToArray())
        { vm.SelectedItem = item; vm.AddRecipeCommand.Execute("crafting"); vm.AddRecipeCommand.Execute("crafting"); vm.AddIngredientCommand.Execute(null); vm.SelectedRecipe!.Ingredients[0].Reference = vm.Options.Ingredients.First().Reference; }
        vm.EnableEsl = esl; await vm.PackCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.NotNull(vm.LastArchivePath);
        using var zip = ZipFile.OpenRead(vm.LastArchivePath); var entry = Assert.Single(zip.Entries, e => e.FullName == "textures/Weapons/Shared/nested/blade.dds");
        using var input = entry.Open(); using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); Assert.Equal(texture, bytes.ToArray());
        foreach (var item in vm.Items) Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, item.ConfirmedFormId!.Value)).Value);
    }
    [Fact]
    public async Task SmartMod_RecipeActionsReuseIdentity_AndLegacyConflictsRequireExplicitResolution()
    {
        using var vm = await PrepareSmartAsync();
        vm.AddRecipeCommand.Execute("crafting"); var first = vm.SelectedRecipe!;
        vm.AddRecipeCommand.Execute("crafting"); Assert.Same(first, vm.SelectedRecipe); Assert.Single(vm.SelectedItem!.Recipes);
        vm.AddRecipeCommand.Execute("tempering"); vm.AddRecipeCommand.Execute("tempering");
        Assert.Equal(2, vm.SelectedItem.Recipes.Count);
        var duplicate = new SmartModRecipeRow(first.Capture() with { Id = Guid.NewGuid(), EditorId = "LegacyDuplicate" });
        vm.SelectedItem.Recipes.Add(duplicate);
        Assert.True(vm.HasRecipeConflict); Assert.True(await vm.SaveDraftAsync());
        using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath);
        Assert.True(reopened.HasRecipeConflict); Assert.Equal(3, reopened.SelectedItem!.Recipes.Count);
        var before = File.ReadAllBytes(reopened.PluginPath);
        await reopened.SaveItemCommand.ExecuteAsync(null); Assert.NotEmpty(reopened.Error);
        Assert.Equal(before, File.ReadAllBytes(reopened.PluginPath));
        reopened.SelectedRecipe = reopened.SelectedItem.Recipes.Single(r => r.Id == first.Id);
        await reopened.ResolveRecipeConflictAsync(); Assert.Equal("", reopened.Error);
        Assert.False(reopened.HasRecipeConflict); Assert.Equal(2, reopened.SelectedItem.Recipes.Count);
        Assert.Contains(reopened.SelectedItem.Recipes, r => r.Id == first.Id);
    }
}

public partial class WeaponProjectTests
{
    [Fact]
    public async Task TextureFolderPlan_MapsContents_PreservesNestedPaths_DeduplicatesAndRejectsStaleSources()
    {
        var source = await AssetAsync(@"arbitrary\nested\blade.dds", "diffuse");
        await AssetAsync(@"arbitrary\notes.txt");
        var folder = Path.Combine(_root, "assets", "arbitrary"); var project = (await Service.CreateAsync(Request())).Value;
        var plan = (await Service.PreviewTextureFolderAsync(project.ProjectFilePath, folder, "Weapons/MyMod", Data)).Value;
        Assert.True(plan.CanImport); Assert.Equal(1, plan.SkippedFiles);
        Assert.Equal("textures/Weapons/MyMod/nested/blade.dds", Assert.Single(plan.Items).RelativePath);
        Assert.True((await Service.ImportTextureFolderAsync(plan, Data)).IsSuccess);
        plan = (await Service.PreviewTextureFolderAsync(project.ProjectFilePath, folder, "Weapons/MyMod", Data)).Value;
        Assert.True(Assert.Single(plan.Items).Identical);
        Assert.True((await Service.ImportTextureFolderAsync(plan, Data)).IsSuccess);
        plan = (await Service.PreviewTextureFolderAsync(project.ProjectFilePath, folder, "New", Data)).Value;
        await File.WriteAllBytesAsync(source, PreviewFixtures.ResourceDds("changed"));
        var manifest = File.ReadAllBytes(project.ProjectFilePath);
        Assert.True((await Service.ImportTextureFolderAsync(plan, Data)).IsFailure);
        Assert.Equal(manifest, File.ReadAllBytes(project.ProjectFilePath));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(project.ProjectFilePath)!, "textures", "New")));
        plan = (await Service.PreviewTextureFolderAsync(project.ProjectFilePath, folder, "Weapons/MyMod", Data)).Value;
        Assert.False(plan.CanImport); Assert.NotNull(Assert.Single(plan.Items).Conflict);
    }
    [Theory]
    [InlineData("../escape")] [InlineData("/root")] [InlineData("C:/root")] [InlineData("a/../../root")]
    public async Task TextureFolderPlan_RejectsUnsafeMapping(string mapping)
    {
        var file = await AssetAsync(@"folder\a.dds"); var project = (await Service.CreateAsync(Request())).Value;
        Assert.True((await Service.PreviewTextureFolderAsync(project.ProjectFilePath, Path.GetDirectoryName(file)!, mapping, Data)).IsFailure);
    }
}

public sealed class SmartMaterialTests
{
    [Fact]
    public void MaterialIndexUsesExactEligibleTypes_CategoriesAliasesAndBoundedResults()
    {
        Assert.Equal("", CraftingMaterialCatalog.Classify("IngotIron", false).Category);
        Assert.Equal("", CraftingMaterialCatalog.Classify("IronSword", true).Category);
        var material = CraftingMaterialCatalog.Classify("IngotIron", true);
        var items = Enumerable.Range(1, 20000).Select(i => new RecordOption { Reference = new("Skyrim.esm", (uint)i), EditorId = "Other" + i }).ToList();
        items.Add(new() { Reference = new("Skyrim.esm", 30000), EditorId = "IngotIron", Name = "Iron ingot", MaterialCategory = material.Category, SearchAliases = material.Aliases });
        var index = new MaterialSearchIndex(items);
        Assert.Single(index.Search("").Items); Assert.Single(index.Search("залізо").Items);
        Assert.Empty(index.Search("", "Leather").Items);
        Assert.Equal(50, index.Search("", includeOther: true).Items.Count);
        var time = Stopwatch.StartNew(); for (var i = 0; i < 30; i++) index.Search("Other 199", includeOther: true);
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(3), time.Elapsed.ToString());
    }
}

public sealed class LeNifTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LeSeparateGeometryAndStripsPreserveTransformsUvAndAllShaderSlots(bool strips)
    {
        var bytes = LeFixture(strips); var copy = bytes.ToArray(); var scene = SseNifReader.Read(bytes); var mesh = Assert.Single(scene.Meshes);
        Assert.Equal(new Vector3(4, 3, 4), mesh.Positions[1]); Assert.Equal(new Vector2(1, 0), mesh.TextureCoordinates[1]);
        Assert.Equal(strips ? new[] { 0, 1, 2, 2, 1, 3 } : new[] { 0, 1, 2 }, mesh.Indices); Assert.Equal(2, scene.TexturePaths.Count); Assert.True(scene.TextureDependenciesComplete);
        Assert.Equal(copy, bytes);
        Assert.ThrowsAny<Exception>(() => SseNifReader.Read(bytes[..^10]));
    }
    [Theory]
    [InlineData("skin")] [InlineData("animated")] [InlineData("nan")] [InlineData("index")] [InlineData("boolean")]
    public void LeUnsupportedAndMalformedGeometryIsRejected(string scenario) => Assert.ThrowsAny<Exception>(() => SseNifReader.Read(LeFixture(false, scenario)));

    // Opt-in local acceptance assets remain outside source control; no copyrighted meshes are redistributed.
    [Fact]
    public void AvailableRealStaticNifFixtures_AreReadWithoutModification()
    {
        var root = Environment.GetEnvironmentVariable("SKYRIM_LE_FIXTURES"); if (string.IsNullOrWhiteSpace(root)) return;
        var results = new List<object>();
        foreach (var file in Directory.GetFiles(root, "*.nif", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file); var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var scene = SseNifReader.Read(bytes); Assert.NotEmpty(scene.Meshes);
            var points = scene.Meshes.SelectMany(m => m.Positions).ToArray();
            Assert.All(points, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            results.Add(new { file, format = NifCompatibilityInspector.Read(file).Family.ToString(), sha256 = hash, meshes = scene.Meshes.Count, vertices = points.Length,
                triangles = scene.Meshes.Sum(m => m.Indices.Length / 3), textures = scene.TexturePaths,
                dependenciesComplete = scene.TextureDependenciesComplete,
                min = new[] { points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z) },
                max = new[] { points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z) } });
        }
        Assert.NotEmpty(results);
        File.WriteAllText(Path.Combine(root, "static-preview-report.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static byte[] LeFixture(bool strips, string scenario = "valid")
    {
        byte[] Block(Action<BinaryWriter> action) { using var m = new MemoryStream(); using var w = new BinaryWriter(m); action(w); return m.ToArray(); }
        void Text(BinaryWriter w, string text) { var b = Encoding.UTF8.GetBytes(text); w.Write((uint)b.Length); w.Write(b); }
        void Net(BinaryWriter w) { w.Write(-1); w.Write(0u); w.Write(scenario == "animated" ? 0 : -1); }
        var blocks = new[] {
            Block(w => { Net(w); w.Write(14u); foreach (var f in new[] {2f,3,4,1,0,0,0,1,0,0,0,1,2}) w.Write(f); w.Write(-1);
                w.Write(1); w.Write(scenario == "skin" ? 0 : -1); w.Write(0u); w.Write(-1); w.Write(false); w.Write(2); w.Write(-1); }),
            Block(w => { w.Write(0); w.Write((ushort)(strips ? 4 : 3)); w.Write((byte)0); w.Write((byte)0); w.Write(scenario == "boolean" ? (byte)2 : (byte)1);
                foreach (var f in new[] {scenario == "nan" ? float.NaN : 0f,0,0,1,0,0,0,1,0}) w.Write(f);
                if (strips) { w.Write(1f); w.Write(1f); w.Write(0f); }
                w.Write((ushort)1); w.Write(0u); w.Write(false); foreach (var f in new[] {0f,0,0,1}) w.Write(f); w.Write(false);
                foreach (var f in new[] {0f,0,1,0,0,1}) w.Write(f); if (strips) { w.Write(1f); w.Write(1f); } w.Write((ushort)0); w.Write(-1); w.Write((ushort)(strips ? 2 : 1));
                if (strips) { w.Write((ushort)1); w.Write((ushort)4); w.Write(true); }
                else { w.Write(3u); w.Write(true); }
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)(scenario == "index" ? 3 : 2)); if (strips) w.Write((ushort)3); else w.Write((ushort)0); }),
            Block(w => { w.Write(0u); Net(w); w.Write(0u); w.Write(0u); foreach (var f in new[] {0f,0,1,1}) w.Write(f); w.Write(3); }),
            Block(w => { w.Write(2u); Text(w, "textures/a.dds"); Text(w, "textures/a_n.dds"); }) };
        return Block(w => { w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n")); w.Write(0x14020007u); w.Write((byte)1); w.Write(12u); w.Write(4u); w.Write(83u); w.Write(new byte[3]);
            w.Write((ushort)4); foreach (var type in new[] {strips ? "NiTriStrips" : "NiTriShape", strips ? "NiTriStripsData" : "NiTriShapeData", "BSLightingShaderProperty", "BSShaderTextureSet"}) Text(w, type);
            for (ushort i = 0; i < 4; i++) w.Write(i); foreach (var block in blocks) w.Write((uint)block.Length);
            w.Write(0u); w.Write(0u); w.Write(0u); foreach (var block in blocks) w.Write(block); w.Write(1u); w.Write(0); });
    }
}
