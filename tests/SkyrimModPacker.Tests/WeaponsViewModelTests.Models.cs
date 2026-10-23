using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private void AddModelTemplate()
    {
        var masterPath = Path.Combine(DataPath, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(masterPath).Mutable().Construct();
        var stat = new Static(new FormKey(master.ModKey, 0x12345), SkyrimRelease.SkyrimSE)
        { EditorID = "FirstPersonIron", Model = new Model { File = @"Weapons\Iron\First.nif" } };
        master.Statics.Add(stat); master.Weapons.First().FirstPersonModel.SetTo(stat.FormKey);
        master.WriteToBinary(masterPath);
    }

    [Fact]
    public async Task Models_Editor_IndependentImport_ReopenAndRestoreOriginal()
    {
        AddModelTemplate();
        var vm = await PrepareAsync();
        var source = Path.Combine(_root, "custom-first.nif"); File.WriteAllText(source, "first model");
        var third = vm.ModelPath;
        await vm.ImportFirstPersonModelCommand.ExecuteAsync(source);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(third, vm.ModelPath);
        var first = vm.FirstPersonModelPath;
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var id = vm.SelectedWeapon!.FormId;
        var project = vm.ProjectFilePath;
        var origin = await new WeaponProjectService(_builder).GetTemplateOriginAsync(project, DataPath, id);
        Assert.Equal(0x12EB7u, origin.Value);
        var reopened = CreateViewModel();
        await reopened.InitializeCommand.ExecuteAsync(null);
        await reopened.OpenProjectCommand.ExecuteAsync(project);
        Assert.Null(reopened.ErrorMessage);
        Assert.Equal(first, reopened.FirstPersonModelPath);
        await reopened.RestoreFirstPersonModelCommand.ExecuteAsync(null);
        Assert.Null(reopened.ErrorMessage);
        Assert.Equal(@"Weapons\Iron\First.nif", reopened.FirstPersonModelPath);
        await reopened.SaveSwordCommand.ExecuteAsync(null);
        Assert.Null(reopened.ErrorMessage);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(reopened.PluginPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(0x12345u, mod.Weapons.First().FirstPersonModel.FormKey.ID);
        Assert.Equal(id, mod.Weapons.First().FormKey.ID);
        Assert.Single(mod.Statics);
    }

    [Fact]
    public async Task Models_UnsavedFirstPersonChange_BlocksPacking_AndMissingModelBlocksSaving()
    {
        AddModelTemplate();
        var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        vm.IsWizardSaved = true;
        vm.FirstPersonModelPath = @"Custom\Missing.nif";
        Assert.False(vm.IsWizardSaved);
        await vm.SaveSwordCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(vm.PluginPath, SkyrimRelease.SkyrimSE);
        Assert.Empty(mod.Statics);
        Assert.Equal(0x12345u, mod.Weapons.First().FirstPersonModel.FormKey.ID);
    }

    [Fact]
    public async Task Models_ThirdPersonRestore_DoesNotAlterFirstPersonDraft()
    {
        AddModelTemplate();
        var vm = await PrepareAsync();
        var source = Path.Combine(_root, "first.nif"); File.WriteAllText(source, "first");
        await vm.ImportFirstPersonModelCommand.ExecuteAsync(source);
        var first = vm.FirstPersonModelPath;
        vm.ModelPath = @"Custom\third.nif";
        await vm.RestoreThirdPersonModelCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(@"Weapons\Iron\LongSword.nif", vm.ModelPath);
        Assert.Equal(first, vm.FirstPersonModelPath);
    }
}
