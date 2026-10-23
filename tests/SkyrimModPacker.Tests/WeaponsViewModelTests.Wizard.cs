using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task Wizard_KeepsDraftAcrossSteps_ShowsChanges_AndSavesOnlyOnFinalAction()
    {
        var vm = await PrepareAsync();
        var before = await File.ReadAllBytesAsync(vm.PluginPath);
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsTemplateStep);
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsModelStep);
        var nif = Path.Combine(_root, "Custom.nif");
        await File.WriteAllTextAsync(nif, "model fixture");
        await vm.ImportModelCommand.ExecuteAsync(nif);
        Assert.Null(vm.ErrorMessage);
        var model = vm.ModelPath;
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsPropertiesStep);
        vm.WeaponName = "My wizard sword"; vm.Damage = "42"; vm.Description = "Unsaved description";
        var editorId = vm.EditorId;
        vm.PreviousStepCommand.Execute(null);
        vm.PreviousStepCommand.Execute(null);
        Assert.True(vm.IsTemplateStep);
        Assert.Equal(model, vm.ModelPath);
        Assert.Equal("My wizard sword", vm.WeaponName);
        await vm.NextStepCommand.ExecuteAsync(null);
        await vm.NextStepCommand.ExecuteAsync(null);
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsSummaryStep);
        Assert.Equal(editorId, vm.EditorId);
        Assert.Contains(vm.WeaponSummary, r => r.IsChanged && r.Value == "7 → 42");
        Assert.Contains(vm.WeaponSummary, r => r.Value.Contains(model));
        Assert.Contains("Swords.esp", vm.SummaryFiles);
        Assert.Contains("meshes/" + model.Replace('\\', '/'), vm.SummaryFiles);
        Assert.Equal(before, await File.ReadAllBytesAsync(vm.PluginPath));
        Assert.Empty(vm.Weapons);
        await vm.SaveWizardSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsWizardSaved);
        Assert.Equal("My wizard sword", Assert.Single(vm.Weapons).Name);
        Assert.Equal(model, vm.SelectedWeapon!.Details.ModelPath);
        vm.PreviousStepCommand.Execute(null);
        vm.Damage = "43";
        Assert.False(vm.IsWizardSaved);
        await vm.NextStepCommand.ExecuteAsync(null);
        await vm.SaveWizardSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsWizardSaved);
        Assert.Equal((ushort)43, Assert.Single(vm.Weapons).Damage);
        Assert.True(File.Exists(vm.PluginPath + ".bak"));
    }

    [Theory]
    [InlineData(nameof(WeaponsViewModel.EditorId), "123bad", nameof(WeaponsViewModel.EditorIdError))]
    [InlineData(nameof(WeaponsViewModel.WeaponName), "", nameof(WeaponsViewModel.WeaponNameError))]
    [InlineData(nameof(WeaponsViewModel.Damage), "65536", nameof(WeaponsViewModel.DamageError))]
    [InlineData(nameof(WeaponsViewModel.Weight), "NaN", nameof(WeaponsViewModel.WeightError))]
    [InlineData(nameof(WeaponsViewModel.Value), "-1", nameof(WeaponsViewModel.ValueError))]
    [InlineData(nameof(WeaponsViewModel.Speed), "0", nameof(WeaponsViewModel.SpeedError))]
    [InlineData(nameof(WeaponsViewModel.Reach), "Infinity", nameof(WeaponsViewModel.ReachError))]
    [InlineData(nameof(WeaponsViewModel.EnchantmentCharge), "65536", nameof(WeaponsViewModel.ChargeError))]
    public async Task Wizard_InvalidFieldsStayOnProperties_WithoutWriting(string field, string invalid, string error)
    {
        var vm = await PrepareAsync();
        vm.CurrentStep = 3;
        var before = await File.ReadAllBytesAsync(vm.PluginPath);
        var property = typeof(WeaponsViewModel).GetProperty(field)!;
        var valid = property.GetValue(vm);
        property.SetValue(vm, invalid);
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsPropertiesStep);
        Assert.NotNull(typeof(WeaponsViewModel).GetProperty(error)!.GetValue(vm));
        Assert.Equal(before, await File.ReadAllBytesAsync(vm.PluginPath));
        property.SetValue(vm, valid);
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsSummaryStep);
        Assert.Null(typeof(WeaponsViewModel).GetProperty(error)!.GetValue(vm));
    }

    [Fact]
    public async Task Wizard_MissingProjectOrTemplate_ShowsInlineError()
    {
        var vm = CreateViewModel();
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsModStep);
        Assert.NotNull(vm.ProjectError);
        await vm.InitializeCommand.ExecuteAsync(null);
        await vm.CreatePluginCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsTemplateStep);
        vm.SelectedTemplate = null;
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsTemplateStep);
        Assert.NotNull(vm.TemplateError);
        vm.SelectedTemplate = vm.Templates.Single();
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsModelStep);
        Assert.Null(vm.TemplateError);
    }

    [Theory]
    [InlineData(@"..\missing.nif")]
    [InlineData(@"C:\missing.nif")]
    [InlineData(@"Custom\missing.nif")]
    public async Task Wizard_InvalidOrMissingCustomModel_StaysOnModelStep(string path)
    {
        var vm = await PrepareAsync();
        vm.CurrentStep = 2;
        vm.ModelPath = path;
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsModelStep);
        Assert.NotNull(vm.ModelError);
        vm.ModelPath = vm.SelectedTemplate!.Details.ModelPath;
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsPropertiesStep);
        Assert.Null(vm.ModelError);
    }

    [Fact]
    public async Task Wizard_NewSwordSuggestsUniqueEditorId_AndExistingSwordCanBeEditedWithoutTemplate()
    {
        var vm = await PrepareAsync();
        var first = vm.EditorId;
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        vm.NewSwordCommand.Execute(null);
        Assert.NotEqual(first, vm.EditorId);
        Assert.EndsWith("2", vm.EditorId);
        var suggested = vm.EditorId;
        vm.EditorId = first;
        vm.CurrentStep = 3;
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.NotNull(vm.EditorIdError);
        Assert.True(vm.IsPropertiesStep);
        vm.EditorId = suggested;
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsSummaryStep);
        vm.SelectedWeapon = vm.Weapons.Single();
        vm.SelectedTemplate = null;
        vm.CurrentStep = 1;
        await vm.NextStepCommand.ExecuteAsync(null);
        await vm.NextStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsPropertiesStep);
        Assert.Null(vm.TemplateError);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("MyMod.esp")]
    [InlineData("")]
    public async Task Wizard_InvalidProjectName_ShowsErrorBeforeCreatingFiles(string name)
    {
        var vm = CreateViewModel();
        vm.PluginName = name;
        await vm.CreatePluginCommand.ExecuteAsync(null);
        Assert.NotNull(vm.PluginNameError);
        Assert.Empty(vm.PluginPath);
        Assert.False(Directory.Exists(vm.OutputDirectory));
    }
}
