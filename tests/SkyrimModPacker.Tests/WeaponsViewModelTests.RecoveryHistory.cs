using NSubstitute;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    private async Task<(WeaponsViewModel Vm, JsonWeaponDraftRepository Drafts, WorkspaceHistoryService History, IWorkspaceConflictPrompt Prompt)> PrepareHistoryEditorAsync()
    {
        var (source, unsaved) = await PrepareGuardedEditorAsync();
        unsaved.ShowAsync().Returns(UnsavedChangesChoice.Discard);
        var store = Path.Combine(_root, "recovery-data");
        var drafts = new JsonWeaponDraftRepository(store); var history = new WorkspaceHistoryService(store);
        var prompt = Substitute.For<IWorkspaceConflictPrompt>(); prompt.ConfirmAsync(Arg.Any<string>()).Returns(false);
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder),
            unsavedChangesPrompt: unsaved, draftRepository: drafts, workspaceHistory: history, conflictPrompt: prompt);
        await vm.InitializeCommand.ExecuteAsync(null); await vm.OpenProjectCommand.ExecuteAsync(source.ProjectFilePath);
        Assert.Null(vm.ErrorMessage); return (vm, drafts, history, prompt);
    }

    private async Task<WeaponEditorDraft> AbandonedAsync(WeaponsViewModel vm, JsonWeaponDraftRepository drafts, WorkspaceHistoryService history, bool fingerprints = true)
    {
        var draft = new WeaponEditorDraft
        {
            WriterSessionId = Guid.NewGuid(), PluginPath = vm.PluginPath, ProjectFilePath = vm.ProjectFilePath, GameDataPath = DataPath,
            State = EditorDraftState.Pending, WeaponIsDirty = true, TemplateFormId = 0x12EB7,
            SourceRevision = fingerprints ? await history.InspectAsync(new(vm.PluginPath, vm.ProjectFilePath, DataPath)) : null,
            Weapon = new() { EditorId = "RecoveredSword", Name = "Recovered pending", Damage = "23", Weight = "9", Value = "25", ModelPath = @"Weapons\Iron\LongSword.nif" }
        };
        Assert.True((await drafts.SaveAsync(draft, 0)).IsSuccess);
        await vm.FindRecoveryCommand.ExecuteAsync(null);
        Assert.Single(vm.RecoveryCandidates); return draft;
    }

    [Fact]
    public async Task Recovery_RestoresOnlyMemory_ThenSavesToConfirmedPlugin()
    {
        var (vm, drafts, history, _) = await PrepareHistoryEditorAsync();
        await AbandonedAsync(vm, drafts, history);
        var before = File.ReadAllBytes(vm.PluginPath);
        await vm.RestoreDraftCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Equal("Recovered pending", vm.WeaponName); Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(before, File.ReadAllBytes(vm.PluginPath)); Assert.Empty(vm.RecoveryCandidates);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Equal("Recovered pending", Assert.Single((await _builder.GetWeaponsAsync(vm.PluginPath)).Value).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_ExternalPluginOrUnknownBaseline_RequiresExplicitReview(bool unknown)
    {
        var (vm, drafts, history, prompt) = await PrepareHistoryEditorAsync();
        await AbandonedAsync(vm, drafts, history, fingerprints: !unknown);
        if (!unknown)
        {
            Assert.True((await _builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = vm.PluginPath, GameDataPath = DataPath,
                TemplateFormId = 0x12EB7, EditorId = "ExternalSword", Name = "External" })).IsSuccess);
        }
        var before = File.ReadAllBytes(vm.PluginPath);
        await vm.RestoreDraftCommand.ExecuteAsync(null);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
        prompt.ConfirmAsync(Arg.Any<string>()).Returns(true);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains((await _builder.GetWeaponsAsync(vm.PluginPath)).Value, w => w.EditorId == "RecoveredSword");
        await prompt.Received(2).ConfirmAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Recovery_DiscardDoesNotWritePlugin_AndStaleCandidateIsRejected()
    {
        var (vm, drafts, history, _) = await PrepareHistoryEditorAsync();
        var draft = await AbandonedAsync(vm, drafts, history);
        Assert.True((await drafts.SaveAsync(draft, 1)).IsSuccess);
        var before = File.ReadAllBytes(vm.PluginPath);
        await vm.RestoreDraftCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.False(vm.HasUnsavedChanges);
        await vm.FindRecoveryCommand.ExecuteAsync(null); await vm.DiscardRecoveryCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Empty(vm.RecoveryCandidates); Assert.Equal(before, File.ReadAllBytes(vm.PluginPath));
    }

    [Fact]
    public async Task History_FieldKeywordAndRecipeMutations_UndoAndRedoWithoutWrites()
    {
        var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        var bytes = File.ReadAllBytes(vm.PluginPath); var old = vm.Damage;
        vm.Damage = "invalid"; Assert.True(vm.CanUndo);
        await vm.UndoCommand.ExecuteAsync(null); Assert.Equal(old, vm.Damage); Assert.True(vm.CanRedo);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Equal("invalid", vm.Damage);
        vm.WeaponKeywords.Add(vm.WorkbenchChoices.First());
        await vm.UndoCommand.ExecuteAsync(null); Assert.Empty(vm.WeaponKeywords);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Single(vm.WeaponKeywords);
        vm.RecipeOutputCount = "bad";
        await vm.UndoCommand.ExecuteAsync(null); Assert.Equal("1", vm.RecipeOutputCount);
        Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
    }

    [Fact]
    public async Task History_SaveAndImport_RoundTripFilesMetadataAndDrafts()
    {
        var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        var initial = File.ReadAllBytes(vm.PluginPath);
        vm.WeaponName = "Saved sword"; await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Single(vm.Weapons);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Equal(initial, File.ReadAllBytes(vm.PluginPath)); Assert.True(vm.HasUnsavedChanges);
        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Single(vm.Weapons);
        var source = Path.Combine(_root, "history.nif"); File.WriteAllText(source, "bytes");
        await vm.ImportModelCommand.ExecuteAsync(source);
        Assert.Null(vm.ErrorMessage); var model = vm.ModelPath;
        Assert.Contains(vm.ProjectResources, p => p.EndsWith("history.nif"));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.DoesNotContain(vm.ProjectResources, p => p.EndsWith("history.nif"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(vm.PluginPath)!, "meshes", model)));
        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(vm.PluginPath)!, "meshes", model)));
    }

    [Fact]
    public async Task Recovery_FingerprintFromAnotherWorkspace_RequiresReview()
    {
        var (vm, drafts, history, prompt) = await PrepareHistoryEditorAsync();
        var draft = await AbandonedAsync(vm, drafts, history);
        Assert.True((await drafts.SaveAsync(draft with { SourceRevision = draft.SourceRevision! with {
            Context = draft.SourceRevision.Context with { PluginPath = Path.Combine(_root, "other.esp") } } }, 1)).IsSuccess);
        await vm.FindRecoveryCommand.ExecuteAsync(null); await vm.RestoreDraftCommand.ExecuteAsync(null);
        var bytes = File.ReadAllBytes(vm.PluginPath);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
        await prompt.Received(1).ConfirmAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Recovery_MissingRecord_DoesNotSelectAnotherWeaponOrDiscardSource()
    {
        var (vm, drafts, history, _) = await PrepareHistoryEditorAsync();
        var draft = await AbandonedAsync(vm, drafts, history);
        Assert.True((await drafts.SaveAsync(draft with { WeaponFormId = 0xABCD }, 1)).IsSuccess);
        await vm.FindRecoveryCommand.ExecuteAsync(null); var name = vm.WeaponName;
        await vm.RestoreDraftCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(name, vm.WeaponName); Assert.Null(vm.SelectedWeapon);
        Assert.Single((await drafts.FindPendingAsync(vm.PluginPath, DataPath, Guid.NewGuid())).Value.Candidates);
    }

    [Fact]
    public async Task History_RecipeSaveAndDelete_UndoRedo_PreservesNewRecipeIdentity()
    {
        var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        await vm.NewRecipeCommand.ExecuteAsync(null);
        vm.RecipeEditorId = "UndoRecipe"; vm.SelectedWorkbench = vm.WorkbenchChoices.First();
        vm.RecipeIngredients.Add(new(vm.Options.Ingredients.First(), 2));
        await vm.SaveRecipeCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        var id = Assert.Single((await _builder.GetRecipesAsync(vm.PluginPath, vm.SelectedWeapon!.FormId)).Value).FormId;
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Null(vm.SelectedRecipe); Assert.True(vm.HasUnsavedRecipeChanges);
        Assert.Empty((await _builder.GetRecipesAsync(vm.PluginPath, vm.SelectedWeapon.FormId)).Value);
        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage); Assert.Equal(id, vm.SelectedRecipe?.FormId);
        await vm.DeleteRecipeCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        await vm.UndoCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage); Assert.Equal(id, vm.SelectedRecipe?.FormId);
    }

    [Fact]
    public async Task History_Reopen_ClearsSessionHistory()
    {
        var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        vm.Damage = "20"; Assert.True(vm.CanUndo);
        await vm.OpenProjectCommand.ExecuteAsync(vm.ProjectFilePath);
        Assert.Null(vm.ErrorMessage); Assert.False(vm.CanUndo); Assert.False(vm.CanRedo);
    }

    [Fact]
    public async Task History_ExternalFileChange_BlocksUndoAndNewEditsClearRedo()
    {
        var (vm, _, _, _) = await PrepareHistoryEditorAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        File.AppendAllText(vm.PluginPath, "external"); var bytes = File.ReadAllBytes(vm.PluginPath);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(bytes, File.ReadAllBytes(vm.PluginPath));
        vm.Damage = "33"; await vm.UndoCommand.ExecuteAsync(null); Assert.True(vm.CanRedo);
        vm.Value = "55"; Assert.False(vm.CanRedo);
    }
}
