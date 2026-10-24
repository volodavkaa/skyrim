using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private void AddInheritedUiProperties()
    {
        var path = Path.Combine(DataPath, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).Mutable().Construct();
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1800), SkyrimRelease.SkyrimSE) { EditorID = "WeapMaterialIron" });
        master.SoundDescriptors.Add(new SoundDescriptor(new FormKey(master.ModKey, 0x1801), SkyrimRelease.SkyrimSE) { EditorID = "InheritedAttack" });
        master.SoundDescriptors.Add(new SoundDescriptor(new FormKey(master.ModKey, 0x1802), SkyrimRelease.SkyrimSE) { EditorID = "InheritedEquip" });
        var weapon = master.Weapons.First();
        weapon.Keywords = [new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1800))];
        weapon.AttackSound.SetTo(new FormKey(master.ModKey, 0x1801));
        weapon.EquipSound.SetTo(new FormKey(master.ModKey, 0x1802));
        master.WriteToBinary(path);
    }

    [Fact]
    public async Task Items_ModelImportAndReplacement_PreserveInheritance_AfterSaveAndReopen()
    {
        AddInheritedUiProperties();
        var game = File.ReadAllBytes(Path.Combine(DataPath, "Skyrim.esm"));
        var vm = await PrepareAsync();
        var file = Path.Combine(_root, "custom.nif"); File.WriteAllText(file, "nif fixture");
        await vm.ImportModelCommand.ExecuteAsync(file);
        await vm.ImportFirstPersonModelCommand.ExecuteAsync(file);
        Assert.Equal("InheritedAttack", vm.SelectedSound?.EditorId);
        Assert.Equal("WeapMaterialIron", Assert.Single(vm.WeaponKeywords).EditorId);
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var id = vm.SelectedWeapon!.FormId;
        Assert.Contains("IronSword", vm.TemplateOriginText);
        File.WriteAllText(file, "different nif");
        await vm.ReplaceThirdPersonModelCommand.ExecuteAsync(file);
        await vm.SaveSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var reopened = CreateViewModel(); await reopened.InitializeCommand.ExecuteAsync(null);
        await reopened.OpenProjectCommand.ExecuteAsync(vm.ProjectFilePath); Assert.Null(reopened.ErrorMessage);
        Assert.Equal(id, reopened.SelectedWeapon?.FormId);
        Assert.Contains("IronSword", reopened.TemplateOriginText);
        Assert.Contains("InheritedAttack", reopened.SoundsInheritanceText);
        Assert.Contains("InheritedEquip", reopened.SoundsInheritanceText);
        Assert.Contains("Inherited", reopened.KeywordsInheritanceText);
        Assert.Equal(game, File.ReadAllBytes(Path.Combine(DataPath, "Skyrim.esm")));
    }

    [Fact]
    public async Task Items_ExplicitClearSurvivesModelChange_AndRestorationIsAnUnsavedAction()
    {
        AddInheritedUiProperties(); var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        vm.ClearSoundCommand.Execute(null); vm.WeaponKeywords.Clear();
        var file = Path.Combine(_root, "changed.nif"); File.WriteAllText(file, "model");
        await vm.ImportModelCommand.ExecuteAsync(file); await vm.SaveSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Null(vm.SelectedSound); Assert.Empty(vm.WeaponKeywords);
        Assert.Contains("Cleared", vm.SoundsInheritanceText);
        Assert.Contains("Removed", vm.KeywordsInheritanceText);
        var plugin = File.ReadAllBytes(vm.PluginPath);
        vm.RestoreTemplateSoundsCommand.Execute(null); vm.RestoreTemplateKeywordsCommand.Execute(null);
        Assert.Equal("InheritedAttack", vm.SelectedSound?.EditorId); Assert.Single(vm.WeaponKeywords);
        Assert.True(vm.HasUnsavedWeaponChanges); Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath));
        await vm.SaveSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal(new PluginRecordReference("Skyrim.esm", 0x1801), vm.SelectedWeapon!.Details.AttackSound);
        Assert.Equal(new PluginRecordReference("Skyrim.esm", 0x1802), vm.SelectedWeapon.Details.Sounds!["EquipSound"]);
    }

    [Fact]
    public async Task Items_AddChooseDifferentType_DuplicateEditAndDelete_KeepOtherItems()
    {
        AddStage4UiMaster(); AddInheritedUiProperties();
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var first = vm.SelectedWeapon!;
        await vm.AddWeaponCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.CurrentStep); Assert.Null(vm.SelectedWeapon);
        var calls = prompt.ReceivedCalls().Count();
        vm.WeaponTypeSelection = WeaponKind.Greatsword; await vm.PendingSelectionChange;
        Assert.Equal(calls, prompt.ReceivedCalls().Count()); // No prompt for untouched generated defaults.
        Assert.Equal(WeaponKind.Greatsword, vm.SelectedTemplate?.Kind);
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Weapons.Count);
        await vm.DuplicateWeaponCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal(3, vm.Weapons.Count); Assert.Equal(3, vm.Weapons.Select(w => w.FormId).Distinct().Count());
        Assert.Equal(3, vm.Weapons.Select(w => w.EditorId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        vm.WeaponName = "Renamed copy"; await vm.SaveSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        vm.CurrentStep = 5; vm.EditWeaponCommand.Execute(null); Assert.Equal(3, vm.CurrentStep);
        Assert.Contains("Renamed copy", vm.ProjectItemsSummary); Assert.Contains(first.EditorId, vm.ProjectItemsSummary);
        await vm.DeleteSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Weapons.Count); Assert.Contains(vm.Weapons, w => w.FormId == first.FormId && w.Name == first.Name);
    }

    [Fact]
    public async Task Items_ImportedLegacyPlugin_DoesNotInventTemplate_ExplicitRestoreOnly()
    {
        AddInheritedUiProperties(); var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var plugin = vm.PluginPath;
        await vm.OpenPluginCommand.ExecuteAsync(plugin); Assert.Null(vm.ErrorMessage);
        Assert.Contains("unknown", vm.TemplateOriginText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(vm.SelectedTemplate);
        vm.ClearSoundCommand.Execute(null);
        vm.RestoreTemplateSoundsCommand.Execute(null); Assert.NotNull(vm.ErrorMessage); Assert.Null(vm.SelectedSound);
        vm.RestorationTemplate = vm.Templates.First(); vm.RestoreTemplateSoundsCommand.Execute(null);
        Assert.Equal("InheritedAttack", vm.SelectedSound?.EditorId);
        Assert.Contains("unknown", vm.TemplateOriginText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Items_RestoreCharacteristics_IsOneUndoableUnsavedAction_AndKeepsIdentityAndModels()
    {
        AddInheritedUiProperties(); var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var editorId = vm.EditorId; var name = vm.WeaponName; var model = vm.ModelPath;
        vm.Damage = "42"; vm.Weight = "3"; vm.Value = "500"; vm.Speed = "2"; vm.Description = "Custom description";
        var bytes = File.ReadAllBytes(vm.PluginPath);
        vm.RestoreTemplateParametersCommand.Execute(null);
        Assert.Equal("7", vm.Damage); Assert.Equal("9", vm.Weight); Assert.Equal("25", vm.Value); Assert.Equal("1", vm.Speed);
        Assert.Equal(editorId, vm.EditorId); Assert.Equal(name, vm.WeaponName); Assert.Equal(model, vm.ModelPath);
        Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
        await vm.UndoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal("42", vm.Damage); Assert.Equal("3", vm.Weight); Assert.Equal("500", vm.Value); Assert.Equal("2", vm.Speed);
        Assert.Equal("Custom description", vm.Description);
    }

    [Fact]
    public async Task Items_CancelNavigation_DoesNotDuplicateOrReplaceDirtyItem()
    {
        var (vm, _) = await PrepareGuardedEditorAsync(); await vm.CloneSwordCommand.ExecuteAsync(null);
        vm.WeaponName = "Unsaved"; var bytes = File.ReadAllBytes(vm.PluginPath);
        await vm.DuplicateWeaponCommand.ExecuteAsync(null); await vm.AddWeaponCommand.ExecuteAsync(null);
        Assert.Single(vm.Weapons); Assert.Equal("Unsaved", vm.WeaponName); Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
    }
}
