using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    private static PluginRecordReference Stage4Ref(uint id) => new("Skyrim.esm", id);
    private void AddStage4Data()
    {
        var path = Path.Combine(Data, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct();
        foreach (var (id, name) in new[] { (0x1500u, "CraftingSmithingForge"), (0x1501u, "CraftingSmithingSharpeningWheel"),
            (0x1505u, "WeapTypeSword"), (0x1506u, "WeapTypeGreatsword"), (0x1507u, "WeapMaterialSteel"), (0x1508u, "WeapMaterialIron") })
            master.Keywords.Add(new Keyword(new FormKey(master.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = name });
        master.MiscItems.Add(new MiscItem(new FormKey(master.ModKey, 0x1502), SkyrimRelease.SkyrimSE) { EditorID = "IngotIron" });
        master.Perks.Add(new Perk(new FormKey(master.ModKey, 0x1503), SkyrimRelease.SkyrimSE) { EditorID = "ArcaneBlacksmith" });
        master.Perks.Add(new Perk(new FormKey(master.ModKey, 0x1504), SkyrimRelease.SkyrimSE) { EditorID = "SteelSmithing" });
        master.LeveledItems.Add(new LeveledItem(new FormKey(master.ModKey, 0x1509), SkyrimRelease.SkyrimSE) { EditorID = "MaterialList" });
        master.ObjectEffects.Add(new ObjectEffect(new FormKey(master.ModKey, 0x1510), SkyrimRelease.SkyrimSE) { EditorID = "WeaponEnchantment",
            CastType = CastType.FireAndForget, TargetType = TargetType.Touch, EnchantType = ObjectEffect.EnchantTypeEnum.Enchantment });
        var sword = master.Weapons.First(); sword.Keywords = [new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1505)), new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1508))];
        var great = master.Weapons.DuplicateInAsNewRecord(sword, new FormKey(master.ModKey, 0x1400));
        great.EditorID = "IronGreatsword"; great.Data!.AnimationType = WeaponAnimationType.TwoHandSword;
        great.Keywords = [new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1506)), new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1507))];
        master.WriteToBinary(path);
    }
    private async Task<(WeaponProject Project, WeaponRecord Parent)> Stage4ProjectAsync()
    {
        AddStage4Data(); var p = (await Service.CreateAsync(Request())).Value;
        var cloned = await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, ProjectFilePath = p.ProjectFilePath,
            GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "ParentSword", Name = "Parent" });
        Assert.True(cloned.IsSuccess, cloned.Error); return (p, cloned.Value);
    }

    [Fact]
    public async Task Stage4_Variants_IndependentTypeStatsRecipesAndSharedAssets_RoundTripAndDelete()
    {
        var (p, parent) = await Stage4ProjectAsync();
        var source = Path.Combine(_root, "shared.nif"); File.WriteAllText(source, "shared bytes");
        var model = (await Service.ImportModelAsync(p.ProjectFilePath, source, Data)).Value;
        var parentEdited = await _builder.UpdateSwordAsync(new UpdateSwordRequest { PluginPath = p.PluginPath, GameDataPath = Data,
            FormId = parent.FormId, EditorId = parent.EditorId, Name = parent.Name, Damage = parent.Damage, Weight = parent.Weight, Value = parent.Value,
            Details = new() { ModelPath = model, FirstPersonModelPath = model, Keywords = parent.Details.Keywords } });
        Assert.True(parentEdited.IsSuccess, parentEdited.Error);
        var variant = await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, ProjectFilePath = p.ProjectFilePath,
            GameDataPath = Data, TemplateFormId = 0x1400, EditorId = "SteelVariant", Name = "Steel greatsword", Damage = 50,
            VariantParentFormId = parent.FormId, VariantLabel = "Two handed", VariantMaterialNotes = "Steel, shared textures",
            Details = new() { ModelPath = model, FirstPersonModel = parentEdited.Value.Details.FirstPersonModel,
                Keywords = [Stage4Ref(0x1506), Stage4Ref(0x1507)] } });
        Assert.True(variant.IsSuccess, variant.Error);
        Assert.Equal(WeaponKind.Greatsword, variant.Value.Kind); Assert.Single((await Service.GetResourcesAsync(p.ProjectFilePath, Data)).Value);
        var relation = Assert.Single((await Service.GetVariantsAsync(p.ProjectFilePath, Data)).Value);
        Assert.Equal(parent.FormId, relation.ParentFormId); Assert.Equal(variant.Value.FormId, relation.FormId);
        Assert.True((await Service.OpenAsync(p.ProjectFilePath, Data)).IsSuccess);
        foreach (var weapon in new[] { parent, variant.Value })
            Assert.True((await _builder.SaveRecipeAsync(new SaveRecipeRequest { PluginPath = p.PluginPath, GameDataPath = Data,
                Recipe = new() { WeaponFormId = weapon.FormId, EditorId = "Recipe" + weapon.EditorId, Workbench = Stage4Ref(0x1500),
                    Ingredients = [new() { Reference = Stage4Ref(0x1502), Count = weapon == parent ? 2 : 4 }] } })).IsSuccess);
        Assert.Equal(2, Assert.Single(Assert.Single((await _builder.GetRecipesAsync(p.PluginPath, parent.FormId)).Value).Ingredients).Count);
        Assert.Equal(4, Assert.Single(Assert.Single((await _builder.GetRecipesAsync(p.PluginPath, variant.Value.FormId)).Value).Ingredients).Count);
        var savedParent = (await _builder.GetWeaponsAsync(p.PluginPath)).Value.Single(w => w.FormId == parent.FormId);
        Assert.Equal(parent.Damage, savedParent.Damage); Assert.Equal(WeaponKind.OneHandSword, savedParent.Kind);
        var recipe = Assert.Single((await _builder.GetRecipesAsync(p.PluginPath, variant.Value.FormId)).Value);
        Assert.True((await _builder.DeleteRecipeAsync(p.PluginPath, Data, recipe.FormId!.Value)).IsSuccess);
        Assert.True((await _builder.DeleteSwordAsync(p.PluginPath, Data, variant.Value.FormId, projectFilePath: p.ProjectFilePath)).IsSuccess);
        Assert.Empty((await Service.GetVariantsAsync(p.ProjectFilePath, Data)).Value);
        Assert.Equal("shared bytes", File.ReadAllText(Path.Combine(Path.GetDirectoryName(p.PluginPath)!, "meshes", model)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage4_Tempering_ArcaneGuardOptionalMaterialPerk_RoundTrip(bool materialPerk)
    {
        var (p, parent) = await Stage4ProjectAsync();
        var result = await _builder.SaveRecipeAsync(new SaveRecipeRequest { PluginPath = p.PluginPath, GameDataPath = Data,
            Recipe = new() { WeaponFormId = parent.FormId, EditorId = "TemperParent", Workbench = Stage4Ref(0x1501),
                RequiredPerk = materialPerk ? Stage4Ref(0x1504) : null, Ingredients = [new() { Reference = Stage4Ref(0x1502), Count = 1 }] } });
        Assert.True(result.IsSuccess, result.Error); Assert.True(result.Value.IsTempering); Assert.Equal(Stage4Ref(0x1503), result.Value.TemperingPerk);
        Assert.Equal(materialPerk ? Stage4Ref(0x1504) : null, result.Value.RequiredPerk);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE);
        var recipe = Assert.Single(mod.ConstructibleObjects);
        Assert.Equal(materialPerk ? 3 : 2, recipe.Conditions.Count);
        Assert.IsAssignableFrom<IEPTemperingItemIsEnchantedConditionDataGetter>(recipe.Conditions[0].Data);
        Assert.Equal(1, (int)recipe.Conditions[0].Flags); Assert.Equal(CompareOperator.NotEqualTo, recipe.Conditions[0].CompareOperator);
        Assert.Equal(0, (int)recipe.Conditions[1].Flags);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("self")]
    [InlineData("list")]
    [InlineData("missing")]
    [InlineData("wrongtype")]
    public async Task Stage4_RecipeInvalidOutputOrComponents_LeavesPluginUnchanged(string issue)
    {
        var (p, parent) = await Stage4ProjectAsync(); var before = File.ReadAllBytes(p.PluginPath);
        var ingredient = issue switch { "self" => new PluginRecordReference(Path.GetFileName(p.PluginPath), parent.FormId), "list" => Stage4Ref(0x1509),
            "missing" => Stage4Ref(0xFFFFFF), "wrongtype" => Stage4Ref(0x1505), _ => Stage4Ref(0x1502) };
        var result = await _builder.SaveRecipeAsync(new SaveRecipeRequest { PluginPath = p.PluginPath, GameDataPath = Data,
            Recipe = new() { WeaponFormId = parent.FormId, EditorId = "InvalidRecipe", Workbench = Stage4Ref(0x1501), OutputCount = issue == "count" ? (ushort)2 : (ushort)1,
                Ingredients = [new() { Reference = ingredient, Count = 1 }] } });
        Assert.True(result.IsFailure); Assert.Equal(before, File.ReadAllBytes(p.PluginPath));
    }

    [Theory]
    [InlineData("restriction")]
    [InlineData("missingEffect")]
    [InlineData("cycle")]
    public async Task Stage4_EnchantmentDependenciesAndRestrictions_RejectInvalidLinks(string issue)
    {
        var (p, parent) = await Stage4ProjectAsync();
        var path = Path.Combine(Data, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct();
        var enchantment = master.ObjectEffects.First();
        if (issue == "cycle") enchantment.BaseEnchantment.SetTo(enchantment.FormKey);
        if (issue == "missingEffect") { var effect = new Effect(); effect.BaseEffect.SetTo(new FormKey(master.ModKey, 0xFFFF)); enchantment.Effects.Add(effect); }
        if (issue == "restriction")
        {
            var restriction = new FormList(new FormKey(master.ModKey, 0x1520), SkyrimRelease.SkyrimSE) { EditorID = "GreatswordsOnly" };
            restriction.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(new FormKey(master.ModKey, 0x1506)));
            master.FormLists.Add(restriction); enchantment.WornRestrictions.SetTo(restriction.FormKey);
        }
        master.WriteToBinary(path);
        var bytes = File.ReadAllBytes(p.PluginPath);
        var result = await _builder.UpdateSwordAsync(new() { PluginPath = p.PluginPath, GameDataPath = Data, FormId = parent.FormId,
            EditorId = parent.EditorId, Name = parent.Name, Damage = parent.Damage, Weight = parent.Weight, Value = parent.Value,
            Details = new() { ModelPath = parent.Details.ModelPath, Keywords = parent.Details.Keywords, Enchantment = Stage4Ref(0x1510), EnchantmentCharge = 100 } });
        Assert.True(result.IsFailure); Assert.Equal(bytes, File.ReadAllBytes(p.PluginPath));
    }

    [Fact]
    public async Task Stage4_TemplateLookup_RejectsNonPlayableAndBoundTemplates()
    {
        var path = Path.Combine(Data, "Skyrim.esm");
        foreach (var flag in new[] { "major", "data", "bound" })
        {
            var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct(); var weapon = master.Weapons.First();
            weapon.MajorFlags = flag == "major" ? Weapon.MajorFlag.NonPlayable : 0;
            weapon.Data!.Flags = flag == "data" ? WeaponData.Flag.NonPlayable : flag == "bound" ? WeaponData.Flag.BoundWeapon : 0;
            master.WriteToBinary(path);
            Assert.Empty((await _builder.GetWeaponTemplatesAsync(Data)).Value);
            var p = (await Service.CreateAsync(new CreatePluginRequest { PluginFileName = flag + ".esp", OutputDirectory = Output, GameDataPath = Data })).Value;
            var before = File.ReadAllBytes(p.PluginPath);
            var result = await _builder.CloneSwordAsync(new() { PluginPath = p.PluginPath, GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "Rejected", Name = "Rejected" });
            Assert.True(result.IsFailure); Assert.Equal(before, File.ReadAllBytes(p.PluginPath));
        }
    }

    [Fact]
    public async Task Stage4_Variants_SeparateModelAndTextureSets_PackWithoutChangingParent()
    {
        var (p, parent) = await Stage4ProjectAsync();
        var files = new List<string>();
        foreach (var name in new[] { "Iron", "Steel" })
        {
            var mesh = Path.Combine(_root, "meshes", name, "sword.nif"); Directory.CreateDirectory(Path.GetDirectoryName(mesh)!); File.WriteAllText(mesh, name + " model");
            var texture = Path.Combine(_root, "textures", name, "sword.dds"); Directory.CreateDirectory(Path.GetDirectoryName(texture)!); File.WriteAllBytes(texture, PreviewFixtures.ResourceDds(name + " texture"));
            files.Add((await Service.ImportModelAsync(p.ProjectFilePath, mesh, Data)).Value);
            Assert.True((await Service.ImportTexturesAsync(p.ProjectFilePath, [texture], Data)).IsSuccess);
        }
        Assert.True((await _builder.UpdateSwordAsync(new() { PluginPath = p.PluginPath, GameDataPath = Data, FormId = parent.FormId,
            EditorId = parent.EditorId, Name = parent.Name, Damage = 7, Weight = 9, Value = 25, Details = new() { ModelPath = files[0], Keywords = parent.Details.Keywords } })).IsSuccess);
        var variant = await _builder.CloneSwordAsync(new() { PluginPath = p.PluginPath, ProjectFilePath = p.ProjectFilePath, GameDataPath = Data,
            TemplateFormId = 0x1400, EditorId = "SteelSeparate", Name = "Steel variant", VariantParentFormId = parent.FormId, VariantLabel = "Steel",
            Details = new() { ModelPath = files[1], Keywords = [Stage4Ref(0x1506), Stage4Ref(0x1507)] } });
        Assert.True(variant.IsSuccess, variant.Error);
        var packing = await Service.GetPackingFilesAsync(p.ProjectFilePath, Data);
        Assert.True(packing.IsSuccess, packing.Error); Assert.Equal(5, packing.Value.Count);
        Assert.Equal(files[0], (await _builder.GetWeaponsAsync(p.PluginPath)).Value.Single(w => w.FormId == parent.FormId).Details.ModelPath);
        Assert.Equal(2, packing.Value.Count(f => f.RelativePath.EndsWith(".dds")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage4_RecipeReference_WinningDeletedOverride_IsRejected(bool local)
    {
        var (p, parent) = await Stage4ProjectAsync();
        var sourcePath = Path.Combine(Data, "Skyrim.esm");
        using var source = SkyrimMod.CreateFromBinaryOverlay(sourcePath, SkyrimRelease.SkyrimSE);
        var destination = local ? p.PluginPath : Path.Combine(Data, "Dawnguard.esm");
        var mod = local ? SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(destination).Mutable().Construct() :
            new SkyrimMod(ModKey.FromNameAndExtension("Dawnguard.esm"), SkyrimRelease.SkyrimSE);
        if (!local) mod.ModHeader.MasterReferences.Add(new Mutagen.Bethesda.Plugins.Records.MasterReference { Master = source.ModKey });
        var deleted = mod.MiscItems.GetOrAddAsOverride(source.MiscItems.First()); deleted.IsDeleted = true;
        if (!local) mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, 0x1600), SkyrimRelease.SkyrimSE) { EditorID = "DlcDependencyMarker" });
        mod.WriteToBinary(destination);
        if (!local)
        {
            var workspace = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(p.PluginPath).Mutable().Construct();
            workspace.ModHeader.MasterReferences.Add(new Mutagen.Bethesda.Plugins.Records.MasterReference { Master = mod.ModKey });
            workspace.Weapons.First().Keywords!.Add(new FormLink<IKeywordGetter>(new FormKey(mod.ModKey, 0x1600)));
            workspace.BeginWrite.ToPath(p.PluginPath).WithLoadOrderFromHeaderMasters().WithDataFolder(Data).NoMastersListContentCheck().Write();
            using var verified = SkyrimMod.CreateFromBinaryOverlay(p.PluginPath, SkyrimRelease.SkyrimSE);
            Assert.Equal("Dawnguard.esm", verified.ModHeader.MasterReferences.Last().Master.FileName.String);
        }
        var before = File.ReadAllBytes(p.PluginPath);
        var result = await _builder.SaveRecipeAsync(new() { PluginPath = p.PluginPath, GameDataPath = Data,
            Recipe = new() { WeaponFormId = parent.FormId, EditorId = "InvalidWinningIngredient", Workbench = Stage4Ref(0x1500),
                Ingredients = [new() { Reference = Stage4Ref(0x1502), Count = 1 }] } });
        Assert.True(result.IsFailure); Assert.Equal(before, File.ReadAllBytes(p.PluginPath));
    }

    [Fact]
    public async Task Stage4_VariantFailedPublication_RestoresMetadataAndPlugin()
    {
        var (p, parent) = await Stage4ProjectAsync(); var bytes = File.ReadAllBytes(p.PluginPath);
        using (var held = new FileStream(p.PluginPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, ProjectFilePath = p.ProjectFilePath,
                GameDataPath = Data, TemplateFormId = 0x1400, EditorId = "FailedVariant", Name = "Failed", VariantParentFormId = parent.FormId, VariantLabel = "Failed" });
            Assert.True(result.IsFailure);
        }
        Assert.Equal(bytes, File.ReadAllBytes(p.PluginPath)); Assert.Empty((await Service.GetVariantsAsync(p.ProjectFilePath, Data)).Value);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("missing")]
    [InlineData("label")]
    public async Task Stage4_VariantInvalidParentOrLabel_LeavesProjectUntouched(string issue)
    {
        var (p, parent) = await Stage4ProjectAsync(); var metadata = File.ReadAllBytes(p.ProjectFilePath); var bytes = File.ReadAllBytes(p.PluginPath);
        var result = await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = p.PluginPath, ProjectFilePath = p.ProjectFilePath,
            GameDataPath = Data, TemplateFormId = 0x1400, EditorId = "InvalidVariant", Name = "Invalid",
            VariantParentFormId = issue == "zero" ? 0u : issue == "missing" ? 0xFFFu : parent.FormId, VariantLabel = issue == "label" ? "" : "Valid" });
        Assert.True(result.IsFailure); Assert.Equal(bytes, File.ReadAllBytes(p.PluginPath)); Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("chargeWithoutEnchantment")]
    [InlineData("zeroCharge")]
    public async Task Stage4_KeywordAndChargeValidation_RefusesIncompatibleDraft(string issue)
    {
        var (p, parent) = await Stage4ProjectAsync(); var before = File.ReadAllBytes(p.PluginPath);
        var details = new WeaponDetails { ModelPath = parent.Details.ModelPath,
            Keywords = issue == "duplicate" ? [Stage4Ref(0x1505), new("skyrim.esm", 0x1505)] : issue == "type" ? [Stage4Ref(0x1506)] : parent.Details.Keywords,
            Enchantment = issue == "zeroCharge" ? Stage4Ref(0x1510) : null,
            EnchantmentCharge = issue == "chargeWithoutEnchantment" ? (ushort)100 : (ushort)0 };
        var result = await _builder.UpdateSwordAsync(new() { PluginPath = p.PluginPath, GameDataPath = Data, FormId = parent.FormId,
            EditorId = parent.EditorId, Name = parent.Name, Damage = parent.Damage, Weight = parent.Weight, Value = parent.Value, Details = details });
        Assert.True(result.IsFailure); Assert.Equal(before, File.ReadAllBytes(p.PluginPath));
    }
}
