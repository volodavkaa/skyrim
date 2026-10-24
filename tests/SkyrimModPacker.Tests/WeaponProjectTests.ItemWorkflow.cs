using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Items_ModelOnlyDetails_PreserveKeywordsAndAllWeaponSoundSlots(bool update)
    {
        var masterPath = Path.Combine(Data, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(masterPath).Mutable().Construct();
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1800), SkyrimRelease.SkyrimSE) { EditorID = "WeapMaterialIron" });
        master.SoundDescriptors.Add(new SoundDescriptor(new FormKey(master.ModKey, 0x1801), SkyrimRelease.SkyrimSE) { EditorID = "InheritedSound" });
        var source = master.Weapons.First();
        source.Keywords = [new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1800))];
        foreach (var name in new[] { "AttackSound", "AttackSound2D", "AttackLoopSound", "AttackFailSound", "IdleSound", "EquipSound", "UnequipSound", "PickUpSound", "PutDownSound" })
        {
            var link = (IFormLinkNullable<ISoundDescriptorGetter>)typeof(Weapon).GetProperty(name)!.GetValue(source)!;
            link.SetTo(new FormKey(master.ModKey, 0x1801));
        }
        master.WriteToBinary(masterPath);
        var bytes = File.ReadAllBytes(masterPath);
        var project = (await Service.CreateAsync(Request())).Value;
        var cloned = await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = project.PluginPath, GameDataPath = Data,
            ProjectFilePath = project.ProjectFilePath, TemplateFormId = source.FormKey.ID, EditorId = "ModelOnly", Name = "Model only",
            Details = update ? null : new WeaponDetails { ModelPath = @"Custom\new.nif" } });
        Assert.True(cloned.IsSuccess, cloned.Error);
        var result = update ? await _builder.UpdateSwordAsync(new UpdateSwordRequest { PluginPath = project.PluginPath, GameDataPath = Data,
            FormId = cloned.Value.FormId, EditorId = cloned.Value.EditorId, Name = cloned.Value.Name, Damage = 7, Weight = 9, Value = 25,
            Details = new WeaponDetails { ModelPath = @"Custom\new.nif" } }) : cloned;
        Assert.True(result.IsSuccess, result.Error);
        Assert.Single(result.Value.Details.Keywords);
        Assert.Equal(9, result.Value.Details.Sounds!.Count);
        Assert.All(result.Value.Details.Sounds.Values, sound => Assert.Equal(new PluginRecordReference("Skyrim.esm", 0x1801), sound));
        Assert.Equal(bytes, File.ReadAllBytes(masterPath));
    }

    [Fact]
    public async Task Items_DuplicateOwnStatAndRecipes_ChangeCopyWithoutChangingOriginal()
    {
        var project = await PreparePackingProjectAsync();
        var parent = (await _builder.GetWeaponsAsync(project.PluginPath)).Value.Single();
        var model = parent.Details.ModelPath;
        Assert.True((await _builder.UpdateSwordAsync(new UpdateSwordRequest { PluginPath = project.PluginPath, GameDataPath = Data,
            FormId = parent.FormId, EditorId = parent.EditorId, Name = parent.Name, Damage = parent.Damage, Weight = parent.Weight, Value = parent.Value,
            ProjectFilePath = project.ProjectFilePath, VanillaTemplateFormId = 0x12EB7,
            Details = new WeaponDetails { ModelPath = model, FirstPersonModelPath = model } })).IsSuccess);
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(Path.Combine(Data, "Skyrim.esm")).Mutable().Construct();
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1800), SkyrimRelease.SkyrimSE) { EditorID = "CraftingSmithingForge" });
        master.MiscItems.Add(new MiscItem(new FormKey(master.ModKey, 0x1801), SkyrimRelease.SkyrimSE) { EditorID = "IngotIron" });
        master.WriteToBinary(Path.Combine(Data, "Skyrim.esm"));
        var recipe = await _builder.SaveRecipeAsync(new SaveRecipeRequest { PluginPath = project.PluginPath, GameDataPath = Data,
            Recipe = new CraftingRecipe { WeaponFormId = parent.FormId, EditorId = "RecipeOriginal", Workbench = new("Skyrim.esm", 0x1800),
                Ingredients = [new RecipeIngredient { Reference = new("Skyrim.esm", 0x1801), Count = 2 }] } });
        Assert.True(recipe.IsSuccess, recipe.Error);
        var before = File.ReadAllBytes(project.PluginPath);
        var copy = await _builder.DuplicateWeaponAsync(new(project.PluginPath, Data, parent.FormId, project.ProjectFilePath));
        Assert.True(copy.IsSuccess, copy.Error); Assert.NotEqual(parent.FormId, copy.Value.FormId);
        Assert.NotEqual(parent.EditorId, copy.Value.EditorId);
        Assert.Equal(new PluginRecordReference("Skyrim.esm", 0x12EB7), (await Service.GetTemplateReferenceAsync(project.ProjectFilePath, Data, copy.Value.FormId)).Value);
        var copiedRecipe = Assert.Single((await _builder.GetRecipesAsync(project.PluginPath, copy.Value.FormId)).Value);
        Assert.NotEqual(recipe.Value.FormId, copiedRecipe.FormId);
        using (var plugin = SkyrimMod.CreateFromBinaryOverlay(project.PluginPath, SkyrimRelease.SkyrimSE))
        {
            var original = plugin.Weapons.Single(w => w.FormKey.ID == parent.FormId);
            var copied = plugin.Weapons.Single(w => w.FormKey.ID == copy.Value.FormId);
            Assert.NotEqual(original.FirstPersonModel.FormKey, copied.FirstPersonModel.FormKey);
            Assert.True(plugin.ModHeader.Stats.NextFormID > plugin.EnumerateMajorRecords().Max(r => r.FormKey.ID));
            Assert.All(plugin.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == plugin.ModKey), r => Assert.InRange(r.FormKey.ID, 0x800u, 0xFFFu));
        }
        Assert.Equal(before, File.ReadAllBytes(project.PluginPath + ".bak"));
        var nextModel = (await Service.ImportModelCopyAsync(project.ProjectFilePath, await AssetAsync("second.nif"), Data)).Value;
        var update = await _builder.UpdateSwordAsync(new UpdateSwordRequest { PluginPath = project.PluginPath, GameDataPath = Data,
            FormId = copy.Value.FormId, EditorId = copy.Value.EditorId, Name = "Independent", Damage = 99, Weight = 3, Value = 100,
            Details = new WeaponDetails { ModelPath = nextModel, FirstPersonModelPath = nextModel } });
        Assert.True(update.IsSuccess, update.Error);
        var savedOriginal = (await _builder.GetWeaponsAsync(project.PluginPath)).Value.Single(w => w.FormId == parent.FormId);
        Assert.Equal(model, savedOriginal.Details.ModelPath); Assert.Equal(parent.Damage, savedOriginal.Damage);
        Assert.True((await _builder.DeleteSwordAsync(project.PluginPath, Data, copy.Value.FormId, projectFilePath: project.ProjectFilePath, deleteRecipes: true)).IsSuccess);
        Assert.Empty((await _builder.GetRecipesAsync(project.PluginPath, copy.Value.FormId)).Value);
        Assert.Single((await _builder.GetRecipesAsync(project.PluginPath, parent.FormId)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Items_DuplicateFailure_DoesNotChangePluginOrMetadata(bool exhausted)
    {
        var project = await PreparePackingProjectAsync();
        if (exhausted)
        {
            var plugin = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(project.PluginPath).Mutable().Construct();
            plugin.Keywords.Add(new Keyword(new FormKey(plugin.ModKey, 0xFFF), SkyrimRelease.SkyrimSE) { EditorID = "LastCompactId" });
            plugin.ModHeader.Stats.NextFormID = 0x1000; plugin.WriteToBinary(project.PluginPath);
        }
        var pluginBytes = File.ReadAllBytes(project.PluginPath); var metadata = File.ReadAllBytes(project.ProjectFilePath);
        var result = await _builder.DuplicateWeaponAsync(new(project.PluginPath, Data, exhausted ? 0x800u : 0x999u, project.ProjectFilePath));
        Assert.True(result.IsFailure); Assert.Equal(pluginBytes, File.ReadAllBytes(project.PluginPath)); Assert.Equal(metadata, File.ReadAllBytes(project.ProjectFilePath));
    }

    [Fact]
    public async Task Items_ProjectZipContainsAllIndependentWeapons_WithSharedResourcesOnce()
    {
        var project = await PreparePackingProjectAsync();
        var original = (await _builder.GetWeaponsAsync(project.PluginPath)).Value.Single();
        for (var i = 0; i < 3; i++)
            Assert.True((await _builder.DuplicateWeaponAsync(new(project.PluginPath, Data, original.FormId, project.ProjectFilePath))).IsSuccess);
        using var provider = PackingProvider();
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(PackingProfile(project), new AppSettings {
            SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "multi-zip") });
        Assert.True(packed.IsSuccess, string.Join("; ", packed.Errors));
        using var zip = ZipFile.OpenRead(packed.OutputArchivePath);
        Assert.Single(zip.Entries, e => e.FullName.EndsWith(".nif")); Assert.Single(zip.Entries, e => e.FullName.EndsWith(".dds"));
        var extracted = Path.Combine(_root, "multi.esp"); zip.GetEntry("MySword.esp")!.ExtractToFile(extracted);
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(extracted, SkyrimRelease.SkyrimSE);
        Assert.Equal(4, plugin.Weapons.Count); Assert.Equal(4, plugin.Weapons.Select(w => w.FormKey).Distinct().Count());
    }
}
