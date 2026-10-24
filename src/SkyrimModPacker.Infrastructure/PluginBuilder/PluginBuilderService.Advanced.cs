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
            if (!File.Exists(Path.Combine(gameDataPath, "Skyrim.esm"))) throw new FileNotFoundException("Skyrim.esm is missing.");
            var sources = new List<ISkyrimModDisposableGetter>();
            try
            {
            foreach (var name in WeaponTypes.VanillaSources.Where(n => File.Exists(Path.Combine(gameDataPath, n)))) sources.Add(ReadMaster(gameDataPath, name));
            var usages = ListUsages(sources, cancellationToken);
            IReadOnlyList<RecordOption> Options(IEnumerable<IMajorRecordGetter> records) => records
                .Where(r => !r.IsDeleted && !string.IsNullOrWhiteSpace(r.EditorID))
                .Select(r =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var material = CraftingMaterialCatalog.Classify(r.EditorID!, r is IMiscItemGetter or IIngredientGetter);
                    var listUsages = r is ILeveledItemGetter && usages.TryGetValue(r.FormKey, out var found) ? found.Distinct().ToArray() : [];
                    var name = r switch { IMiscItemGetter misc => misc.Name?.String, IIngredientGetter ingredient => ingredient.Name?.String,
                        ILeveledItemGetter => string.Join("; ", listUsages.Select(u => u.OwnerName).Where(n => n.Length > 0).Distinct().Take(2)), _ => null };
                    return new RecordOption { Reference = Reference(r.FormKey)!, EditorId = r.EditorID!, Name = name ?? "",
                        MaterialCategory = material.Category, SearchAliases = material.Aliases, ListUsages = listUsages };
                }).OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase).ToList();
            return new WeaponOptions
            {
                Keywords = Options(sources.SelectMany(s => s.Keywords).DistinctBy(r => r.FormKey)), Sounds = Options(sources.SelectMany(s => s.SoundDescriptors).DistinctBy(r => r.FormKey)),
                Enchantments = Options(sources.SelectMany(s => s.ObjectEffects).Where(IsWeaponEnchantment).DistinctBy(r => r.FormKey)),
                Ingredients = Options(sources.SelectMany(s => s.EnumerateMajorRecords<IItemGetter>()).Where(r => r is not ILeveledItemGetter).DistinctBy(r => r.FormKey)),
                Perks = Options(sources.SelectMany(s => s.Perks).DistinctBy(r => r.FormKey)), LeveledLists = Options(sources.SelectMany(s => s.LeveledItems).DistinctBy(r => r.FormKey)),
                ExistingEditorIds = sources.SelectMany(s => s.EnumerateMajorRecords()).Where(r => !r.IsDeleted && !string.IsNullOrWhiteSpace(r.EditorID))
                    .Select(r => new RecordOption { Reference = Reference(r.FormKey)!, EditorId = r.EditorID! }).ToArray()
            };
            }
            finally { foreach (var source in sources) source.Dispose(); }
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
        RangeMin = sword.Data?.RangeMin ?? 0, RangeMax = sword.Data?.RangeMax ?? 0, Stagger = sword.Data?.Stagger ?? 0,
        ModelPath = sword.Model?.File.GivenPath ?? string.Empty,
        FirstPersonModel = Reference(sword.FirstPersonModel.FormKey),
        FirstPersonModelPath = source?.Statics.FirstOrDefault(s => s.FormKey == sword.FirstPersonModel.FormKey && !s.IsDeleted)?.Model?.File.GivenPath,
        Keywords = sword.Keywords?.Select(k => Reference(k.FormKey)).OfType<PluginRecordReference>().ToList() ?? [],
        AttackSound = Reference(sword.AttackSound.FormKey), Enchantment = Reference(sword.ObjectEffect.FormKey),
        Sounds = WeaponSounds(sword),
        EnchantmentCharge = sword.EnchantmentAmount ?? 0
    };

    private static void ApplyDetails(ISkyrimMod mod, IWeapon sword, WeaponDetails details, string gameDataPath)
    {
        var speed = details.SpeedSpecified ? details.Speed : sword.Data?.Speed ?? 1;
        var reach = details.ReachSpecified ? details.Reach : sword.Data?.Reach ?? 1;
        var description = details.DescriptionSpecified ? details.Description : sword.Description?.String ?? "";
        var enchantmentReference = details.EnchantmentSpecified ? details.Enchantment : Reference(sword.ObjectEffect.FormKey);
        var charge = details.EnchantmentChargeSpecified ? details.EnchantmentCharge : details.EnchantmentSpecified && enchantmentReference == null ? (ushort)0 : sword.EnchantmentAmount ?? 0;
        if (!float.IsFinite(speed) || speed <= 0 || !float.IsFinite(reach) || reach <= 0)
            throw new InvalidOperationException("Speed and reach must be finite positive numbers.");
        if (description.Contains('\0'))
            throw new InvalidOperationException("Description must not contain a null character.");
        var modelPath = (details.ModelPathSpecified ? details.ModelPath : sword.Model?.File.GivenPath ?? "").Trim().Replace('/', '\\');
        if (modelPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) modelPath = modelPath[7..];
        if (string.IsNullOrWhiteSpace(modelPath) || Path.IsPathRooted(modelPath) ||
            modelPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || modelPath.Contains(':') ||
            modelPath.Split('\\').Any(part => part is ".." or "." or "") ||
            !Path.GetExtension(modelPath).Equals(".nif", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a relative NIF path inside meshes, for example Weapons\\Iron\\LongSword.nif.");
        if (enchantmentReference != null && charge == 0)
            throw new InvalidOperationException("A weapon enchantment requires a positive charge.");
        if (enchantmentReference == null && charge != 0)
            throw new InvalidOperationException("Charge must be zero when no enchantment is assigned.");
        using var resolver = new RecordResolver(mod, gameDataPath);
        var keywordRecords = (details.KeywordsSpecified ? details.Keywords : ToDetails(sword).Keywords).Select(k => resolver.Resolve<IKeywordGetter>(k)).ToList();
        var keywords = keywordRecords.Select(k => k.FormKey).ToList();
        if (keywords.Distinct().Count() != keywords.Count) throw new InvalidOperationException("Duplicate keywords are not allowed.");
        var knownTypes = new Dictionary<string, WeaponKind>(StringComparer.OrdinalIgnoreCase) {
            ["WeapTypeSword"] = WeaponKind.OneHandSword, ["WeapTypeDagger"] = WeaponKind.Dagger, ["WeapTypeWarAxe"] = WeaponKind.WarAxe,
            ["WeapTypeMace"] = WeaponKind.Mace, ["WeapTypeGreatsword"] = WeaponKind.Greatsword,
            ["WeapTypeBattleaxe"] = WeaponKind.BattleaxeWarhammer, ["WeapTypeWarhammer"] = WeaponKind.BattleaxeWarhammer, ["WeapTypeBow"] = WeaponKind.Bow, ["WeapTypeCrossbow"] = WeaponKind.Crossbow };
        foreach (var keyword in keywordRecords)
            if (knownTypes.TryGetValue(keyword.EditorID ?? "", out var type) && type != WeaponTypes.Kind(sword) &&
                !(WeaponTypes.Kind(sword) == WeaponKind.Crossbow && type == WeaponKind.Bow))
                throw new InvalidOperationException("Weapon type keyword is incompatible with the inherited animation type: " + keyword.EditorID);
        var soundChanges = details.Sounds?.ToDictionary(p => p.Key, p => p.Value) ?? [];
        if (details.AttackSoundSpecified) soundChanges[nameof(IWeaponGetter.AttackSound)] = details.AttackSound;
        foreach (var change in soundChanges)
        {
            if (!WeaponSounds(sword).ContainsKey(change.Key)) throw new InvalidOperationException("Unsupported weapon sound slot: " + change.Key);
            if (change.Value != null) _ = resolver.Resolve<ISoundDescriptorGetter>(change.Value);
        }
        var enchantment = enchantmentReference == null ? null : resolver.Resolve<IObjectEffectGetter>(enchantmentReference);
        if (enchantment != null && !IsWeaponEnchantment(enchantment))
            throw new InvalidOperationException("Select a touch, fire-and-forget weapon enchantment, rather than an armor or staff enchantment.");
        if (enchantment != null)
        {
            var visited = new HashSet<FormKey>();
            var current = enchantment;
            while (true)
            {
                if (!visited.Add(current.FormKey)) throw new InvalidOperationException("Cyclic base enchantment references.");
                if (!IsWeaponEnchantment(current)) throw new InvalidOperationException("Incompatible base enchantment.");
                foreach (var effect in current.Effects)
                {
                    var magic = resolver.Resolve<IMagicEffectGetter>(Reference(effect.BaseEffect.FormKey) ?? throw new InvalidOperationException("Enchantment effect is NULL."));
                    if (magic.CastType != CastType.FireAndForget || magic.TargetType != TargetType.Touch) throw new InvalidOperationException("Enchantment contains an incompatible magic effect.");
                }
                if (current.WornRestrictions.FormKey != FormKey.Null)
                {
                    var restriction = resolver.Resolve<IFormListGetter>(Reference(current.WornRestrictions.FormKey)!);
                    if (!restriction.Items.Any(i => i.FormKey == sword.FormKey || keywords.Contains(i.FormKey)))
                        throw new InvalidOperationException("The weapon does not match the enchantment's restriction list.");
                }
                if (current.BaseEnchantment.FormKey == FormKey.Null) break;
                current = resolver.Resolve<IObjectEffectGetter>(Reference(current.BaseEnchantment.FormKey)!);
            }
        }
        sword.Description = description;
        sword.Data!.Speed = speed;
        sword.Data.Reach = reach;
        sword.Model ??= new Model();
        sword.Model.File = modelPath;
        ApplyFirstPersonModel(mod, sword, details, gameDataPath);
        sword.Keywords = [];
        foreach (var key in keywords) sword.Keywords.Add(new FormLink<IKeywordGetter>(key));
        foreach (var change in soundChanges) SetWeaponSound(sword, change.Key, change.Value);
        sword.ObjectEffect.SetTo(enchantment?.FormKey);
        sword.EnchantmentAmount = enchantment == null ? null : charge;
    }

    public Task<Result<IReadOnlyList<CraftingRecipe>>> GetRecipesAsync(string pluginPath, uint weaponFormId, CancellationToken cancellationToken = default)
    {
        return RunAsync<IReadOnlyList<CraftingRecipe>>(() =>
        {
            using var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Construct();
            var weaponKey = new FormKey(mod.ModKey, weaponFormId);
            return mod.ConstructibleObjects.Where(r => !r.IsDeleted && r.FormKey.ModKey == mod.ModKey && r.CreatedObject.FormKey == weaponKey)
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
                !r.IsDeleted && r.FormKey.ModKey == mod.ModKey && r.FormKey.ID == data.FormId && r.CreatedObject.FormKey == sword.FormKey) : null;
            if (data.FormId.HasValue && recipe == null) throw new InvalidOperationException("Recipe not found for the selected sword.");
            if (recipe != null) _ = ToRecipe(recipe); // Reject unsupported conditions rather than discarding them.
            if (mod.EnumerateMajorRecords().Any(r => r.FormKey != recipe?.FormKey && string.Equals(r.EditorID, data.EditorId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The recipe EditorID already exists.");
            using var resolver = new RecordResolver(mod, request.GameDataPath);
            var benchRecord = resolver.Resolve<IKeywordGetter>(data.Workbench);
            var bench = benchRecord.FormKey;
            var tempering = string.Equals(benchRecord.EditorID, "CraftingSmithingSharpeningWheel", StringComparison.OrdinalIgnoreCase);
            if (tempering && data.OutputCount != 1) throw new InvalidOperationException("A tempering recipe must improve exactly one weapon.");
            var ingredients = data.Ingredients.Select(i => {
                var item = resolver.Resolve<IItemGetter>(i.Reference);
                if (item is ILeveledItemGetter || item.FormKey == sword.FormKey) throw new InvalidOperationException("Recipe materials cannot be leveled lists or the output weapon itself.");
                return (Key: item.FormKey, i.Count);
            }).ToList();
            if (ingredients.Select(i => i.Key).Distinct().Count() != ingredients.Count)
                throw new InvalidOperationException("Combine duplicate ingredients into one row.");
            var perk = data.RequiredPerk == null ? FormKey.Null : resolver.Resolve<IPerkGetter>(data.RequiredPerk).FormKey;
            FormKey arcane = FormKey.Null;
            if (tempering)
            {
                using var vanilla = ReadMaster(request.GameDataPath, "Skyrim.esm");
                var arcaneRecord = vanilla.Perks.FirstOrDefault(p => !p.IsDeleted && p.EditorID == "ArcaneBlacksmith") ?? throw new InvalidOperationException("ArcaneBlacksmith perk is missing from Skyrim.esm.");
                arcane = resolver.Resolve<IPerkGetter>(Reference(arcaneRecord.FormKey)!).FormKey;
            }
            if (recipe != null && ToRecipe(recipe).TemperingPerk is { } existingArcane && resolver.Resolve<IPerkGetter>(existingArcane).EditorID != "ArcaneBlacksmith")
                throw new InvalidOperationException("The existing tempering condition has an unsupported perk.");
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
            if (tempering)
            {
                recipe.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.NotEqualTo, ComparisonValue = 1,
                    Flags = (Condition.Flag)1, Data = new EPTemperingItemIsEnchantedConditionData { RunOnType = Condition.RunOnType.Subject } });
                var guard = new HasPerkConditionData { RunOnType = Condition.RunOnType.Subject }; guard.Perk.Link.SetTo(arcane);
                recipe.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = guard });
            }
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
        var conditions = recipe.Conditions.ToArray();
        var tempering = conditions.Length >= 2 && conditions[0] is IConditionFloatGetter first && first.ComparisonValue == 1 &&
            first.CompareOperator == CompareOperator.NotEqualTo && (int)first.Flags == 1 && first.Data is IEPTemperingItemIsEnchantedConditionDataGetter ep &&
            ep.RunOnType == Condition.RunOnType.Subject && !ep.UseAliases && !ep.UsePackageData;
        var perkConditions = tempering ? conditions.Skip(1).ToArray() : conditions;
        if (perkConditions.Length > (tempering ? 2 : 1) || perkConditions.Any(c =>
            c is not IConditionFloatGetter value || value.ComparisonValue != 1 || c.CompareOperator != CompareOperator.EqualTo ||
            c.Flags != 0 || c.Data is not IHasPerkConditionDataGetter perk || perk.RunOnType != Condition.RunOnType.Subject ||
            perk.UseAliases || perk.UsePackageData || perk.Perk.Link.FormKey == FormKey.Null) ||
            recipe.Items?.Any(i => i.Data != null || i.Item == null || i.Item.Count <= 0 || i.Item.Item.FormKey == FormKey.Null) == true || recipe.CreatedObjectCount == 0)
            throw new InvalidOperationException("This recipe has advanced conditions or ingredient metadata that this editor cannot change.");
        return new CraftingRecipe
        {
            IsTempering = tempering,
            TemperingPerk = tempering ? Reference(((IHasPerkConditionDataGetter)perkConditions[0].Data).Perk.Link.FormKey) : null,
            FormId = recipe.FormKey.ID, WeaponFormId = recipe.CreatedObject.FormKey.ID, EditorId = recipe.EditorID ?? string.Empty,
            Workbench = Reference(recipe.WorkbenchKeyword.FormKey) ?? throw new InvalidOperationException("Recipe has no workbench keyword."),
            OutputCount = recipe.CreatedObjectCount ?? 1,
            Ingredients = recipe.Items?.Select(i => new RecipeIngredient
            {
                Reference = Reference(i.Item.Item.FormKey) ?? throw new InvalidOperationException("Recipe has an invalid ingredient."), Count = i.Item.Count
            }).ToList() ?? [],
            RequiredPerk = perkConditions.Skip(tempering ? 1 : 0).Select(c => c.Data).OfType<IHasPerkConditionDataGetter>()
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
                if (matching.Count > 0)
                {
                    if (!request.UpdateExisting || matching.Count > 1) throw new InvalidOperationException("The weapon is already in the list. Resolve existing entries before adding another.");
                    var existing = matching[0];
                    if (existing.Data!.Level == request.Level && existing.Data.Count == request.Count) return true;
                    existing.Data.Level = request.Level; existing.Data.Count = request.Count;
                    list.Entries.Remove(existing);
                    var position = list.Entries.TakeWhile(e => e.Data == null || e.Data.Level <= request.Level).Count(); list.Entries.Insert(position, existing);
                    WritePlugin(transaction, mod, request.PluginPath, request.GameDataPath, true, cancellationToken, mod.ModHeader.Stats.NextFormID); return true;
                }
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
        private readonly Dictionary<ModKey, Dictionary<FormKey, IMajorRecordGetter>> _overrides = [];

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
            var formKey = new FormKey(key, reference.FormId);
            var local = mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == formKey);
            if (local != null)
                return local is T typed && !local.IsDeleted ? typed : throw new InvalidOperationException($"{reference} is deleted or has an incompatible local override.");
            foreach (var dependency in mod.ModHeader.MasterReferences.AsEnumerable().Reverse())
            {
                if (!_sources.TryGetValue(dependency.Master, out var winnerSource))
                {
                    ValidateFileName(dependency.Master.FileName.String, true);
                    winnerSource = ReadMaster(dataPath, dependency.Master.FileName.String); _sources.Add(dependency.Master, winnerSource);
                }
                // Typed enumeration visits only the relevant groups (e.g. KYWD rather than every CELL/REFR).
                var typedWinner = winnerSource.EnumerateMajorRecords<T>().FirstOrDefault(r => r.FormKey == formKey);
                if (typedWinner != null) return !typedWinner.IsDeleted ? typedWinner : throw new InvalidOperationException($"{reference} is deleted in its winning master.");
                if (winnerSource.ModKey == formKey.ModKey)
                    throw new InvalidOperationException($"{reference} is missing or has an incompatible record type.");
                if (!_overrides.TryGetValue(winnerSource.ModKey, out var overrides))
                {
                    overrides = winnerSource.EnumerateMajorRecords().Where(r => r.FormKey.ModKey != winnerSource.ModKey).ToDictionary(r => r.FormKey);
                    _overrides.Add(winnerSource.ModKey, overrides);
                }
                if (overrides.ContainsKey(formKey)) throw new InvalidOperationException($"{reference} has an incompatible winning master override.");
            }
            return source.EnumerateMajorRecords<T>().FirstOrDefault(r => r.FormKey == formKey && !r.IsDeleted)
                ?? throw new InvalidOperationException($"{reference} is missing or is not a {typeof(T).Name} record.");
        }

        public void Dispose()
        {
            foreach (var source in _sources.Values) source.Dispose();
        }
    }
}
