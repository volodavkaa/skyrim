using System.IO.Compression;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    private async Task VerifyWeaponKindAsync(WeaponKind kind, bool esl)
    {
        var masterPath = Path.Combine(Data, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(masterPath).Mutable().Construct();
        var template = master.Weapons.First().DeepCopy();
        template.Data!.AnimationType = (WeaponAnimationType)(int)kind;
        template.Data.Speed = .75f; template.Data.Reach = 1.3f;
        template.Data.RangeMin = 300; template.Data.RangeMax = 2400; template.Data.NumProjectiles = 1;
        template.Data.SightFOV = 45; template.Data.Stagger = .4f; template.Data.AnimationAttackMult = 1.2f;
        template.EditorID = "Vanilla" + kind;
        var stat = new Static(new FormKey(master.ModKey, 0x1200), SkyrimRelease.SkyrimSE) { EditorID = "First" + kind, Model = new() { File = @"Vanilla\First.nif" } };
        master.Statics.Add(stat); template.FirstPersonModel.SetTo(stat.FormKey);
        var keyword = new Keyword(new FormKey(master.ModKey, 0x1201), SkyrimRelease.SkyrimSE) { EditorID = "WeapType" + kind };
        master.Keywords.Add(keyword); template.Keywords = [new FormLink<IKeywordGetter>(keyword.FormKey)];
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1300), SkyrimRelease.SkyrimSE) { EditorID = "CraftingSmithingForge" });
        master.MiscItems.Add(new MiscItem(new FormKey(master.ModKey, 0x1301), SkyrimRelease.SkyrimSE) { EditorID = "IngotIron" });
        master.Weapons.Clear(); master.Weapons.Add(template); master.WriteToBinary(masterPath);
        var templatePlugin = "Skyrim.esm";
        if (kind == WeaponKind.Crossbow)
        {
            templatePlugin = "Dawnguard.esm";
            var dlc = new SkyrimMod(ModKey.FromNameAndExtension(templatePlugin), SkyrimRelease.SkyrimSE);
            dlc.ModHeader.MasterReferences.Add(new MasterReference { Master = master.ModKey });
            // Same local ID, different owner: an override must never be picked as the DLC-owned template.
            var collision = dlc.Weapons.GetOrAddAsOverride(template); collision.Data!.AnimationType = WeaponAnimationType.OneHandSword;
            var dlcWeapon = dlc.Weapons.DuplicateInAsNewRecord(template, new FormKey(dlc.ModKey, template.FormKey.ID));
            var dlcStat = dlc.Statics.DuplicateInAsNewRecord(stat, new FormKey(dlc.ModKey, stat.FormKey.ID));
            dlcWeapon.FirstPersonModel.SetTo(dlcStat.FormKey);
            dlc.WriteToBinary(Path.Combine(Data, templatePlugin));
            master.Weapons.First().Data!.AnimationType = WeaponAnimationType.OneHandSword;
            master.Weapons.First().EditorID = "BaseSwordCollision"; master.Weapons.First().Keywords = [];
            master.WriteToBinary(masterPath);
        }
        var original = File.ReadAllBytes(masterPath);
        var templates = await _builder.GetWeaponTemplatesAsync(Data);
        Assert.True(templates.IsSuccess, templates.Error); var selectedTemplate = Assert.Single(templates.Value, t => t.Kind == kind);
        var project = (await Service.CreateAsync(Request(esl ? ".esl" : ".esp"))).Value;
        var source = Path.Combine(_root, "kind.nif"); File.WriteAllText(source, "custom nif");
        var third = (await Service.ImportModelAsync(project.ProjectFilePath, source, Data)).Value;
        var cloned = await _builder.CloneSwordAsync(new CloneSwordRequest {
            PluginPath = project.PluginPath, ProjectFilePath = project.ProjectFilePath, GameDataPath = Data, TemplateFormId = template.FormKey.ID, TemplatePluginName = templatePlugin,
            EditorId = "Custom" + kind, Name = "Custom", Details = new() { ModelPath = third, FirstPersonModelPath = third, Keywords = selectedTemplate.Details.Keywords,
                Speed = .8f, Reach = 1.5f } });
        Assert.True(cloned.IsSuccess, cloned.Error); Assert.Equal(kind, cloned.Value.Kind);
        Assert.True((await Service.GetOriginalModelsAsync(project.ProjectFilePath, Data, cloned.Value.FormId)).IsSuccess);
        var updated = await _builder.UpdateSwordAsync(new UpdateSwordRequest { PluginPath = project.PluginPath, GameDataPath = Data,
            FormId = cloned.Value.FormId, EditorId = cloned.Value.EditorId, Name = "Edited", Damage = 23, Weight = 15, Value = 200 });
        Assert.True(updated.IsSuccess, updated.Error);
        Assert.True((await Service.OpenAsync(project.ProjectFilePath, Data)).IsSuccess);
        var reopened = Assert.Single((await _builder.GetWeaponsAsync(project.PluginPath)).Value);
        Assert.Equal(kind, reopened.Kind); Assert.Equal(23, reopened.Damage); Assert.Equal(cloned.Value.FormId, reopened.FormId);
        var models = (await _builder.GetWeaponModelsAsync(project.PluginPath, Data, reopened.FormId)).Value;
        Assert.Equal(third, models.FirstPersonPath); Assert.Equal(third, models.ThirdPersonPath);
        var recipe = await _builder.SaveRecipeAsync(new SaveRecipeRequest { PluginPath = project.PluginPath, GameDataPath = Data,
            Recipe = new CraftingRecipe { WeaponFormId = reopened.FormId, EditorId = "RecipeKind", Workbench = new("Skyrim.esm", 0x1300),
                Ingredients = [new() { Reference = new("Skyrim.esm", 0x1301), Count = 2 }] } });
        Assert.True(recipe.IsSuccess, recipe.Error);
        Assert.Single((await _builder.GetRecipesAsync(project.PluginPath, reopened.FormId)).Value);
        var files = await Service.GetPackingFilesAsync(project.ProjectFilePath, Data);
        Assert.True(files.IsSuccess, files.Error);
        using var provider = PackingProvider();
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(PackingProfile(project, esl),
            new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "kind-archives"), UseTempDirectory = true });
        Assert.True(packed.IsSuccess, string.Join("; ", packed.Errors));
        var zip = packed.OutputArchivePath!;
        using (var archive = ZipFile.OpenRead(zip)) { Assert.Equal(2, archive.Entries.Count); Assert.Contains(archive.Entries, e => e.Name == "kind.nif"); }
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(project.PluginPath, SkyrimRelease.SkyrimSE))
        {
            var weapon = Assert.Single(mod.Weapons); Assert.Equal((WeaponAnimationType)(int)kind, weapon.Data!.AnimationType);
            Assert.Equal(.8f, weapon.Data.Speed); Assert.Equal(1.5f, weapon.Data.Reach); Assert.Equal(keyword.FormKey, Assert.Single(weapon.Keywords!).FormKey);
            Assert.Equal(300, weapon.Data.RangeMin); Assert.Equal(2400, weapon.Data.RangeMax); Assert.Equal(1, weapon.Data.NumProjectiles);
            Assert.Equal(45, weapon.Data.SightFOV); Assert.Equal(.4f, weapon.Data.Stagger); Assert.Equal(1.2f, weapon.Data.AnimationAttackMult);
            Assert.Equal(esl, mod.IsSmallMaster); Assert.Equal(0x803u, mod.ModHeader.Stats.NextFormID);
            Assert.Contains(mod.ModHeader.MasterReferences, m => m.Master.FileName.String == templatePlugin);
        }
        Assert.Equal(original, File.ReadAllBytes(masterPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Kinds_01_Greatsword_FullCycle(bool esl) => VerifyWeaponKindAsync(WeaponKind.Greatsword, esl);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Kinds_02_Dagger_FullCycle(bool esl) => VerifyWeaponKindAsync(WeaponKind.Dagger, esl);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Kinds_03_WarAxe_FullCycle(bool esl) => VerifyWeaponKindAsync(WeaponKind.WarAxe, esl);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Kinds_04_Mace_FullCycle(bool esl) => VerifyWeaponKindAsync(WeaponKind.Mace, esl);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Kinds_05_BattleaxeWarhammer_FullCycle(bool esl) => VerifyWeaponKindAsync(WeaponKind.BattleaxeWarhammer, esl);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Kinds_06_Bow_FullCycle(bool esl) => VerifyWeaponKindAsync(WeaponKind.Bow, esl);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Kinds_07_CrossbowDlc_FullCycle(bool esl) => VerifyWeaponKindAsync(WeaponKind.Crossbow, esl);
}
