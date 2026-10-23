using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private async Task<(WeaponsViewModel Vm, IUnsavedChangesPrompt Prompt)> PrepareGuardedEditorAsync()
    {
        var masterPath = Path.Combine(DataPath, "Skyrim.esm");
        var master = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(masterPath).Mutable().Construct();
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1101), SkyrimRelease.SkyrimSE) { EditorID = "CraftingSmithingForge" });
        master.MiscItems.Add(new MiscItem(new FormKey(master.ModKey, 0x1102), SkyrimRelease.SkyrimSE) { EditorID = "IngotIron" });
        master.WriteToBinary(masterPath);
        var prompt = Substitute.For<IUnsavedChangesPrompt>();
        prompt.ShowAsync().Returns(UnsavedChangesChoice.ContinueEditing);
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), unsavedChangesPrompt: prompt)
        { PluginName = "Guarded", OutputDirectory = Path.Combine(_root, "guarded") };
        await vm.InitializeCommand.ExecuteAsync(null);
        await vm.CreatePluginCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasUnsavedChanges);
        return (vm, prompt);
    }

    [Theory]
    [InlineData(nameof(WeaponsViewModel.EditorId), "ChangedId")]
    [InlineData(nameof(WeaponsViewModel.WeaponName), "Changed name")]
    [InlineData(nameof(WeaponsViewModel.Damage), "invalid")]
    [InlineData(nameof(WeaponsViewModel.Weight), "NaN")]
    [InlineData(nameof(WeaponsViewModel.Value), "123")]
    [InlineData(nameof(WeaponsViewModel.Description), "Description")]
    [InlineData(nameof(WeaponsViewModel.Speed), "1.3")]
    [InlineData(nameof(WeaponsViewModel.Reach), "1.2")]
    [InlineData(nameof(WeaponsViewModel.ModelPath), @"Custom\Third.nif")]
    [InlineData(nameof(WeaponsViewModel.FirstPersonModelPath), @"Custom\First.nif")]
    [InlineData(nameof(WeaponsViewModel.EnchantmentCharge), "400")]
    public async Task Unsaved_WeaponFields_TrackInvalidTextAndRevertingToBaseline(string field, string value)
    {
        var (vm, _) = await PrepareGuardedEditorAsync();
        var property = typeof(WeaponsViewModel).GetProperty(field)!;
        var before = property.GetValue(vm);
        property.SetValue(vm, value);
        Assert.True(vm.HasUnsavedWeaponChanges); Assert.True(vm.HasUnsavedChanges);
        property.SetValue(vm, before);
        Assert.False(vm.HasUnsavedWeaponChanges); Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Unsaved_CollectionsAndChoices_TrackMutationsAndReplacements()
    {
        var (vm, _) = await PrepareGuardedEditorAsync();
        var option = new RecordOption { Reference = new PluginRecordReference("Skyrim.esm", 0x101) };
        vm.WeaponKeywords.Add(option); Assert.True(vm.HasUnsavedWeaponChanges);
        vm.WeaponKeywords.Remove(option); Assert.False(vm.HasUnsavedWeaponChanges);
        vm.WeaponKeywords = [option]; Assert.True(vm.HasUnsavedWeaponChanges);
        vm.WeaponKeywords.Clear(); Assert.False(vm.HasUnsavedWeaponChanges);
        vm.SelectedSound = option; Assert.True(vm.HasUnsavedWeaponChanges);
        vm.SelectedSound = null; Assert.False(vm.HasUnsavedWeaponChanges);
        vm.SelectedEnchantment = option; Assert.True(vm.HasUnsavedWeaponChanges);
        vm.ClearEnchantmentCommand.Execute(null); Assert.False(vm.HasUnsavedWeaponChanges);
        vm.RecipeIngredients.Add(new RecipeIngredientRow(option, 2)); Assert.True(vm.HasUnsavedRecipeChanges);
        vm.RecipeIngredients.Clear(); Assert.False(vm.HasUnsavedRecipeChanges);
        vm.RecipeIngredients = [new RecipeIngredientRow(option, 1)]; Assert.True(vm.HasUnsavedRecipeChanges);
        vm.RecipeIngredients.Clear(); Assert.False(vm.HasUnsavedRecipeChanges);
        vm.SelectedPerk = option; Assert.True(vm.HasUnsavedRecipeChanges);
        vm.ClearPerkCommand.Execute(null); Assert.False(vm.HasUnsavedRecipeChanges);
        vm.SelectedWorkbench = null; Assert.True(vm.HasUnsavedRecipeChanges);
    }

    [Theory]
    [InlineData(nameof(WeaponsViewModel.RecipeEditorId), "CustomRecipe")]
    [InlineData(nameof(WeaponsViewModel.RecipeOutputCount), "bad")]
    public async Task Unsaved_RecipeTextFields_RevertWithoutLosingWeaponDraft(string field, string value)
    {
        var (vm, _) = await PrepareGuardedEditorAsync();
        vm.Damage = "31";
        var property = typeof(WeaponsViewModel).GetProperty(field)!;
        var before = property.GetValue(vm);
        property.SetValue(vm, value); Assert.True(vm.HasUnsavedRecipeChanges);
        property.SetValue(vm, before); Assert.False(vm.HasUnsavedRecipeChanges);
        Assert.True(vm.HasUnsavedWeaponChanges); Assert.Equal("31", vm.Damage);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.ContinueEditing, false)]
    [InlineData(UnsavedChangesChoice.Discard, true)]
    [InlineData(UnsavedChangesChoice.Save, true)]
    public async Task Unsaved_CloseGuard_OffersThreeChoices_AndWritesOnlyForSave(UnsavedChangesChoice choice, bool mayLeave)
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        var plugin = File.ReadAllBytes(vm.PluginPath);
        vm.WeaponName = "Pending sword";
        prompt.ShowAsync().Returns(choice);
        Assert.Equal(mayLeave, await vm.RequestLeaveEditorAsync());
        await prompt.Received(1).ShowAsync();
        if (choice == UnsavedChangesChoice.Save)
        {
            Assert.False(vm.HasUnsavedChanges);
            Assert.Equal("Pending sword", Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value).Name);
        }
        else
        {
            Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath));
            Assert.Equal("Pending sword", vm.WeaponName); // Discard commits only when the requested transition succeeds.
        }
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("disk")]
    public async Task Unsaved_FailedSave_BlocksLeavingAndKeepsDraft(string failure)
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        var plugin = File.ReadAllBytes(vm.PluginPath);
        vm.WeaponName = "Keep pending";
        if (failure == "validation") vm.Damage = "invalid";
        else Directory.CreateDirectory(vm.PluginPath + ".bak");
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Save);
        Assert.False(await vm.RequestLeaveEditorAsync());
        Assert.NotNull(vm.ErrorMessage); Assert.True(vm.HasUnsavedChanges);
        Assert.Equal("Keep pending", vm.WeaponName);
        Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath));
    }

    [Fact]
    public async Task Unsaved_WeaponSave_PreservesRecipeDraft_AndSaveAllHandlesPartialFailure()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        vm.WeaponName = "Pending sword";
        vm.RecipeEditorId = "PendingRecipe";
        vm.RecipeOutputCount = "invalid";
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Save);
        Assert.False(await vm.RequestLeaveEditorAsync());
        Assert.NotNull(vm.SelectedWeapon); Assert.False(vm.HasUnsavedWeaponChanges);
        Assert.True(vm.HasUnsavedRecipeChanges); Assert.Equal("invalid", vm.RecipeOutputCount);
        vm.RecipeOutputCount = "2";
        vm.RecipeIngredients.Add(new RecipeIngredientRow(vm.Options.Ingredients.Single(i => i.EditorId == "IngotIron"), 3));
        Assert.True(await vm.RequestLeaveEditorAsync());
        Assert.False(vm.HasUnsavedChanges);
        var recipe = Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, vm.SelectedWeapon!.FormId)).Value);
        Assert.Equal("PendingRecipe", recipe.EditorId); Assert.Equal((ushort)2, recipe.OutputCount);
        Assert.Equal(3, Assert.Single(recipe.Ingredients).Count);
    }

    [Fact]
    public async Task Unsaved_BusyOrPendingPrompt_RejectsConcurrentLeaveRequests()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        vm.IsLoading = true;
        Assert.False(await vm.RequestLeaveEditorAsync());
        vm.IsLoading = false; vm.WeaponName = "Pending";
        var completion = new TaskCompletionSource<UnsavedChangesChoice>();
        prompt.ShowAsync().Returns(completion.Task);
        var first = vm.RequestLeaveEditorAsync();
        Assert.True(vm.IsEditorBusy);
        Assert.False(await vm.RequestLeaveEditorAsync());
        completion.SetResult(UnsavedChangesChoice.ContinueEditing);
        Assert.False(await first); Assert.False(vm.IsEditorBusy);
        await prompt.Received(1).ShowAsync();
    }

    [Theory]
    [InlineData("project")]
    [InlineData("plugin")]
    [InlineData("import")]
    [InlineData("create")]
    [InlineData("templates")]
    [InlineData("data")]
    public async Task Unsaved_ContextChanges_CancelBeforeReplacingProjectOrFields(string operation)
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        var project = vm.ProjectFilePath; var plugin = vm.PluginPath;
        vm.WeaponName = "Keep me";
        switch (operation)
        {
            case "project": await vm.OpenProjectCommand.ExecuteAsync("missing.json"); break;
            case "plugin": await vm.OpenPluginCommand.ExecuteAsync("missing.esp"); break;
            case "import": await vm.ImportProjectCommand.ExecuteAsync("missing.esp"); break;
            case "create": await vm.CreatePluginCommand.ExecuteAsync(null); break;
            case "templates": await vm.LoadTemplatesCommand.ExecuteAsync(null); break;
            case "data": await vm.ChooseGameDataCommand.ExecuteAsync(_root); break;
        }
        Assert.Equal(project, vm.ProjectFilePath); Assert.Equal(plugin, vm.PluginPath);
        Assert.Equal("Keep me", vm.WeaponName); Assert.True(vm.HasUnsavedChanges);
        await prompt.Received(1).ShowAsync();
    }

    [Fact]
    public async Task Unsaved_RecordTemplateAndRecipeSelectors_KeepCommittedSelectionOnCancel()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        var selected = vm.SelectedWeapon;
        vm.WeaponName = "Pending";
        vm.TemplateSelection = new SwordTemplate { FormId = 42, EditorId = "Other" };
        await vm.PendingSelectionChange;
        Assert.Same(selected, vm.SelectedWeapon); Assert.Equal("Pending", vm.WeaponName);
        vm.WeaponSelection = new WeaponRecord { FormId = 43, EditorId = "Other", Name = "Other" };
        await vm.PendingSelectionChange;
        Assert.Same(selected, vm.SelectedWeapon); Assert.Equal("Pending", vm.WeaponName);
        vm.RecipeEditorId = "PendingRecipe";
        vm.RecipeSelection = new CraftingRecipe { EditorId = "OtherRecipe", Workbench = vm.WorkbenchChoices.First().Reference };
        await vm.PendingSelectionChange;
        Assert.Null(vm.SelectedRecipe); Assert.Equal("PendingRecipe", vm.RecipeEditorId);
        await prompt.Received(3).ShowAsync();
    }

    [Fact]
    public async Task Unsaved_DiscardedRecordSwitch_DoesNotWritePendingFields()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        var bytes = File.ReadAllBytes(vm.PluginPath);
        vm.WeaponName = "Pending";
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        vm.WeaponSelection = new WeaponRecord { EditorId = "OtherSword", Name = "Another displayed record", FormId = 42 };
        await vm.PendingSelectionChange;
        Assert.Equal("Another displayed record", vm.WeaponName); Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
    }

    [Fact]
    public async Task Unsaved_ImportsAndReadOnlyRefreshes_HaveSeparateDiskAndDraftSemantics()
    {
        var (vm, _) = await PrepareGuardedEditorAsync();
        var dds = Path.Combine(_root, "texture.dds"); File.WriteAllText(dds, "fixture");
        await vm.ImportTexturesCommand.ExecuteAsync(new[] { dds });
        Assert.False(vm.HasUnsavedChanges);
        vm.Damage = "31";
        await vm.CheckModelsCommand.ExecuteAsync(null);
        await vm.RefreshResourceCatalogCommand.ExecuteAsync(null);
        await vm.LoadOptionsCommand.ExecuteAsync(null);
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.Equal("31", vm.Damage); Assert.True(vm.HasUnsavedWeaponChanges);
        var nif = Path.Combine(_root, "model.nif"); File.WriteAllText(nif, "fixture");
        await vm.ImportFirstPersonModelCommand.ExecuteAsync(nif);
        Assert.True(vm.HasUnsavedWeaponChanges);
        Assert.Single(vm.ProjectResources, p => p.EndsWith(".nif"));
    }

    [Fact]
    public async Task Unsaved_PromptFailureAndFailedOpen_PreservePendingDraft()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        vm.WeaponName = "Pending";
        prompt.ShowAsync().Returns(Task.FromException<UnsavedChangesChoice>(new IOException("prompt failed")));
        Assert.False(await vm.RequestLeaveEditorAsync()); Assert.Equal("prompt failed", vm.ErrorMessage);
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        await vm.OpenProjectCommand.ExecuteAsync(Path.Combine(_root, "missing.json"));
        Assert.NotNull(vm.ErrorMessage); Assert.Equal("Pending", vm.WeaponName); Assert.True(vm.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Save)]
    [InlineData(UnsavedChangesChoice.Discard)]
    public async Task Unsaved_ProjectSwitch_AppliesChoiceToOldProjectBeforeOpeningNew(UnsavedChangesChoice choice)
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        var oldPlugin = vm.PluginPath;
        var target = (await new WeaponProjectService(_builder).CreateAsync(new CreatePluginRequest
        { PluginFileName = "Other.esp", OutputDirectory = Path.Combine(_root, "other"), GameDataPath = DataPath })).Value;
        vm.WeaponName = "Pending in old project";
        prompt.ShowAsync().Returns(choice);
        await vm.OpenProjectCommand.ExecuteAsync(target.ProjectFilePath);
        Assert.Null(vm.ErrorMessage); Assert.Equal(target.ProjectFilePath, vm.ProjectFilePath);
        Assert.False(vm.HasUnsavedChanges); Assert.Empty(vm.Weapons);
        var oldWeapons = (await _builder.GetWeaponsAsync(oldPlugin)).Value;
        if (choice == UnsavedChangesChoice.Save) Assert.Equal("Pending in old project", Assert.Single(oldWeapons).Name);
        else Assert.Empty(oldWeapons);
    }

    [Fact]
    public async Task Unsaved_ExplicitNewDrafts_AreDirty_AndDataReloadDiscardsOnlyAfterSuccess()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        await vm.NewSwordCommand.ExecuteAsync(null);
        Assert.True(vm.HasUnsavedWeaponChanges);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.False(vm.HasUnsavedChanges);
        await vm.NewRecipeCommand.ExecuteAsync(null);
        Assert.True(vm.HasUnsavedRecipeChanges);
        vm.WeaponName = "Pending";
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        await vm.ChooseGameDataCommand.ExecuteAsync(_root); // Invalid folder must not consume the draft.
        Assert.True(vm.HasUnsavedChanges); Assert.Equal("Pending", vm.WeaponName);
        await vm.ChooseGameDataCommand.ExecuteAsync(DataPath);
        Assert.Null(vm.ErrorMessage); Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(vm.SelectedWeapon!.Name, vm.WeaponName);
    }

    [Fact]
    public async Task Unsaved_RecipeTransition_SaveDoesNotSaveOrResetWeaponEdits()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        vm.Damage = "55";
        vm.RecipeEditorId = "PendingRecipe";
        vm.RecipeIngredients.Add(new RecipeIngredientRow(vm.Options.Ingredients.Single(i => i.EditorId == "IngotIron"), 1));
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Save);
        await vm.NewRecipeCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.True(vm.HasUnsavedWeaponChanges);
        Assert.Equal("55", vm.Damage);
        Assert.Equal((ushort)7, Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value).Damage);
        Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, vm.SelectedWeapon!.FormId)).Value);
        Assert.True(vm.HasUnsavedRecipeChanges); // Explicit new recipe is a separate pending draft.
    }

    [Fact]
    public async Task Unsaved_TransientSelectionClearsAndChangedSettings_DoNotConsumeDraft()
    {
        var (vm, prompt) = await PrepareGuardedEditorAsync();
        var template = vm.SelectedTemplate;
        vm.WeaponName = "Pending";
        vm.TemplateSearch = "no matching template";
        vm.TemplateSelection = null; // WPF can send null when ItemsSource filtering removes the visible item.
        Assert.Same(template, vm.SelectedTemplate);
        Assert.Equal("Pending", vm.WeaponName);
        var dataBefore = vm.GameDataPath;
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new SkyrimModPacker.Core.Models.AppSettings { SkyrimDataPath = _root });
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(dataBefore, vm.GameDataPath);
        Assert.Same(template, vm.SelectedTemplate); Assert.Equal("Pending", vm.WeaponName);
        Assert.True(vm.HasUnsavedChanges);
        await prompt.DidNotReceive().ShowAsync();
    }

    [Fact]
    public async Task Unsaved_CancelledSave_DoesNotAuthorizeLeaving()
    {
        var (savedVm, _) = await PrepareGuardedEditorAsync();
        await savedVm.CloneSwordCommand.ExecuteAsync(null);
        var before = File.ReadAllBytes(savedVm.PluginPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Substitute.For<SkyrimModPacker.Core.Interfaces.IPluginBuilderService>();
        builder.UpdateSwordAsync(Arg.Any<UpdateSwordRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
            return SkyrimModPacker.Core.Common.Result<WeaponRecord>.Failure("Unexpected completion");
        });
        var prompt = Substitute.For<IUnsavedChangesPrompt>();
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Save);
        var vm = new WeaponsViewModel(builder, _settings, _profiles, new WeaponProjectService(_builder), unsavedChangesPrompt: prompt)
        { PluginPath = savedVm.PluginPath, ProjectFilePath = savedVm.ProjectFilePath, SelectedWeapon = savedVm.SelectedWeapon };
        vm.WeaponName = "Pending after cancellation";
        var leave = vm.RequestLeaveEditorAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelOperationCommand.Execute(null);
        Assert.False(await leave);
        Assert.True(vm.HasUnsavedChanges); Assert.False(vm.IsEditorBusy);
        Assert.Equal("Pending after cancellation", vm.WeaponName);
        Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
    }
}
