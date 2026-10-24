using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private async Task<SmartModViewModel> PrepareGuidedAsync()
    {
        var vm = await PrepareSmartAsync(); var path = Path.Combine(DataPath, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct();
        foreach (var id in new[] { 0x7D00u, 0x7D01u })
        {
            var entry = new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1 } }; entry.Data.Reference.SetTo(master.Weapons.First().FormKey);
            master.LeveledItems.Add(new LeveledItem(new FormKey(master.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = "GuidedList" + id, Entries = [entry] });
        }
        var outfit = new Outfit(new FormKey(master.ModKey, 0x7D02), SkyrimRelease.SkyrimSE) { EditorID = "GuidedOutfit", Items = [] };
        outfit.Items.Add(new FormLink<IOutfitTargetGetter>(new FormKey(master.ModKey, 0x7D00))); master.Outfits.Add(outfit);
        var npc = new Npc(new FormKey(master.ModKey, 0x7D03), SkyrimRelease.SkyrimSE) { EditorID = "GuidedNpc", Name = "Test NPC" };
        npc.DefaultOutfit.SetTo(outfit.FormKey); master.Npcs.Add(npc);
        var container = new Container(new FormKey(master.ModKey, 0x7D04), SkyrimRelease.SkyrimSE) { EditorID = "GuidedChest", Name = "Test chest", Items = [] };
        var contents = new ContainerEntry { Item = new ContainerItem { Count = 1 } }; contents.Item.Item.SetTo(new FormKey(master.ModKey, 0x7D01)); container.Items.Add(contents); master.Containers.Add(container);
        master.WriteToBinary(path); await vm.InitializeAsync(vm.ProjectPath); Assert.Equal("", vm.Error); return vm;
    }
    [Fact]
    public async Task GuidedListsChooseSpecificNpcViaOutfitOrSpecificChestAndClearOldSelection()
    {
        await using var vm = await PrepareGuidedAsync(); Assert.False(vm.ShowListChoices);
        vm.FilterListCommand.Execute("Equipment"); vm.ListTargetSearch = "guidednpc";
        var npc = Assert.Single(vm.ListTargets, t => t.Reference != null); Assert.Contains("Test NPC", npc.DisplayName);
        vm.SelectedListTarget = npc; var selected = Assert.Single(vm.FilteredLeveledLists); Assert.Equal(selected, vm.SelectedLeveledList);
        Assert.Contains(selected.ListUsages, u => u.Kind == "Npc" && u.Owner == npc.Reference && u.ViaRecord?.FormId == 0x7D02);
        vm.FilterListCommand.Execute("Containers"); Assert.Null(vm.SelectedListTarget); Assert.Null(vm.SelectedLeveledList);
        vm.ListTargetSearch = "Test chest"; vm.SelectedListTarget = Assert.Single(vm.ListTargets, t => t.Reference != null);
        Assert.Equal(0x7D01u, Assert.Single(vm.FilteredLeveledLists).Reference.FormId);
        vm.SkipListCommand.Execute(null); Assert.False(vm.ShowListChoices); Assert.Null(vm.SelectedLeveledList);
    }
    [Fact]
    public async Task GuidedListActionSavesUnsavedWeaponAndRepeatKeepsOneEntry()
    {
        await using var vm = await PrepareGuidedAsync(); vm.FilterListCommand.Execute("Equipment");
        vm.SelectedListTarget = vm.ListTargets.Single(t => t.Reference?.FormId == 0x7D03); vm.ListLevel = "7"; vm.ListCount = "2";
        Assert.Null(vm.SelectedItem!.ConfirmedFormId); await vm.AddToListCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.NotNull(vm.SelectedItem.ConfirmedFormId);
        var bytes = File.ReadAllBytes(vm.PluginPath); await vm.AddToListCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
        var details = (await _builder.GetLeveledListDetailsAsync(vm.PluginPath, DataPath, vm.SelectedLeveledList!.Reference)).Value;
        var entry = Assert.Single(details.Entries, e => e.Reference?.PluginName == "SmartWeapons.esp"); Assert.Equal(7, entry.Level); Assert.Equal(2, entry.Count);
    }
    [Fact]
    public async Task RenameNameAndEditorIdKeepsFormIdAndRecipeAndBlocksConflicts()
    {
        using var vm = await PrepareSmartAsync(); var item = vm.SelectedItem!;
        vm.AddRecipeCommand.Execute("crafting"); vm.AddOrIncrementIngredient(vm.Options.Ingredients.First());
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); var form = item.ConfirmedFormId;
        var recipe = Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, form!.Value)).Value); var originalId = item.EditorId;
        vm.GenerateIdentifierCommand.Execute(null); Assert.NotEqual(originalId, item.EditorId); item.Name = "Перейменований меч"; item.EditorId = "MyAuthor_StarBlade_123";
        await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.Equal(form, item.ConfirmedFormId);
        var weapon = Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value); Assert.Equal(item.Name, weapon.Name); Assert.Equal(item.EditorId, weapon.EditorId);
        Assert.Equal(recipe.FormId, Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, form.Value)).Value).FormId);
        var bytes = File.ReadAllBytes(vm.PluginPath); item.EditorId = "ironsword";
        Assert.Contains("Smart_IdMasterConflict", vm.IdentifierNotice); await vm.SaveItemCommand.ExecuteAsync(null); Assert.NotEmpty(vm.Error); Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
        item.EditorId = vm.Items.First(i => i.Id != item.Id).EditorId.ToLowerInvariant();
        Assert.Contains("Smart_IdDraftConflict", vm.IdentifierNotice); await vm.SaveItemCommand.ExecuteAsync(null); Assert.NotEmpty(vm.Error); Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
        vm.GenerateIdentifierCommand.Execute(null); await vm.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.Equal(form, item.ConfirmedFormId);
        using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal(item.EditorId, reopened.SelectedItem!.EditorId); Assert.Equal(item.Name, reopened.SelectedItem.Name);
    }
}
