using NSubstitute;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Services;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private async Task<WeaponsViewModel> PresetEditorAsync()
    {
        var source = await PrepareAsync();
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder),
            presetRepository: new JsonWeaponPresetRepository(Path.Combine(_root, "presets")));
        await vm.InitializeCommand.ExecuteAsync(null); await vm.OpenProjectCommand.ExecuteAsync(source.ProjectFilePath);
        vm.NewSwordCommand.Execute(null); return vm;
    }

    [Fact]
    public async Task Stage5_PresetsApplyToNewItemWithoutChangingIdentityOrModels_AndSupportUndo()
    {
        var vm = await PresetEditorAsync();
        vm.PresetName = "Strong sword"; vm.Damage = "123"; vm.Description = "Preset description";
        await vm.SavePresetCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Null(vm.PresetError);
        vm.Damage = "7"; vm.Description = "Original description";
        var id = vm.EditorId; var name = vm.WeaponName; var model = vm.ModelPath; var before = File.ReadAllBytes(vm.PluginPath);
        await vm.ApplyPresetCommand.ExecuteAsync(null);
        Assert.Null(vm.PresetError); Assert.Equal("123", vm.Damage); Assert.Equal("Preset description", vm.Description);
        Assert.Equal(id, vm.EditorId); Assert.Equal(name, vm.WeaponName); Assert.Equal(model, vm.ModelPath); Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
        await vm.UndoCommand.ExecuteAsync(null); Assert.Equal("7", vm.Damage); Assert.Equal("Original description", vm.Description);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Equal("123", vm.Damage);
    }

    [Theory]
    [InlineData("kind")] [InlineData("existing")] [InlineData("keyword")] [InlineData("sound")] [InlineData("enchantment")] [InlineData("version")]
    public async Task Stage5_IncompatibleAndStalePresetsChangeNoFields(string scenario)
    {
        var vm = await PresetEditorAsync();
        if (scenario == "existing") { await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); }
        var missing = new PluginRecordReference("Missing.esm", 0x123);
        vm.SelectedPreset = new WeaponPreset { Name = "Stale", Kind = scenario == "kind" ? WeaponKind.Bow : WeaponKind.OneHandSword,
            Damage = 99, Keywords = scenario == "keyword" ? [missing] : [], Sound = scenario == "sound" ? missing : null,
            Enchantment = scenario == "enchantment" ? missing : null, Charge = (ushort)(scenario == "enchantment" ? 100 : 0), Version = scenario == "version" ? 2 : 1 };
        var damage = vm.Damage; var keywords = vm.WeaponKeywords.ToArray(); var sound = vm.SelectedSound; var model = vm.ModelPath;
        await vm.ApplyPresetCommand.ExecuteAsync(null);
        Assert.NotNull(vm.PresetError); Assert.Equal(damage, vm.Damage); Assert.Equal(keywords, vm.WeaponKeywords); Assert.Same(sound, vm.SelectedSound); Assert.Equal(model, vm.ModelPath);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task Stage5_DraftPersistsEveryStepWithNewSchema(int step)
    {
        var (vm, _, scheduler) = await PrepareDraftEditorAsync(); vm.CurrentStep = step; vm.Damage = "unfinished";
        await scheduler.FireAsync(); var draft = ReadEditorDraft(vm);
        Assert.Equal(2, draft.WizardSchemaVersion); Assert.Equal(step, draft.CurrentStep); Assert.Equal("unfinished", draft.Weapon.Damage);
    }

    [Theory]
    [InlineData(1, 4, 5)] [InlineData(2, 4, 4)] [InlineData(2, 5, 5)] [InlineData(2, 6, 6)]
    public async Task Stage5_RecoveryMigratesLegacySummaryAndRetainsNewSteps(int schema, int step, int expected)
    {
        var (vm, drafts, history, _) = await PrepareHistoryEditorAsync();
        var draft = await AbandonedAsync(vm, drafts, history);
        var updated = await drafts.SaveAsync(draft with { WizardSchemaVersion = schema, CurrentStep = step }, 1);
        Assert.True(updated.IsSuccess, updated.Error);
        await vm.FindRecoveryCommand.ExecuteAsync(null); await vm.RestoreDraftCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Equal(expected, vm.CurrentStep); Assert.Equal("Recovered pending", vm.WeaponName);
    }

    [Fact]
    public async Task Stage5_RecipeStepBlocksDirtyDraft_AndDiscardMakesRecipesOptional()
    {
        var vm = await PrepareAsync(); vm.CurrentStep = 4;
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsRecipeStep); Assert.NotNull(vm.ErrorMessage);
        await vm.SaveWizardSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        await vm.NewRecipeCommand.ExecuteAsync(null); vm.RecipeOutputCount = "invalid";
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsRecipeStep); Assert.NotNull(vm.ErrorMessage);
        await vm.SaveRecipeCommand.ExecuteAsync(null); Assert.NotNull(vm.RecipeCountError); Assert.NotNull(vm.RecipeWorkbenchError); Assert.NotNull(vm.RecipeIngredientsError);
        await vm.DiscardRecipeDraftCommand.ExecuteAsync(null); Assert.False(vm.HasUnsavedRecipeChanges);
        await vm.NextStepCommand.ExecuteAsync(null); Assert.True(vm.IsSummaryStep);
    }

    [Fact]
    public async Task Stage5_BatchPreviewAssignmentsAreExplicit_AndSavedOnlyWhenItemSaved()
    {
        var vm = await PrepareAsync(); var plugin = File.ReadAllBytes(vm.PluginPath);
        var third = Path.Combine(_root, "third.nif"); var first = Path.Combine(_root, "first.nif");
        await File.WriteAllTextAsync(third, "third"); await File.WriteAllTextAsync(first, "first");
        await vm.PreviewBatchCommand.ExecuteAsync(new[] { third, first }); Assert.Null(vm.ErrorMessage); Assert.Equal(2, vm.BatchItems.Count);
        vm.BatchItems[0].Assignment = ModelAssignment.ThirdPerson; vm.BatchItems[1].Assignment = ModelAssignment.FirstPerson;
        await vm.ImportBatchCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.EndsWith("third.nif", vm.ModelPath); Assert.EndsWith("first.nif", vm.FirstPersonModelPath);
        Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath)); Assert.Equal(2, vm.ProjectResources.Count);
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        Assert.Equal(vm.FirstPersonModelPath, vm.SelectedWeapon!.Details.FirstPersonModelPath);
    }

    [Fact]
    public async Task Stage5_BatchLanguageChangeRetainsAssignmentsAndSourceIdentity()
    {
        var source = await PrepareAsync(); var localization = Substitute.For<ILocalizationService>();
        localization.GetString(Arg.Any<string>()).Returns(info => info.Arg<string>());
        localization.GetString("Stage5_ResourceOnly").Returns("Resource only");
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), localization);
        await vm.InitializeCommand.ExecuteAsync(null); await vm.OpenProjectCommand.ExecuteAsync(source.ProjectFilePath);
        var file = Path.Combine(_root, "language.nif"); await File.WriteAllTextAsync(file, "model");
        await vm.PreviewBatchCommand.ExecuteAsync(new[] { file }); Assert.Null(vm.ErrorMessage);
        Assert.Equal("Resource only", vm.BatchItems[0].Assignments[0].Label); vm.BatchItems[0].Assignment = ModelAssignment.FirstPerson;
        var path = vm.BatchItems[0].RelativePath;
        localization.GetString("Stage5_ResourceOnly").Returns("Лише ресурс"); localization.LanguageChanged += Raise.Event<Action>();
        Assert.Equal("Лише ресурс", vm.BatchItems[0].Assignments[0].Label);
        Assert.Equal(ModelAssignment.FirstPerson, vm.BatchItems[0].Assignment); Assert.Equal(path, vm.BatchItems[0].RelativePath);
    }
}
