using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Core.Interfaces;

/// <summary>Creates plugins in a workspace outside the game directory.</summary>
public interface IPluginBuilderService
{
    Task<Result<WeaponModels>> GetWeaponModelsAsync(string pluginPath, string gameDataPath, uint formId, CancellationToken cancellationToken = default);
    Task<Result<string>> CreateAsync(CreatePluginRequest request, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SwordTemplate>>> GetSwordTemplatesAsync(string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<WeaponRecord>> CloneSwordAsync(CloneSwordRequest request, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<WeaponRecord>>> GetWeaponsAsync(string pluginPath, CancellationToken cancellationToken = default);
    Task<Result<WeaponRecord>> UpdateSwordAsync(UpdateSwordRequest request, CancellationToken cancellationToken = default);
    Task<Result> DeleteSwordAsync(string pluginPath, string gameDataPath, uint formId, CancellationToken cancellationToken = default);
    Task<Result<WeaponOptions>> GetWeaponOptionsAsync(string gameDataPath, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<CraftingRecipe>>> GetRecipesAsync(string pluginPath, uint weaponFormId, CancellationToken cancellationToken = default);
    Task<Result<CraftingRecipe>> SaveRecipeAsync(SaveRecipeRequest request, CancellationToken cancellationToken = default);
    Task<Result> DeleteRecipeAsync(string pluginPath, string gameDataPath, uint recipeFormId, CancellationToken cancellationToken = default);
    Task<Result> AddToLeveledListAsync(LeveledListRequest request, CancellationToken cancellationToken = default);
    Task<Result> RemoveFromLeveledListAsync(LeveledListRequest request, CancellationToken cancellationToken = default);
}
