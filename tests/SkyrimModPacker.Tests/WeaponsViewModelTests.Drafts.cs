using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private sealed class ManualDraftScheduler : IDraftAutosaveScheduler
    {
        public Func<Task>? Pending;
        public void Schedule(Func<Task> save) => Pending = save;
        public void Cancel() => Pending = null;
        public async Task FireAsync() { var save = Pending; Pending = null; if (save != null) await save(); }
    }
    private async Task<(WeaponsViewModel Vm, IUnsavedChangesPrompt Prompt, ManualDraftScheduler Scheduler)> PrepareDraftEditorAsync(
        IWeaponDraftRepository? repository = null, bool autosave = true)
    {
        var (source, prompt) = await PrepareGuardedEditorAsync();
        var scheduler = new ManualDraftScheduler();
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), unsavedChangesPrompt: prompt,
            draftRepository: repository ?? new JsonWeaponDraftRepository(Path.Combine(_root, "draft-store")), draftAutosaveScheduler: scheduler)
        { EnableDraftAutosave = autosave };
        await vm.InitializeCommand.ExecuteAsync(null);
        await vm.OpenProjectCommand.ExecuteAsync(source.ProjectFilePath);
        Assert.Null(vm.ErrorMessage); Assert.Null(vm.DraftSaveError);
        return (vm, prompt, scheduler);
    }
    private static WeaponEditorDraft ReadEditorDraft(WeaponsViewModel vm) => JsonSerializer.Deserialize<WeaponEditorDraft>(File.ReadAllText(vm.DraftFilePath!),
        new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;

    [Fact]
    public async Task Drafts_DebouncedSnapshot_PreservesInvalidTextModelIntentAndCollections_WithoutPluginWrites()
    {
        var (vm, _, scheduler) = await PrepareDraftEditorAsync();
        var plugin = File.ReadAllBytes(vm.PluginPath); var project = File.ReadAllBytes(vm.ProjectFilePath);
        vm.WeaponName = "Pending"; vm.Damage = "invalid"; vm.FirstPersonModelPath = @"Custom\First.nif";
        vm.RecipeEditorId = "PendingRecipe"; vm.RecipeOutputCount = "invalid";
        vm.RecipeIngredients.Add(new RecipeIngredientRow(vm.Options.Ingredients.First(), 2));
        vm.WeaponKeywords.Add(vm.WorkbenchChoices.First()); vm.CurrentStep = 3;
        Assert.Equal(EditorDraftState.Clean, ReadEditorDraft(vm).State);
        await scheduler.FireAsync();
        var draft = ReadEditorDraft(vm);
        Assert.Equal(EditorDraftState.Pending, draft.State); Assert.Equal("invalid", draft.Weapon.Damage);
        Assert.Equal("Pending", draft.Weapon.Name); Assert.True(draft.Weapon.FirstPersonEdited);
        Assert.Equal(@"Custom\First.nif", draft.Weapon.FirstPersonPath);
        Assert.Single(draft.Weapon.Keywords); Assert.Single(draft.Recipe.Ingredients);
        Assert.Equal("invalid", draft.Recipe.OutputCount); Assert.Equal(3, draft.CurrentStep);
        Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath)); Assert.Equal(project, File.ReadAllBytes(vm.ProjectFilePath));
        Assert.True(vm.HasUnsavedChanges); Assert.NotNull(vm.DraftSaveStatus);
        Assert.DoesNotContain(Path.GetDirectoryName(vm.PluginPath)!, vm.DraftFilePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Drafts_ManualPluginSave_SynchronizesState_AndOldTimerCannotPublishPendingFields()
    {
        var (vm, _, scheduler) = await PrepareDraftEditorAsync();
        vm.WeaponName = "Pending";
        var oldTimer = scheduler.Pending!;
        await vm.SaveEditorDraftCommand.ExecuteAsync(null);
        Assert.Equal(EditorDraftState.Pending, ReadEditorDraft(vm).State);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Null(vm.DraftSaveError);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(EditorDraftState.Clean, ReadEditorDraft(vm).State);
        Assert.Equal(vm.SelectedWeapon!.FormId, ReadEditorDraft(vm).WeaponFormId);
        await oldTimer();
        Assert.Equal(EditorDraftState.Clean, ReadEditorDraft(vm).State);
    }

    [Fact]
    public async Task Drafts_PartialSave_KeepsOnlyRecipePending_ThenBecomesClean()
    {
        var (vm, _, _) = await PrepareDraftEditorAsync();
        vm.WeaponName = "Pending"; vm.RecipeOutputCount = "bad";
        await vm.SaveAllChangesCommand.ExecuteAsync(null);
        var draft = ReadEditorDraft(vm);
        Assert.NotNull(vm.ErrorMessage); Assert.False(draft.WeaponIsDirty); Assert.True(draft.RecipeIsDirty);
        Assert.NotNull(draft.WeaponFormId); Assert.Equal("bad", draft.Recipe.OutputCount);
        vm.RecipeOutputCount = "1";
        vm.RecipeIngredients.Add(new RecipeIngredientRow(vm.Options.Ingredients.First(), 1));
        await vm.SaveAllChangesCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Equal(EditorDraftState.Clean, ReadEditorDraft(vm).State);
    }

    [Fact]
    public async Task Drafts_StorageFailure_DoesNotPretendPluginFailed_AndBlocksCloseUntilSynchronized()
    {
        var (vm, _, _) = await PrepareDraftEditorAsync();
        var backup = vm.DraftFilePath + ".bak";
        Directory.CreateDirectory(backup);
        vm.WeaponName = "Pending";
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.NotNull(vm.DraftSaveError);
        Assert.NotNull(vm.SelectedWeapon); Assert.False(vm.HasUnsavedChanges);
        Assert.False(await vm.RequestLeaveEditorAsync());
        Directory.Delete(backup);
        Assert.True(await vm.RequestLeaveEditorAsync()); Assert.Null(vm.DraftSaveError);
        Assert.Equal(EditorDraftState.Clean, ReadEditorDraft(vm).State);
    }

    [Fact]
    public async Task Drafts_DiscardMarker_IsDistinctFromClean_AndRecipeDiscardPreservesWeapon()
    {
        var (vm, prompt, _) = await PrepareDraftEditorAsync();
        vm.WeaponName = "Pending weapon"; vm.RecipeEditorId = "PendingRecipe";
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        Assert.True(await vm.RequestLeaveEditorAsync(recipeOnly: true));
        var partial = ReadEditorDraft(vm);
        Assert.Equal(EditorDraftState.Pending, partial.State); Assert.True(partial.WeaponIsDirty);
        Assert.True(partial.RecipeDiscarded); Assert.False(partial.RecipeIsDirty);
        Assert.True(await vm.RequestLeaveEditorAsync());
        var discarded = ReadEditorDraft(vm);
        Assert.Equal(EditorDraftState.Discarded, discarded.State); Assert.True(discarded.WeaponDiscarded);
    }

    [Fact]
    public async Task Drafts_AutosaveOff_AllowsManualDraft_AndStillSynchronizesItAfterPluginSave()
    {
        var (vm, _, scheduler) = await PrepareDraftEditorAsync(autosave: false);
        Assert.Null(vm.DraftFilePath);
        vm.WeaponName = "Pending"; Assert.Null(scheduler.Pending);
        await vm.SaveEditorDraftCommand.ExecuteAsync(null);
        Assert.Equal(EditorDraftState.Pending, ReadEditorDraft(vm).State);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Equal(EditorDraftState.Clean, ReadEditorDraft(vm).State);
        Assert.Null(scheduler.Pending);
    }

    [Fact]
    public async Task Drafts_ActiveAutosaveAndManualWrite_AreSerialized()
    {
        var actual = new JsonWeaponDraftRepository(Path.Combine(_root, "serialized-drafts"));
        var wrapper = Substitute.For<IWeaponDraftRepository>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = false;
        wrapper.SaveAsync(Arg.Any<WeaponEditorDraft>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (hold) { hold = false; entered.SetResult(); await release.Task; }
            return await actual.SaveAsync(call.ArgAt<WeaponEditorDraft>(0), call.ArgAt<long>(1), call.ArgAt<CancellationToken>(2));
        });
        var (vm, _, scheduler) = await PrepareDraftEditorAsync(wrapper);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        var before = File.ReadAllBytes(vm.PluginPath);
        vm.Damage = "31"; hold = true;
        var autosave = scheduler.FireAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Damage = "66";
        var manual = vm.SaveSwordCommand.ExecuteAsync(null);
        Assert.False(manual.IsCompleted); Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
        release.SetResult(); await autosave; await manual;
        Assert.Null(vm.ErrorMessage); Assert.Null(vm.DraftSaveError);
        Assert.Equal("66", ReadEditorDraft(vm).Weapon.Damage); Assert.Equal(EditorDraftState.Clean, ReadEditorDraft(vm).State);
        Assert.Equal((ushort)66, Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value).Damage);
    }

    [Fact]
    public async Task Drafts_FailedTransition_RestoresPendingMarker_AndAnotherSessionUsesAnotherFile()
    {
        var (vm, prompt, _) = await PrepareDraftEditorAsync();
        vm.WeaponName = "Pending";
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        await vm.OpenProjectCommand.ExecuteAsync(Path.Combine(_root, "missing.json"));
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(EditorDraftState.Pending, ReadEditorDraft(vm).State);
        var oldPath = vm.DraftFilePath!; var oldBytes = File.ReadAllBytes(oldPath);
        var other = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder),
            draftRepository: new JsonWeaponDraftRepository(Path.Combine(_root, "draft-store")));
        await other.InitializeCommand.ExecuteAsync(null); await other.OpenProjectCommand.ExecuteAsync(vm.ProjectFilePath);
        Assert.NotEqual(oldPath, other.DraftFilePath); Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
        Assert.False(other.HasUnsavedChanges); // No automatic restore in 3.2.
    }

    [Fact]
    public async Task Drafts_ProjectSwitch_KeepsOldDiscardMarker_AndSidecarsStayOutsidePackingFiles()
    {
        var (vm, prompt, _) = await PrepareDraftEditorAsync();
        vm.WeaponName = "Old pending";
        await vm.SaveEditorDraftCommand.ExecuteAsync(null);
        var oldDraftPath = vm.DraftFilePath!;
        var next = (await new WeaponProjectService(_builder).CreateAsync(new CreatePluginRequest
        { PluginFileName = "Other.esp", OutputDirectory = Path.Combine(_root, "next"), GameDataPath = DataPath })).Value;
        prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        await vm.OpenProjectCommand.ExecuteAsync(next.ProjectFilePath);
        Assert.Null(vm.ErrorMessage); Assert.NotEqual(oldDraftPath, vm.DraftFilePath);
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        Assert.Equal(EditorDraftState.Discarded, JsonSerializer.Deserialize<WeaponEditorDraft>(File.ReadAllText(oldDraftPath), options)!.State);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        var packing = await new WeaponProjectService(_builder).GetPackingFilesAsync(vm.ProjectFilePath, DataPath);
        Assert.True(packing.IsSuccess, packing.Error);
        Assert.EndsWith(".esp", Assert.Single(packing.Value).AbsolutePath);
        Assert.DoesNotContain(packing.Value, f => f.AbsolutePath == vm.DraftFilePath || f.AbsolutePath == oldDraftPath);
    }

    [Fact]
    public async Task Drafts_EditsDuringIo_KeepScheduledStatusUntilNewestSnapshotIsWritten()
    {
        var actual = new JsonWeaponDraftRepository(Path.Combine(_root, "pending-drafts"));
        var wrapper = Substitute.For<IWeaponDraftRepository>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = false;
        wrapper.SaveAsync(Arg.Any<WeaponEditorDraft>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (hold) { hold = false; entered.SetResult(); await release.Task; }
            return await actual.SaveAsync(call.ArgAt<WeaponEditorDraft>(0), call.ArgAt<long>(1), call.ArgAt<CancellationToken>(2));
        });
        var (vm, _, scheduler) = await PrepareDraftEditorAsync(wrapper);
        vm.WeaponName = "First"; hold = true;
        var write = scheduler.FireAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.WeaponName = "Newest";
        release.SetResult(); await write;
        Assert.Equal("First", ReadEditorDraft(vm).Weapon.Name);
        Assert.Equal("Draft save scheduled.", vm.DraftSaveStatus);
        await scheduler.FireAsync();
        Assert.Equal("Newest", ReadEditorDraft(vm).Weapon.Name);
        Assert.Equal("Draft saved separately from the plugin.", vm.DraftSaveStatus);
    }
}
