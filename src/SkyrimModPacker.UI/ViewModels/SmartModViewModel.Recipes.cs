using System.IO;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    public void AddOrIncrementIngredient(RecordOption selection, SmartModIngredientRow? editing = null)
    {
        if (IsBusy || SelectedRecipe == null) return;
        var existing = SelectedRecipe.Ingredients.FirstOrDefault(i => i != editing && i.Reference == selection.Reference);
        if (existing != null)
        {
            if (!int.TryParse(existing.Count, out var count) || count <= 0 || count == int.MaxValue) { Error = Label("Smart_IngredientQuantityError"); return; }
            existing.Count = (count + 1).ToString(); return;
        }
        if (editing != null) editing.Reference = selection.Reference;
        else SelectedRecipe.Ingredients.Add(new(new(selection.Reference, "1")));
    }
    private WeaponOptions? _ingredientIndexSource;
    private MaterialSearchIndex? _ingredientIndex;
    public MaterialSearchIndex IngredientIndex
    {
        get { if (!ReferenceEquals(_ingredientIndexSource, Options)) { _ingredientIndex = new(Options.Ingredients); _ingredientIndexSource = Options; } return _ingredientIndex!; }
    }
    public bool HasSelectedRecipe => SelectedRecipe != null;
    public bool HasCraftingRecipe => SelectedItem?.Recipes.Any(r => !r.IsTempering) == true;
    public bool HasTemperingRecipe => SelectedItem?.Recipes.Any(r => r.IsTempering) == true;
    public bool HasRecipeConflict => SelectedItem?.Recipes.GroupBy(r => r.IsTempering).Any(g => g.Count() > 1) == true;
    public string CraftingAction => Label(HasCraftingRecipe ? "Smart_ConfigureRecipe" : "Smart_EnableRecipe");
    public string TemperingAction => Label(HasTemperingRecipe ? "Smart_ConfigureRecipe" : "Smart_EnableRecipe");
    public string RecipeWorkbenchText => SelectedRecipe == null ? "" : Options.Keywords.FirstOrDefault(k => k.Reference == SelectedRecipe.Workbench) is { } bench &&
        bench.EditorId is not ("CraftingSmithingForge" or "CraftingSmithingSharpeningWheel") ? bench.DisplayName :
        Label(SelectedRecipe.IsTempering ? "Smart_TemperingBench" : "Smart_CraftingBench");
    public string RecipeConflictDetails => SelectedItem == null ? "" : string.Join(Environment.NewLine,
        SelectedItem.Recipes.Select(r => r.DisplayName + " [" + r.EditorId + "] — " + r.OutputCount + "; " +
            string.Join(", ", r.Ingredients.Select(i => (Options.Ingredients.FirstOrDefault(o => o.Reference == i.Reference)?.DisplayName ?? i.Reference?.ToString() ?? "?") + " × " + i.Count))));

    // The caller presents the complete removal list and obtains an explicit UI decision first.
    public Task ResolveRecipeConflictAsync() => RunAsync(async token =>
    {
        if (SelectedItem is not { } owner || SelectedRecipe is not { } keep) return;
        var removed = owner.Recipes.Where(r => r.IsTempering == keep.IsTempering && r.Id != keep.Id).ToArray();
        if (removed.Length == 0) return;
        await PersistAsync(token);
        var before = Capture();
        var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
        if (_history == null) throw new IOException(Label("Smart_ConflictNeedsBackup"));
        var checkpoint = _history == null ? null : await _history.CaptureAsync(Context, true, token: token);
        try
        {
            foreach (var recipe in removed)
            {
                if (recipe.ConfirmedFormId is { } form)
                {
                    var result = await _builder.DeleteRecipeAsync(PluginPath, _gameData, form, token);
                    if (result.IsFailure) throw new IOException(result.Error);
                    EnsureExpectedWorkspaceUnchanged();
                }
                owner.Recipes.Remove(recipe);
            }
            await RefreshBaselineAsync(token); await PersistAsync(token);
        }
        catch (Exception failure)
        {
            try
            {
                if (checkpoint != null && expectation?.HasWrites == true)
                {
                    EnsureExpectedWorkspaceUnchanged();
                    var current = await _history!.InspectAsync(Context, true, CancellationToken.None);
                    var expected = await _history.CaptureAsync(Context, true, token: CancellationToken.None);
                    if (!current.Matches(expected.Revision)) throw new IOException(Label("Smart_ExternalChanged"));
                    await _history.RestoreAsync(checkpoint, expected, token: CancellationToken.None);
                }
                ApplyDocument(before with { Revision = _document.Revision });
                await RefreshBaselineAsync(CancellationToken.None); await PersistAsync(CancellationToken.None);
            }
            catch (Exception rollback) { _writeBlocked = true; throw new IOException(failure.Message + Environment.NewLine + rollback.Message, failure); }
            throw;
        }
    });
}
