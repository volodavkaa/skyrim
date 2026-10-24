using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private string _optionsGameDataPath = string.Empty;
    [ObservableProperty] private WeaponOptions _options = new();
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _speed = "1";
    [ObservableProperty] private string _reach = "1";
    [ObservableProperty] private string _modelPath = string.Empty;
    [ObservableProperty] private ObservableCollection<RecordOption> _weaponKeywords = [];
    [ObservableProperty] private RecordOption? _keywordToAdd;
    [ObservableProperty] private RecordOption? _selectedKeyword;
    [ObservableProperty] private ObservableCollection<RecordOption> _soundChoices = [];
    [ObservableProperty] private ObservableCollection<RecordOption> _enchantmentChoices = [];
    [ObservableProperty] private RecordOption? _selectedSound;
    [ObservableProperty] private RecordOption? _selectedEnchantment;
    [ObservableProperty] private string _enchantmentCharge = "0";
    [ObservableProperty] private ObservableCollection<CraftingRecipe> _recipes = [];
    [ObservableProperty] private CraftingRecipe? _selectedRecipe;
    [ObservableProperty] private string _recipeEditorId = string.Empty;
    [ObservableProperty] private string _recipeOutputCount = "1";
    [ObservableProperty] private ObservableCollection<RecordOption> _workbenchChoices = [];
    [ObservableProperty] private ObservableCollection<RecordOption> _perkChoices = [];
    [ObservableProperty] private RecordOption? _selectedWorkbench;
    [ObservableProperty] private RecordOption? _selectedPerk;
    [ObservableProperty] private RecordOption? _ingredientToAdd;
    [ObservableProperty] private string _ingredientCount = "1";
    [ObservableProperty] private ObservableCollection<RecipeIngredientRow> _recipeIngredients = [];
    [ObservableProperty] private RecipeIngredientRow? _selectedIngredient;
    [ObservableProperty] private RecordOption? _selectedLeveledList;
    [ObservableProperty] private string _listLevel = "1";
    [ObservableProperty] private string _listCount = "1";

    [RelayCommand]
    private Task LoadOptionsAsync() => RunOperationAsync(token => LoadOptionsCoreAsync(token, force: true));

    private async Task LoadOptionsCoreAsync(CancellationToken token, bool force = false)
    {
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        if (!force && _optionsGameDataPath == GameDataPath) return;
        var result = await _builder.GetWeaponOptionsAsync(GameDataPath, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        var sound = SelectedSound?.Reference;
        var enchantment = SelectedEnchantment?.Reference;
        var workbench = SelectedWorkbench?.Reference;
        var perk = SelectedPerk?.Reference;
        Options = result.Value;
        FilterKeywords();
        OnPropertyChanged(nameof(VariantMaterials));
        _optionsGameDataPath = GameDataPath;
        SoundChoices = new ObservableCollection<RecordOption>(Options.Sounds);
        EnchantmentChoices = new ObservableCollection<RecordOption>(Options.Enchantments);
        WorkbenchChoices = new ObservableCollection<RecordOption>(Options.Keywords.Where(k => k.EditorId.StartsWith("Crafting", StringComparison.OrdinalIgnoreCase)));
        PerkChoices = new ObservableCollection<RecordOption>(Options.Perks);
        SelectedSound = SelectOption(SoundChoices, sound);
        SelectedEnchantment = SelectOption(EnchantmentChoices, enchantment);
        SelectedWorkbench = SelectOption(WorkbenchChoices, workbench) ?? WorkbenchChoices.FirstOrDefault(k => k.EditorId == "CraftingSmithingForge") ?? WorkbenchChoices.FirstOrDefault();
        SelectedPerk = SelectOption(PerkChoices, perk);
        WeaponKeywords = new ObservableCollection<RecordOption>(WeaponKeywords.Select(k => Option(Options.Keywords, k.Reference)));
    }

    private static RecordOption Option(IEnumerable<RecordOption> choices, PluginRecordReference reference) =>
        choices.FirstOrDefault(c => c.Reference == reference) ?? new RecordOption { Reference = reference };

    private static RecordOption? SelectOption(ObservableCollection<RecordOption> choices, PluginRecordReference? reference)
    {
        if (reference == null) return null;
        var option = Option(choices, reference);
        if (!choices.Contains(option)) choices.Add(option);
        return option;
    }

    private void FillDetails(WeaponDetails details)
    {
        _draftSounds = details.Sounds;
        Description = details.Description;
        Speed = details.Speed.ToString(CultureInfo.CurrentCulture);
        Reach = details.Reach.ToString(CultureInfo.CurrentCulture);
        ModelPath = details.ModelPath;
        ResetFirstPersonDraft(details);
        WeaponKeywords = new ObservableCollection<RecordOption>(details.Keywords.Select(k => Option(Options.Keywords, k)));
        SelectedSound = SelectOption(SoundChoices, details.AttackSound);
        SelectedEnchantment = SelectOption(EnchantmentChoices, details.Enchantment);
        EnchantmentCharge = details.EnchantmentCharge.ToString(CultureInfo.CurrentCulture);
    }

    private WeaponDetails ParseDetails()
    {
        static float Positive(string value)
        {
            if (!(float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) ||
                  float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) || !float.IsFinite(number) || number <= 0)
                throw new InvalidOperationException("Speed and reach must be finite positive numbers.");
            return number;
        }
        if (!ushort.TryParse(EnchantmentCharge, out var charge)) throw new InvalidOperationException("Enchantment charge must be 0–65535.");
        return new WeaponDetails
        {
            Description = Description, Speed = Positive(Speed), Reach = Positive(Reach), ModelPath = ModelPath,
            FirstPersonModel = _restoreFirstPerson ? _firstPersonReference : null,
            ClearFirstPersonModel = _restoreFirstPerson && _firstPersonReference == null,
            FirstPersonModelPath = _firstPersonEdited ? FirstPersonModelPath : null,
            Keywords = WeaponKeywords.Select(k => k.Reference).ToList(), AttackSound = SelectedSound?.Reference,
            Sounds = _draftSounds,
            Enchantment = SelectedEnchantment?.Reference, EnchantmentCharge = charge
        };
    }

    [RelayCommand]
    private void AddKeyword()
    {
        if (!IsLoading && KeywordToAdd != null && WeaponKeywords.All(k => k.Reference != KeywordToAdd.Reference)) WeaponKeywords.Add(KeywordToAdd);
    }

    [RelayCommand]
    private void RemoveKeyword() { if (!IsLoading && SelectedKeyword != null) WeaponKeywords.Remove(SelectedKeyword); }

    [RelayCommand]
    private void ClearSound() { if (!IsLoading) SelectedSound = null; }

    [RelayCommand]
    private void ClearEnchantment() { if (!IsLoading) { SelectedEnchantment = null; EnchantmentCharge = "0"; } }

    partial void OnSelectedEnchantmentChanged(RecordOption? value)
    {
        if (value != null && EnchantmentCharge == "0") EnchantmentCharge = "1000";
    }

    private void ResetRecipeEditor()
    {
        RecipeEditorId = "Recipe" + (SelectedWeapon?.EditorId ?? EditorId);
        RecipeOutputCount = "1";
        RecipeIngredients.Clear();
        SelectedIngredient = null;
        SelectedPerk = null;
    }

    partial void OnSelectedRecipeChanged(CraftingRecipe? value)
    {
        if (value == null) return;
        RecipeEditorId = value.EditorId;
        RecipeOutputCount = value.OutputCount.ToString(CultureInfo.CurrentCulture);
        SelectedWorkbench = SelectOption(WorkbenchChoices, value.Workbench);
        SelectedPerk = SelectOption(PerkChoices, value.RequiredPerk);
        RecipeIngredients = new ObservableCollection<RecipeIngredientRow>(value.Ingredients.Select(i => new RecipeIngredientRow(Option(Options.Ingredients, i.Reference), i.Count)));
        AcceptRecipeDraft();
        OnPropertyChanged(nameof(RecipeSelection));
    }

    [RelayCommand]
    private async Task NewRecipeAsync()
    {
        if (IsLoading) return;
        if (!await RequestLeaveEditorAsync(recipeOnly: true)) return;
        var before = CaptureMemento();
        _applyingEditorState = true;
        try
        {
            SelectedRecipe = null;
            ResetRecipeEditor();
            AcceptRecipeDraft();
            _newRecipePending = true; UpdateUnsavedChanges();
            OnPropertyChanged(nameof(RecipeSelection));
        }
        finally { _applyingEditorState = false; }
        var after = CaptureMemento();
        if (MemoryKey(before) != MemoryKey(after)) PushEditorHistory(new(before, after));
        _lastEditorMemento = after;
    }

    [RelayCommand]
    private void ClearPerk() { if (!IsLoading) SelectedPerk = null; }

    [RelayCommand]
    private void AddIngredient()
    {
        if (IsLoading) return;
        if (IngredientToAdd == null || !int.TryParse(IngredientCount, out var count) || count <= 0)
        {
            ErrorMessage = Text("Weapons_InvalidIngredient", "Select an ingredient and a positive count.");
            return;
        }
        if (RecipeIngredients.Any(i => i.Item.Reference == IngredientToAdd.Reference))
        {
            ErrorMessage = Text("Weapons_DuplicateIngredient", "Remove the existing ingredient row before changing its count.");
            return;
        }
        ErrorMessage = null;
        RecipeIngredients.Add(new RecipeIngredientRow(IngredientToAdd, count));
    }

    [RelayCommand]
    private void RemoveIngredient() { if (!IsLoading && SelectedIngredient != null) RecipeIngredients.Remove(SelectedIngredient); }

    private uint RequireSelectedSword()
    {
        RequirePlugin();
        return SelectedWeapon?.FormId ?? throw new InvalidOperationException(Text("Weapons_SelectRecord", "Select a sword in the plugin first."));
    }

    [RelayCommand]
    private Task LoadRecipesAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(token => RefreshRecipesCoreAsync(null, token)), recipeOnly: true);

    private async Task RefreshRecipesCoreAsync(uint? selectedId, CancellationToken token)
    {
        var result = await _builder.GetRecipesAsync(PluginPath, RequireSelectedSword(), token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        Recipes = new ObservableCollection<CraftingRecipe>(result.Value);
        if (SelectedWeapon != null) _weaponRecipeCounts[SelectedWeapon.FormId] = result.Value.Count;
        OnPropertyChanged(nameof(ProjectItemsSummary));
        SelectedRecipe = Recipes.FirstOrDefault(r => r.FormId == selectedId) ?? Recipes.FirstOrDefault();
        if (SelectedRecipe == null) ResetRecipeEditor();
        AcceptRecipeDraft();
        OnPropertyChanged(nameof(RecipeSelection));
    }

    [RelayCommand]
    private Task SaveRecipeAsync() => RunOperationAsync(async token =>
    {
        var id = RequireSelectedSword();
        if (!ValidateRecipeFields())
            throw new InvalidOperationException(Text("Weapons_InvalidRecipe", "Select a workbench and enter a positive output count."));
        var count = ushort.Parse(RecipeOutputCount);
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        var result = await _builder.SaveRecipeAsync(new SaveRecipeRequest
        {
            PluginPath = PluginPath, GameDataPath = GameDataPath,
            Recipe = new CraftingRecipe
            {
                FormId = SelectedRecipe?.FormId, WeaponFormId = id, EditorId = RecipeEditorId.Trim(),
                Workbench = SelectedWorkbench!.Reference, OutputCount = count, RequiredPerk = SelectedPerk?.Reference,
                Ingredients = RecipeIngredients.Select(i => new RecipeIngredient { Reference = i.Item.Reference, Count = i.Count }).ToList()
            }
        }, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshRecipesCoreAsync(result.Value.FormId, token);
        StatusMessage = Text("Weapons_RecipeSaved", "Recipe saved. A .bak copy was saved.");
    }, write: true);

    [RelayCommand]
    private Task DeleteRecipeAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
    {
        RequireSelectedSword();
        if (SelectedRecipe?.FormId is not { } id) throw new InvalidOperationException(Text("Weapons_SelectRecipe", "Select a saved recipe first."));
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        var result = await _builder.DeleteRecipeAsync(PluginPath, GameDataPath, id, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshRecipesCoreAsync(null, token);
        StatusMessage = Text("Weapons_RecipeDeleted", "Recipe deleted. A .bak copy was saved.");
    }, write: true), recipeOnly: true);

    [RelayCommand]
    private Task AddToListAsync() => ChangeListAsync(remove: false);

    [RelayCommand]
    private Task RemoveFromListAsync() => ChangeListAsync(remove: true);

    private Task ChangeListAsync(bool remove) => RunOperationAsync(async token =>
    {
        var id = RequireSelectedSword();
        if (SelectedLeveledList == null) throw new InvalidOperationException(Text("Weapons_SelectList", "Select a leveled list first."));
        short level = 1, count = 1;
        if (!remove && (!short.TryParse(ListLevel, out level) || level <= 0 || !short.TryParse(ListCount, out count) || count <= 0))
            throw new InvalidOperationException(Text("Weapons_InvalidList", "Level and count must be between 1 and 32767."));
        GameDataPath = (await _settings.LoadAsync(token)).SkyrimDataPath;
        var request = new LeveledListRequest { PluginPath = PluginPath, GameDataPath = GameDataPath, WeaponFormId = id, List = SelectedLeveledList.Reference, Level = level, Count = count };
        var result = remove ? await _builder.RemoveFromLeveledListAsync(request, token) : await _builder.AddToLeveledListAsync(request, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        StatusMessage = Text("Weapons_ListSaved", "Leveled list saved in your plugin. A .bak copy was saved.");
    }, write: true);
}

public sealed record RecipeIngredientRow(RecordOption Item, int Count)
{
    public string DisplayName => $"{Item.DisplayName} × {Count}";
}
