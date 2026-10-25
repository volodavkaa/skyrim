using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    public Task<Result<IReadOnlyList<WeaponComparisonRecipe>>> GetComparisonRecipesAsync(string pluginPath, uint weaponFormId, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<WeaponComparisonRecipe>>(() =>
        {
            using var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Construct();
            var key = new FormKey(mod.ModKey, weaponFormId); var result = new List<WeaponComparisonRecipe>();
            foreach (var record in mod.ConstructibleObjects.Where(r => !r.IsDeleted && r.CreatedObject.FormKey == key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var recipe = ToRecipe(record);
                    result.Add(new(recipe.EditorId, new() { EditorId = recipe.EditorId, IsTempering = recipe.IsTempering,
                        Workbench = recipe.Workbench, RequiredPerk = recipe.RequiredPerk, TemperingPerk = recipe.TemperingPerk,
                        OutputCount = recipe.OutputCount.ToString(), Ingredients = recipe.Ingredients.Select(i => new SmartModIngredientDraft(i.Reference, i.Count.ToString())).ToArray() }));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Preserve visible ingredients and raw condition identities, but never offer lossy copying.
                    var conditions = string.Join(", ", record.Conditions.Select(c => c.Data.GetType().Name + " / " + c.CompareOperator + " / flags=" + c.Flags));
                    result.Add(new(record.EditorID ?? record.FormKey.ToString(), new() { EditorId = record.EditorID ?? "", Workbench = Reference(record.WorkbenchKeyword.FormKey),
                        OutputCount = record.CreatedObjectCount?.ToString() ?? "", Ingredients = record.Items?.Where(i => i.Item != null).Select(i => new SmartModIngredientDraft(Reference(i.Item.Item.FormKey), i.Item.Count.ToString())).ToArray() ?? [] },
                        ex.Message + (conditions.Length == 0 ? "" : Environment.NewLine + conditions)));
                }
            }
            return result;
        }, cancellationToken);
}
