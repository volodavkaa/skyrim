using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Esl;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public class AdvancedPluginBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdvancedPluginBuilderTests", Guid.NewGuid().ToString("N"));
    private readonly PluginBuilderService _builder = new();
    private string Data => Path.Combine(_root, "Game", "Data");
    private string Plugin => Path.Combine(_root, "workspace", "Sword.esp");
    private string MasterPath => Path.Combine(Data, "Skyrim.esm");
    private static readonly ModKey Skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
    private static PluginRecordReference Ref(uint id) => new("Skyrim.esm", id);

    public AdvancedPluginBuilderTests()
    {
        Directory.CreateDirectory(Data);
        var source = new SkyrimMod(Skyrim, SkyrimRelease.SkyrimSE);
        source.Keywords.Add(new Keyword(new FormKey(Skyrim, 0x100), SkyrimRelease.SkyrimSE) { EditorID = "WeapTypeSword" });
        source.Keywords.Add(new Keyword(new FormKey(Skyrim, 0x101), SkyrimRelease.SkyrimSE) { EditorID = "CraftingSmithingForge" });
        source.MiscItems.Add(new MiscItem(new FormKey(Skyrim, 0x102), SkyrimRelease.SkyrimSE) { EditorID = "IngotIron", Name = "Iron Ingot" });
        source.SoundDescriptors.Add(new SoundDescriptor(new FormKey(Skyrim, 0x103), SkyrimRelease.SkyrimSE) { EditorID = "SwordAttackSound" });
        source.ObjectEffects.Add(new ObjectEffect(new FormKey(Skyrim, 0x104), SkyrimRelease.SkyrimSE)
        {
            EditorID = "FireWeaponEnchantment", CastType = CastType.FireAndForget, TargetType = TargetType.Touch,
            EnchantType = ObjectEffect.EnchantTypeEnum.Enchantment
        });
        source.ObjectEffects.Add(new ObjectEffect(new FormKey(Skyrim, 0x105), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ArmorEnchantment", CastType = CastType.ConstantEffect, TargetType = TargetType.Self,
            EnchantType = ObjectEffect.EnchantTypeEnum.Enchantment
        });
        source.Perks.Add(new Perk(new FormKey(Skyrim, 0x106), SkyrimRelease.SkyrimSE) { EditorID = "SteelSmithing" });
        var sword = new Weapon(new FormKey(Skyrim, 0x12EB7), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IronSword", Name = "Iron Sword", Description = "Vanilla description",
            BasicStats = new WeaponBasicStats { Damage = 7, Weight = 9, Value = 25 },
            Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword, Speed = 1, Reach = 1 },
            Model = new Model { File = @"Weapons\Iron\LongSword.nif" },
            Keywords = [new FormLink<IKeywordGetter>(new FormKey(Skyrim, 0x100))]
        };
        source.Weapons.Add(sword);
        var list = new LeveledItem(new FormKey(Skyrim, 0x2000), SkyrimRelease.SkyrimSE)
        {
            EditorID = "LItemSword", Flags = LeveledItem.Flag.CalculateForEachItemInCount, Entries = []
        };
        var entry = new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 2, Unknown = 17 } };
        entry.Data.Reference.SetTo(sword.FormKey);
        list.Entries.Add(entry);
        source.LeveledItems.Add(list);
        // Simulate vanilla master records in the low range, without changing the plugin writer's checks.
        source.WriteToBinary(MasterPath, new BinaryWriteParameters { LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed() });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task PrepareAsync(WeaponDetails? details = null)
    {
        var created = await _builder.CreateAsync(new CreatePluginRequest
        {
            PluginFileName = "Sword.esp", OutputDirectory = Path.GetDirectoryName(Plugin)!, GameDataPath = Data, Masters = ["Skyrim.esm"]
        });
        Assert.True(created.IsSuccess, created.Error);
        var clone = await _builder.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = Plugin, GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "CustomSword", Name = "Крижаний меч", Details = details
        });
        Assert.True(clone.IsSuccess, clone.Error);
    }

    private WeaponDetails Details(float speed = 1.2f, float reach = 1.4f, string model = @"meshes\Weapons\Custom\blade.nif", PluginRecordReference? sound = null, PluginRecordReference? enchantment = null, ushort charge = 1000) => new()
    {
        Description = "Опис меча", Speed = speed, Reach = reach, ModelPath = model,
        Keywords = [Ref(0x100)], AttackSound = sound ?? Ref(0x103), Enchantment = enchantment ?? Ref(0x104), EnchantmentCharge = charge
    };

    private SaveRecipeRequest Recipe(uint? id = null, int ingredientCount = 2) => new()
    {
        PluginPath = Plugin, GameDataPath = Data,
        Recipe = new CraftingRecipe
        {
            FormId = id, WeaponFormId = 0x800, EditorId = "RecipeCustomSword", Workbench = Ref(0x101),
            Ingredients = [new RecipeIngredient { Reference = Ref(0x102), Count = ingredientCount }], RequiredPerk = Ref(0x106)
        }
    };

    private LeveledListRequest List(short level = 3) => new()
    {
        PluginPath = Plugin, GameDataPath = Data, WeaponFormId = 0x800, List = Ref(0x2000), Level = level, Count = 1
    };

    [Fact]
    public async Task ExtendedFields_RoundTripAndClearOptionalLinks_WithoutChangingGame()
    {
        var gameBytes = await File.ReadAllBytesAsync(MasterPath);
        await PrepareAsync(Details());
        var read = await _builder.GetWeaponsAsync(Plugin);
        Assert.True(read.IsSuccess, read.Error);
        var sword = Assert.Single(read.Value);
        Assert.Equal("Опис меча", sword.Details.Description);
        Assert.Equal(1.2f, sword.Details.Speed);
        Assert.Equal(1.4f, sword.Details.Reach);
        Assert.Equal(@"Weapons\Custom\blade.nif", sword.Details.ModelPath);
        Assert.Equal(Ref(0x103), sword.Details.AttackSound);
        Assert.Equal(Ref(0x104), sword.Details.Enchantment);
        Assert.Equal((ushort)1000, sword.Details.EnchantmentCharge);
        var before = await File.ReadAllBytesAsync(Plugin);
        var updated = await _builder.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = Plugin, GameDataPath = Data, FormId = 0x800, EditorId = sword.EditorId, Name = sword.Name,
            Damage = 12, Weight = 8, Value = 100,
            Details = new WeaponDetails { Description = "Без чар", Speed = 1, Reach = 1, ModelPath = @"Weapons\Iron\LongSword.nif" }
        });
        Assert.True(updated.IsSuccess, updated.Error);
        Assert.Null(updated.Value.Details.Enchantment);
        Assert.Null(updated.Value.Details.AttackSound);
        Assert.Empty(updated.Value.Details.Keywords);
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin + ".bak"));
        Assert.Equal(gameBytes, await File.ReadAllBytesAsync(MasterPath));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("speed")]
    [InlineData("reach")]
    [InlineData("sound")]
    [InlineData("enchantment")]
    [InlineData("charge")]
    public async Task InvalidExtendedFields_DoNotChangePluginOrBackup(string problem)
    {
        await PrepareAsync();
        var before = await File.ReadAllBytesAsync(Plugin);
        var backup = await File.ReadAllBytesAsync(Plugin + ".bak");
        var details = Details(speed: problem == "speed" ? float.NaN : 1, reach: problem == "reach" ? 0 : 1,
            model: problem == "model" ? @"..\escape.nif" : @"Weapons\Iron\LongSword.nif",
            sound: problem == "sound" ? Ref(0x102) : null, enchantment: problem == "enchantment" ? Ref(0x105) : null,
            charge: problem == "charge" ? (ushort)0 : (ushort)1000);
        var result = await _builder.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = Plugin, GameDataPath = Data, FormId = 0x800, EditorId = "CustomSword", Name = "Sword", Details = details
        });
        Assert.True(result.IsFailure);
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Plugin + ".bak"));
    }

    [Fact]
    public async Task Recipe_CreateEditDelete_PreservesFormIdsAndPerkCondition()
    {
        await PrepareAsync();
        var before = await File.ReadAllBytesAsync(Plugin);
        var saved = await _builder.SaveRecipeAsync(Recipe());
        Assert.True(saved.IsSuccess, saved.Error);
        Assert.Equal(0x801u, saved.Value.FormId);
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin + ".bak"));
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(Plugin, SkyrimRelease.SkyrimSE))
        {
            var recipe = Assert.Single(mod.ConstructibleObjects);
            Assert.Equal(new FormKey(mod.ModKey, 0x800), recipe.CreatedObject.FormKey);
            Assert.Equal(new FormKey(Skyrim, 0x101), recipe.WorkbenchKeyword.FormKey);
            Assert.Equal(2, Assert.Single(recipe.Items!).Item.Count);
            var condition = Assert.IsAssignableFrom<IConditionFloatGetter>(Assert.Single(recipe.Conditions));
            Assert.Equal(1f, condition.ComparisonValue);
            Assert.Equal(new FormKey(Skyrim, 0x106), Assert.IsAssignableFrom<IHasPerkConditionDataGetter>(condition.Data).Perk.Link.FormKey);
        }
        var edited = await _builder.SaveRecipeAsync(Recipe(saved.Value.FormId, ingredientCount: 3));
        Assert.True(edited.IsSuccess, edited.Error);
        var recipes = await _builder.GetRecipesAsync(Plugin, 0x800);
        Assert.Equal(3, Assert.Single(Assert.Single(recipes.Value).Ingredients).Count);
        Assert.Equal(saved.Value.FormId, edited.Value.FormId);
        Assert.True((await _builder.DeleteSwordAsync(Plugin, Data, 0x800)).IsFailure);
        Assert.True((await _builder.DeleteRecipeAsync(Plugin, Data, saved.Value.FormId!.Value)).IsSuccess);
        var recreated = await _builder.SaveRecipeAsync(Recipe());
        Assert.True(recreated.IsSuccess, recreated.Error);
        Assert.Equal(0x802u, recreated.Value.FormId);
    }

    [Fact]
    public async Task LeveledList_AddRemove_PreservesOriginalEntriesFlagsAndGame()
    {
        await PrepareAsync();
        var gameBytes = await File.ReadAllBytesAsync(MasterPath);
        var result = await _builder.AddToLeveledListAsync(List());
        Assert.True(result.IsSuccess, result.Error);
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(Plugin, SkyrimRelease.SkyrimSE))
        {
            var list = Assert.Single(mod.LeveledItems);
            Assert.Equal(new FormKey(Skyrim, 0x2000), list.FormKey);
            Assert.Equal(LeveledItem.Flag.CalculateForEachItemInCount, list.Flags);
            Assert.Equal(2, list.Entries!.Count);
            Assert.Equal(new FormKey(Skyrim, 0x12EB7), list.Entries[0].Data!.Reference.FormKey);
            Assert.Equal((short)17, list.Entries[0].Data!.Unknown);
            Assert.Equal((short)2, list.Entries[0].Data!.Count);
            Assert.Equal(new FormKey(mod.ModKey, 0x800), list.Entries[1].Data!.Reference.FormKey);
            Assert.Equal((short)3, list.Entries[1].Data!.Level);
        }
        var beforeDuplicate = await File.ReadAllBytesAsync(Plugin);
        Assert.True((await _builder.AddToLeveledListAsync(List())).IsFailure);
        Assert.Equal(beforeDuplicate, await File.ReadAllBytesAsync(Plugin));
        var analysis = await new EslService().AnalyzeAsync(Plugin);
        Assert.True(analysis.Value.IsCompatible);
        Assert.Equal(1, analysis.Value.OwnRecordCount);
        Assert.True((await _builder.DeleteSwordAsync(Plugin, Data, 0x800)).IsFailure);
        Assert.True((await _builder.RemoveFromLeveledListAsync(List())).IsSuccess);
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(Plugin, SkyrimRelease.SkyrimSE))
            Assert.Equal(new FormKey(Skyrim, 0x12EB7), Assert.Single(Assert.Single(mod.LeveledItems).Entries!).Data!.Reference.FormKey);
        Assert.True((await _builder.DeleteSwordAsync(Plugin, Data, 0x800)).IsSuccess);
        Assert.Equal(gameBytes, await File.ReadAllBytesAsync(MasterPath));
    }

    [Fact]
    public async Task LeveledList_MaximumEntriesAndInvalidLevel_AreRejected()
    {
        var source = SkyrimMod.CreateFromBinary(MasterPath, SkyrimRelease.SkyrimSE);
        var list = source.LeveledItems.First();
        for (var i = 1; i < 255; i++)
        {
            var entry = new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1 } };
            entry.Data.Reference.SetTo(new FormKey(Skyrim, 0x12EB7));
            list.Entries!.Add(entry);
        }
        source.WriteToBinary(MasterPath, new BinaryWriteParameters { LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed() });
        await PrepareAsync();
        var before = await File.ReadAllBytesAsync(Plugin);
        Assert.True((await _builder.AddToLeveledListAsync(List())).IsFailure);
        Assert.True((await _builder.AddToLeveledListAsync(List(level: 0))).IsFailure);
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Screen_AdvancedSwordRecipeAndList_PackWithEslAndPreserveSource(bool useTemp)
    {
        var settings = Substitute.For<ISettingsRepository>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings { SkyrimDataPath = Data });
        var profiles = new JsonProfileRepository(Path.Combine(_root, "appdata"));
        var vm = new WeaponsViewModel(_builder, settings, profiles, new WeaponProjectService(_builder)) { PluginName = "Sword", OutputDirectory = Path.GetDirectoryName(Plugin)! };
        await vm.CreatePluginCommand.ExecuteAsync(null);
        await vm.LoadTemplatesCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Single(vm.Options.Enchantments);
        vm.EditorId = "CustomSword";
        vm.Description = "Опис меча";
        vm.Speed = "1.2"; vm.Reach = "1.4";
        vm.SelectedSound = vm.SoundChoices.Single();
        vm.SelectedEnchantment = vm.EnchantmentChoices.Single();
        vm.EnchantmentCharge = "1000";
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        vm.IngredientToAdd = vm.Options.Ingredients.Single(i => i.EditorId == "IngotIron");
        vm.IngredientCount = "2";
        vm.AddIngredientCommand.Execute(null);
        vm.SelectedPerk = vm.PerkChoices.Single();
        await vm.SaveRecipeCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Single(vm.Recipes);
        vm.SelectedLeveledList = vm.Options.LeveledLists.Single();
        await vm.AddToListCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        await vm.CreatePackingProfileCommand.ExecuteAsync(null);
        var profile = Assert.Single(await profiles.LoadAllAsync());
        profile.EnableEsl = true;
        var gameBytes = await File.ReadAllBytesAsync(MasterPath);
        var workspaceBytes = await File.ReadAllBytesAsync(vm.PluginPath);
        var services = new ServiceCollection();
        services.AddLogging(); services.AddApplicationServices(); services.AddInfrastructureServices(Path.Combine(_root, "appdata"));
        using var provider = services.BuildServiceProvider();
        var packed = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile,
            new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives"), UseTempDirectory = useTemp });
        Assert.True(packed.IsSuccess, string.Join(";", packed.Errors));
        using (var zip = ZipFile.OpenRead(packed.OutputArchivePath))
        {
            var entry = Assert.Single(zip.Entries);
            var unpackedDirectory = Path.Combine(_root, "unpacked");
            Directory.CreateDirectory(unpackedDirectory);
            var unpacked = Path.Combine(unpackedDirectory, entry.Name);
            entry.ExtractToFile(unpacked);
            using var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(unpacked).WithUtf8Encoding().Construct();
            Assert.True(mod.IsSmallMaster);
            Assert.Equal("Опис меча", Assert.Single(mod.Weapons).Description!.String);
            Assert.Equal(new FormKey(mod.ModKey, 0x800), Assert.Single(mod.ConstructibleObjects).CreatedObject.FormKey);
            Assert.Equal(2, Assert.Single(mod.LeveledItems).Entries!.Count);
            Assert.Equal(2, mod.EnumerateMajorRecords().Count(r => r.FormKey.ModKey == mod.ModKey));
        }
        Assert.Equal(gameBytes, await File.ReadAllBytesAsync(MasterPath));
        Assert.Equal(workspaceBytes, await File.ReadAllBytesAsync(vm.PluginPath));
        // Reloading lookup choices must not overwrite unsaved field edits.
        vm.Description = "Unsaved description";
        await vm.LoadOptionsCommand.ExecuteAsync(null);
        Assert.Equal("Unsaved description", vm.Description);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("type")]
    [InlineData("duplicate")]
    [InlineData("perk")]
    public async Task InvalidRecipe_DoesNotChangePluginOrBackup(string problem)
    {
        await PrepareAsync();
        var before = await File.ReadAllBytesAsync(Plugin);
        var backup = await File.ReadAllBytesAsync(Plugin + ".bak");
        var ingredients = new List<RecipeIngredient>
        {
            new() { Reference = Ref(problem == "type" ? 0x103u : 0x102u), Count = problem == "count" ? 0 : 1 }
        };
        if (problem == "duplicate") ingredients.Add(new RecipeIngredient { Reference = new PluginRecordReference("skyrim.esm", 0x102), Count = 2 });
        var result = await _builder.SaveRecipeAsync(new SaveRecipeRequest
        {
            PluginPath = Plugin, GameDataPath = Data,
            Recipe = new CraftingRecipe
            {
                WeaponFormId = 0x800, EditorId = "RecipeTest", Workbench = Ref(0x101), Ingredients = ingredients,
                RequiredPerk = problem == "perk" ? Ref(0x100) : null
            }
        });
        Assert.True(result.IsFailure);
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Plugin + ".bak"));
    }

    [Fact]
    public async Task NewOperations_RejectGameDestinationsAndExhaustedRecipeIds()
    {
        await PrepareAsync();
        var gamePlugin = Path.Combine(Data, "Sword.esp");
        File.Copy(Plugin, gamePlugin);
        var original = await File.ReadAllBytesAsync(gamePlugin);
        Assert.True((await _builder.SaveRecipeAsync(new SaveRecipeRequest { PluginPath = gamePlugin, GameDataPath = Data, Recipe = Recipe().Recipe })).IsFailure);
        var list = new LeveledListRequest { PluginPath = gamePlugin, GameDataPath = Data, WeaponFormId = 0x800, List = Ref(0x2000) };
        Assert.True((await _builder.AddToLeveledListAsync(list)).IsFailure);
        Assert.True((await _builder.RemoveFromLeveledListAsync(list)).IsFailure);
        Assert.True((await _builder.DeleteRecipeAsync(gamePlugin, Data, 0x801)).IsFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(gamePlugin));
        Assert.False(File.Exists(gamePlugin + ".bak"));
        var mod = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
        for (uint id = 0x801; id <= 0xFFF; id++) mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE));
        mod.WriteToBinary(Plugin);
        var full = await File.ReadAllBytesAsync(Plugin);
        var result = await _builder.SaveRecipeAsync(Recipe());
        Assert.True(result.IsFailure);
        Assert.Contains("exhausted", result.Error);
        Assert.Equal(full, await File.ReadAllBytesAsync(Plugin));
    }

    [Fact]
    public async Task Recipe_WithUnsupportedCondition_IsNotSilentlyRewritten()
    {
        await PrepareAsync();
        var saved = await _builder.SaveRecipeAsync(Recipe());
        Assert.True(saved.IsSuccess, saved.Error);
        var mod = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
        var condition = (ConditionFloat)mod.ConstructibleObjects.First().Conditions[0];
        condition.CompareOperator = CompareOperator.NotEqualTo;
        mod.WriteToBinary(Plugin);
        var original = await File.ReadAllBytesAsync(Plugin);
        var result = await _builder.SaveRecipeAsync(Recipe(saved.Value.FormId));
        Assert.True(result.IsFailure);
        Assert.Contains("advanced conditions", result.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(Plugin));
    }
}
