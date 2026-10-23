using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    public Task<Result<WeaponOptions>> GetWeaponOptionsAsync(string gameDataPath, CancellationToken cancellationToken = default)
    {
        return RunAsync(() =>
        {
            ValidateDataPath(gameDataPath);
            using var source = ReadMaster(gameDataPath, "Skyrim.esm");
            IReadOnlyList<RecordOption> Options(IEnumerable<IMajorRecordGetter> records) => records
                .Where(r => !r.IsDeleted && !string.IsNullOrWhiteSpace(r.EditorID))
                .Select(r =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new RecordOption { Reference = Reference(r.FormKey)!, EditorId = r.EditorID! };
                }).OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase).ToList();
            return new WeaponOptions
            {
                Keywords = Options(source.Keywords), Sounds = Options(source.SoundDescriptors),
                Enchantments = Options(source.ObjectEffects.Where(IsWeaponEnchantment)),
                Ingredients = Options(source.EnumerateMajorRecords<IItemGetter>().Where(r => r is not ILeveledItemGetter)),
                Perks = Options(source.Perks), LeveledLists = Options(source.LeveledItems)
            };
        }, cancellationToken);
    }

    private static bool IsWeaponEnchantment(IObjectEffectGetter enchantment) =>
        enchantment.EnchantType == ObjectEffect.EnchantTypeEnum.Enchantment &&
        enchantment.CastType == CastType.FireAndForget && enchantment.TargetType == TargetType.Touch;

    private static PluginRecordReference? Reference(FormKey key) => key == FormKey.Null
        ? null : new PluginRecordReference(key.ModKey.FileName.String, key.ID);

    private static WeaponDetails ToDetails(IWeaponGetter sword, ISkyrimModGetter? source = null) => new()
    {
        Description = sword.Description?.String ?? string.Empty,
        Speed = sword.Data?.Speed ?? 1, Reach = sword.Data?.Reach ?? 1,
        ModelPath = sword.Model?.File.GivenPath ?? string.Empty,
        FirstPersonModel = Reference(sword.FirstPersonModel.FormKey),
        FirstPersonModelPath = source?.Statics.FirstOrDefault(s => s.FormKey == sword.FirstPersonModel.FormKey && !s.IsDeleted)?.Model?.File.GivenPath,
        Keywords = sword.Keywords?.Select(k => Reference(k.FormKey)).OfType<PluginRecordReference>().ToList() ?? [],
        AttackSound = Reference(sword.AttackSound.FormKey), Enchantment = Reference(sword.ObjectEffect.FormKey),
        EnchantmentCharge = sword.EnchantmentAmount ?? 0
    };

    private static void ApplyDetails(ISkyrimMod mod, IWeapon sword, WeaponDetails details, string gameDataPath)
    {
        if (!float.IsFinite(details.Speed) || details.Speed <= 0 || !float.IsFinite(details.Reach) || details.Reach <= 0)
            throw new InvalidOperationException("Speed and reach must be finite positive numbers.");
        if (details.Description.Contains('\0'))
            throw new InvalidOperationException("Description must not contain a null character.");
        var modelPath = details.ModelPath.Trim().Replace('/', '\\');
        if (modelPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) modelPath = modelPath[7..];
        if (string.IsNullOrWhiteSpace(modelPath) || Path.IsPathRooted(modelPath) ||
            modelPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || modelPath.Contains(':') ||
            modelPath.Split('\\').Any(part => part is ".." or "." or "") ||
            !Path.GetExtension(modelPath).Equals(".nif", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a relative NIF path inside meshes, for example Weapons\\Iron\\LongSword.nif.");
        if (details.Enchantment != null && details.EnchantmentCharge == 0)
            throw new InvalidOperationException("A weapon enchantment requires a positive charge.");
        using var resolver = new RecordResolver(mod, gameDataPath);
        var keywords = details.Keywords.Select(k => resolver.Resolve<IKeywordGetter>(k).FormKey).Distinct().ToList();
        var sound = details.AttackSound == null ? FormKey.Null : resolver.Resolve<ISoundDescriptorGetter>(details.AttackSound).FormKey;
        var enchantment = details.Enchantment == null ? null : resolver.Resolve<IObjectEffectGetter>(details.Enchantment);
        if (enchantment != null && !IsWeaponEnchantment(enchantment))
            throw new InvalidOperationException("Select a touch, fire-and-forget weapon enchantment, rather than an armor or staff enchantment.");
        sword.Description = details.Description;
        sword.Data!.Speed = details.Speed;
        sword.Data.Reach = details.Reach;
        sword.Model ??= new Model();
        sword.Model.File = modelPath;
        ApplyFirstPersonModel(mod, sword, details, gameDataPath);
        sword.Keywords = [];
        foreach (var key in keywords) sword.Keywords.Add(new FormLink<IKeywordGetter>(key));
        sword.AttackSound.SetTo(details.AttackSound == null ? (FormKey?)null : sound);
        sword.ObjectEffect.SetTo(enchantment?.FormKey);
        sword.EnchantmentAmount = enchantment == null ? null : details.EnchantmentCharge;
    }

    public Task<Result<IReadOnlyList<CraftingRecipe>>> GetRecipesAsync(string pluginPath, uint weaponFormId, CancellationToken cancellationToken = default)
    {
        return RunAsync<IReadOnlyList<CraftingRecipe>>(() =>
        {
            using var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Construct();
            var weaponKey = new FormKey(mod.ModKey, weaponFormId);
            return mod.ConstructibleObjects.Where(r => r.FormKey.ModKey == mod.ModKey && r.CreatedObject.FormKey == weaponKey)
                .Select(r => { cancellationToken.ThrowIfCancellationRequested(); return ToRecipe(r); }).ToList();
        }, cancellationToken);
    }

    public Task<Result<CraftingRecipe>> SaveRecipeAsync(SaveRecipeRequest request, CancellationToken cancellationToken = default)
    {
        return RunWriteAsync(transaction =>
        {
            var mod = ReadWorkspaceForEditing(request.PluginPath, request.GameDataPath);
            var data = request.Recipe;
            var sword = FindOwnedSword(mod, data.WeaponFormId);
            if (string.IsNullOrWhiteSpace(data.EditorId) || data.EditorId.Length > 128 || !char.IsAsciiLetter(data.EditorId[0]) ||
                data.EditorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new InvalidOperationException("Recipe EditorID must start with an ASCII letter and contain letters, digits or underscores.");
            if (data.OutputCount == 0 || data.Ingredients.Count == 0 || data.Ingredients.Any(i => i.Count <= 0))
                throw new InvalidOperationException("A recipe needs a positive output count and at least one ingredient with a positive count.");
            if (data.Ingredients.Select(i => i.Reference).Distinct().Count() != data.Ingredients.Count)
                throw new InvalidOperationException("Combine duplicate ingredients into one row.");
            var recipe = data.FormId.HasValue ? mod.ConstructibleObjects.FirstOrDefault(r =>
                r.FormKey.ModKey == mod.ModKey && r.FormKey.ID == data.FormId && r.CreatedObject.FormKey == sword.FormKey) : null;
            if (data.FormId.HasValue && recipe == null) throw new InvalidOperationException("Recipe not found for the selected sword.");
            if (recipe != null) _ = ToRecipe(recipe); // Reject unsupported conditions rather than discarding them.
            if (mod.EnumerateMajorRecords().Any(r => r.FormKey != recipe?.FormKey && string.Equals(r.EditorID, data.EditorId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The recipe EditorID already exists.");
            using var resolver = new RecordResolver(mod, request.GameDataPath);
            var bench = resolver.Resolve<IKeywordGetter>(data.Workbench).FormKey;
            var ingredients = data.Ingredients.Select(i => (Key: resolver.Resolve<IItemGetter>(i.Reference).FormKey, i.Count)).ToList();
            if (ingredients.Select(i => i.Key).Distinct().Count() != ingredients.Count)
                throw new InvalidOperationException("Combine duplicate ingredients into one row.");
            var perk = data.RequiredPerk == null ? FormKey.Null : resolver.Resolve<IPerkGetter>(data.RequiredPerk).FormKey;
            if (recipe == null)
            {
                var id = AllocateCompactId(mod);
                recipe = new ConstructibleObject(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE);
                mod.ConstructibleObjects.Add(recipe);
            }
            recipe.EditorID = data.EditorId;
            recipe.CreatedObject.SetTo(sword.FormKey);
            recipe.WorkbenchKeyword.SetTo(bench);
            recipe.CreatedObjectCount = data.OutputCount;
            recipe.Items = [];
            foreach (var ingredient in ingredients)
            {
                var entry = new ContainerEntry { Item = new ContainerItem { Count = ingredient.Count } };
                entry.Item.Item.SetTo(ingredient.Key);
                recipe.Items.Add(entry);
            }
            recipe.Conditions.Clear();
            if (perk != FormKey.Null)
            {
                var conditionData = new HasPerkConditionData { RunOnType = Condition.RunOnType.Subject };
                conditionData.Perk.Link.SetTo(perk);
                recipe.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = conditionData });
            }
            WritePlugin(transaction, mod, request.PluginPath, request.GameDataPath, true, cancellationToken, mod.ModHeader.Stats.NextFormID);
            return ToRecipe(recipe);
        }, () => request.PluginPath, request.GameDataPath, cancellationToken);
    }

    public async Task<Result> DeleteRecipeAsync(string pluginPath, string gameDataPath, uint recipeFormId, CancellationToken cancellationToken = default)
    {
        return await RunWriteAsync(transaction =>
        {
            var mod = ReadWorkspaceForEditing(pluginPath, gameDataPath);
            var key = new FormKey(mod.ModKey, recipeFormId);
            var recipe = mod.ConstructibleObjects.FirstOrDefault(r => r.FormKey == key);
            if (recipe == null) throw new InvalidOperationException("Owned recipe not found.");
            if (mod.EnumerateMajorRecords().Any(r => r.FormKey != key && r.EnumerateFormLinks().Any(link => link.FormKey == key)))
                throw new InvalidOperationException("The recipe is referenced by another record.");
            mod.ConstructibleObjects.Remove(key);
            WritePlugin(transaction, mod, pluginPath, gameDataPath, true, cancellationToken, mod.ModHeader.Stats.NextFormID);
            return true;
        }, () => pluginPath, gameDataPath, cancellationToken);
    }

    private static CraftingRecipe ToRecipe(IConstructibleObjectGetter recipe)
    {
        if (recipe.Conditions.Count > 1 || recipe.Conditions.Any(c =>
            c is not IConditionFloatGetter value || value.ComparisonValue != 1 || c.CompareOperator != CompareOperator.EqualTo ||
            c.Flags != 0 || c.Data is not IHasPerkConditionDataGetter perk || perk.RunOnType != Condition.RunOnType.Subject ||
            perk.UseAliases || perk.UsePackageData || perk.Perk.Link.FormKey == FormKey.Null) ||
            recipe.Items?.Any(i => i.Data != null || i.Item == null) == true)
            throw new InvalidOperationException("This recipe has advanced conditions or ingredient metadata that this editor cannot change.");
        return new CraftingRecipe
        {
            FormId = recipe.FormKey.ID, WeaponFormId = recipe.CreatedObject.FormKey.ID, EditorId = recipe.EditorID ?? string.Empty,
            Workbench = Reference(recipe.WorkbenchKeyword.FormKey) ?? throw new InvalidOperationException("Recipe has no workbench keyword."),
            OutputCount = recipe.CreatedObjectCount ?? 1,
            Ingredients = recipe.Items?.Select(i => new RecipeIngredient
            {
                Reference = Reference(i.Item.Item.FormKey) ?? throw new InvalidOperationException("Recipe has an invalid ingredient."), Count = i.Item.Count
            }).ToList() ?? [],
            RequiredPerk = recipe.Conditions.Select(c => c.Data).OfType<IHasPerkConditionDataGetter>()
                .Select(c => Reference(c.Perk.Link.FormKey)).SingleOrDefault()
        };
    }

    public async Task<Result> AddToLeveledListAsync(LeveledListRequest request, CancellationToken cancellationToken = default) =>
        await ChangeLeveledListAsync(request, remove: false, cancellationToken);

    public async Task<Result> RemoveFromLeveledListAsync(LeveledListRequest request, CancellationToken cancellationToken = default) =>
        await ChangeLeveledListAsync(request, remove: true, cancellationToken);

    private Task<Result<bool>> ChangeLeveledListAsync(LeveledListRequest request, bool remove, CancellationToken cancellationToken)
    {
        return RunWriteAsync(transaction =>
        {
            var mod = ReadWorkspaceForEditing(request.PluginPath, request.GameDataPath);
            var sword = FindOwnedSword(mod, request.WeaponFormId);
            if (!remove && (request.Level < 1 || request.Count < 1))
                throw new InvalidOperationException("Leveled list level and count must be positive.");
            using var resolver = new RecordResolver(mod, request.GameDataPath);
            var source = resolver.Resolve<ILeveledItemGetter>(request.List);
            var list = mod.LeveledItems.FirstOrDefault(l => l.FormKey == source.FormKey) ?? mod.LeveledItems.GetOrAddAsOverride(source);
            list.Entries ??= [];
            var matching = list.Entries.Where(e => e.Data?.Reference.FormKey == sword.FormKey).ToList();
            if (remove)
            {
                if (matching.Count == 0) throw new InvalidOperationException("The sword is not in the selected list.");
                foreach (var entry in matching) list.Entries.Remove(entry);
            }
            else
            {
                if (matching.Count > 0) throw new InvalidOperationException("The sword is already in the list. Remove it first to change its level or count.");
                if (list.Entries.Count >= 255) throw new InvalidOperationException("The leveled list already contains the maximum 255 entries.");
                var entry = new LeveledItemEntry { Data = new LeveledItemEntryData { Level = request.Level, Count = request.Count } };
                entry.Data.Reference.SetTo(sword.FormKey);
                var index = list.Entries.TakeWhile(e => e.Data == null || e.Data.Level <= request.Level).Count();
                list.Entries.Insert(index, entry);
            }
            WritePlugin(transaction, mod, request.PluginPath, request.GameDataPath, true, cancellationToken, mod.ModHeader.Stats.NextFormID);
            return true;
        }, () => request.PluginPath, request.GameDataPath, cancellationToken);
    }

    private static uint AllocateCompactId(ISkyrimMod mod)
    {
        var ids = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey).Select(r => r.FormKey.ID).ToList();
        var minimum = mod.ModHeader.Stats.Version >= 1.71f ? 1u : 0x800u;
        if (ids.Any(id => id < minimum || id > 0xFFF)) throw new InvalidOperationException("The plugin contains non-compact FormIDs. Compaction is not performed.");
        var next = Math.Max(0x800u, Math.Max(mod.ModHeader.Stats.NextFormID, ids.DefaultIfEmpty(0x7FFu).Max() + 1));
        if (next > 0xFFF) throw new InvalidOperationException("The compact FormID range is exhausted.");
        mod.ModHeader.Stats.NextFormID = next + 1;
        return next;
    }

    /// <summary>Keeps reference sources open for a single operation and validates their record types.</summary>
    private sealed class RecordResolver(ISkyrimMod mod, string dataPath) : IDisposable
    {
        private readonly Dictionary<ModKey, ISkyrimModDisposableGetter> _sources = [];

        public T Resolve<T>(PluginRecordReference reference) where T : class, IMajorRecordGetter
        {
            ValidateFileName(reference.PluginName, allowMaster: true);
            if (reference.FormId == 0 || reference.FormId > 0xFFFFFF) throw new InvalidOperationException("Invalid plugin-local FormID.");
            var key = ModKey.FromNameAndExtension(reference.PluginName);
            ISkyrimModGetter source = mod;
            if (key != mod.ModKey)
            {
                if (!_sources.TryGetValue(key, out var loaded))
                {
                    loaded = ReadMaster(dataPath, reference.PluginName);
                    _sources.Add(key, loaded);
                }
                source = loaded;
                if (mod.ModHeader.MasterReferences.All(m => m.Master != key))
                {
                    if (loaded.ModHeader.MasterReferences.Any(parent => mod.ModHeader.MasterReferences.All(m => m.Master != parent.Master)))
                        throw new InvalidOperationException($"Add the parent masters of {reference.PluginName} to the plugin first.");
                    mod.ModHeader.MasterReferences.Add(new MasterReference { Master = key });
                }
            }
            return source.EnumerateMajorRecords<T>().FirstOrDefault(r => r.FormKey == new FormKey(key, reference.FormId) && !r.IsDeleted)
                ?? throw new InvalidOperationException($"{reference} is missing or is not a {typeof(T).Name} record.");
        }

        public void Dispose()
        {
            foreach (var source in _sources.Values) source.Dispose();
        }
    }
}
