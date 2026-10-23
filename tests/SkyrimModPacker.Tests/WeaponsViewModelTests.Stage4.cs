using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private void AddStage4UiMaster()
    {
        var path = Path.Combine(DataPath, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct();
        foreach (var (id, name) in new[] { (0x1601u, "CraftingSmithingSharpeningWheel"), (0x1602u, "WeapTypeGreatsword"), (0x1603u, "WeapMaterialSteel") })
            master.Keywords.Add(new Keyword(new FormKey(master.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = name });
        master.Perks.Add(new Perk(new FormKey(master.ModKey, 0x1604), SkyrimRelease.SkyrimSE) { EditorID = "ArcaneBlacksmith" });
        var great = master.Weapons.DuplicateInAsNewRecord(master.Weapons.First(), new FormKey(master.ModKey, 0x1600));
        great.EditorID = "IronGreatsword"; great.Name = "Greatsword"; great.Data!.AnimationType = WeaponAnimationType.TwoHandSword;
        great.Keywords = [new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1602))];
        master.WriteToBinary(path);
    }

    [Fact]
    public async Task Stage4Ui_TypeSelection_UsesGuardAndCorrectTemplate_AndHistory()
    {
        AddStage4UiMaster();
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        Assert.Equal(WeaponKind.OneHandSword, vm.SelectedWeaponKind);
        vm.WeaponName = "Unsaved"; vm.WeaponTypeSelection = WeaponKind.Greatsword; await vm.PendingSelectionChange;
        Assert.Equal(WeaponKind.OneHandSword, vm.SelectedWeaponKind); Assert.Equal("Unsaved", vm.WeaponName);
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        vm.WeaponTypeSelection = WeaponKind.Greatsword; await vm.PendingSelectionChange;
        Assert.Equal(WeaponKind.Greatsword, vm.SelectedTemplate?.Kind); Assert.Equal("Greatsword", vm.WeaponName);
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal(WeaponKind.Greatsword, vm.SelectedWeapon?.Kind);
    }

    [Fact]
    public async Task Stage4Ui_BalanceSearchAndWarnings_AreReadOnlyAndLive()
    {
        AddStage4UiMaster(); var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        var bytes = File.ReadAllBytes(vm.PluginPath);
        vm.Damage = "10"; vm.Weight = "7";
        Assert.Equal("+3", vm.BalanceRows.Single(r => r.Label == "Damage").Difference);
        Assert.Equal("-2", vm.BalanceRows.Single(r => r.Label == "Weight").Difference);
        vm.Damage = "NaN"; Assert.Equal("—", vm.BalanceRows.Single(r => r.Label == "Damage").Difference);
        vm.KeywordSearch = "skyrim greatsword";
        var keyword = Assert.Single(vm.FilteredKeywords); Assert.Equal("WeapTypeGreatsword", keyword.EditorId);
        vm.WeaponKeywords.Add(keyword); Assert.NotNull(vm.CompatibilityWarnings);
        vm.WeaponKeywords.Add(keyword); Assert.Contains("Duplicate", vm.CompatibilityWarnings);
        vm.WeaponKeywords.Clear(); vm.EnchantmentCharge = "100"; Assert.NotNull(vm.CompatibilityWarnings);
        Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath)); Assert.Equal("NaN", vm.Damage);
    }

    [Fact]
    public async Task Stage4Ui_Variants_UndoRedoAndReopen_PreserveRelationshipsAndSharedResources()
    {
        AddStage4UiMaster(); var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        var source = Path.Combine(_root, "variant.nif"); File.WriteAllText(source, "nif");
        await vm.ImportModelCommand.ExecuteAsync(source); await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var parent = vm.SelectedWeapon!;
        vm.VariantTemplate = vm.Templates.Single(t => t.Kind == WeaponKind.Greatsword); vm.VariantLabel = "Two handed";
        vm.VariantMaterialKeyword = Assert.Single(vm.VariantMaterials); vm.VariantMaterialNotes = "Steel"; vm.ShareVariantModels = true;
        await vm.CreateVariantCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal(WeaponKind.Greatsword, vm.SelectedWeapon?.Kind); Assert.Equal(parent.Details.ModelPath, vm.ModelPath);
        Assert.Single(vm.Variants); Assert.Single(vm.ProjectResources);
        await vm.UndoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Empty(vm.Variants); Assert.Single(vm.Weapons);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Single(vm.Variants); Assert.Equal(2, vm.Weapons.Count);
        await vm.OpenProjectCommand.ExecuteAsync(vm.ProjectFilePath); Assert.Null(vm.ErrorMessage); Assert.Single(vm.Variants);
        vm.SelectedWeapon = vm.Weapons.Single(w => w.Kind == WeaponKind.Greatsword);
        await vm.LoadBalanceOriginCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Equal(0x1600u, vm.BalanceTemplate?.FormId);
    }

    [Fact]
    public async Task Stage4Ui_TemperingCommand_SaveUndoRedo_RestoresDraftAndConditions()
    {
        AddStage4UiMaster(); var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        await vm.NewTemperingRecipeCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal("CraftingSmithingSharpeningWheel", vm.SelectedWorkbench?.EditorId);
        vm.RecipeIngredients.Add(new(vm.Options.Ingredients.First(), 1));
        await vm.SaveRecipeCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.True(vm.SelectedRecipe?.IsTempering);
        await vm.UndoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.True(vm.HasUnsavedRecipeChanges);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.True(vm.SelectedRecipe?.IsTempering);
    }
}
