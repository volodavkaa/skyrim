using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModTypeRow(WeaponKind kind, string name) : ObservableObject
{
    public WeaponKind Kind { get; } = kind;
    public string Name { get; } = name;
    [ObservableProperty] private string _count = "0";
}

public sealed partial class SmartModIngredientRow : ObservableObject
{
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private PluginRecordReference? _reference;
    [ObservableProperty] private string _count = "1";
    [ObservableProperty] private string _quantityError = "";
    public SmartModIngredientRow(SmartModIngredientDraft draft) { Reference = draft.Reference; Count = draft.Count; }
    public SmartModIngredientDraft Capture() => new(Reference, Count);
}

public sealed partial class SmartModRecipeRow : ObservableObject
{
    public Guid Id { get; }
    public uint? ConfirmedFormId { get; set; }
    public bool IsTempering { get; }
    public string DisplayName { get; }
    public PluginRecordReference? TemperingPerk { get; }
    [ObservableProperty] private string _editorId = "";
    [ObservableProperty] private string _outputCount = "1";
    [ObservableProperty] private PluginRecordReference? _workbench;
    [ObservableProperty] private PluginRecordReference? _requiredPerk;
    public ObservableCollection<SmartModIngredientRow> Ingredients { get; } = [];
    public SmartModRecipeRow(SmartModRecipeDraft draft, Func<string, string>? label = null)
    {
        Id = draft.Id; ConfirmedFormId = draft.ConfirmedFormId; IsTempering = draft.IsTempering;
        DisplayName = label?.Invoke(draft.IsTempering ? "Smart_Tempering" : "Smart_Crafting") ?? (draft.IsTempering ? "Tempering" : "Crafting");
        TemperingPerk = draft.TemperingPerk; EditorId = draft.EditorId; OutputCount = draft.OutputCount;
        Workbench = draft.Workbench; RequiredPerk = draft.RequiredPerk;
        foreach (var ingredient in draft.Ingredients) Ingredients.Add(new(ingredient));
    }
    public SmartModRecipeDraft Capture() => new()
    {
        Id = Id, ConfirmedFormId = ConfirmedFormId, IsTempering = IsTempering, TemperingPerk = TemperingPerk,
        EditorId = EditorId, OutputCount = OutputCount, Workbench = Workbench, RequiredPerk = RequiredPerk,
        Ingredients = Ingredients.Select(i => i.Capture()).ToArray()
    };
}

public sealed partial class SmartModItemRow : ObservableObject, IDataErrorInfo
{
    private SmartModItemDraft _draft;
    private readonly Func<string, string> _label;
    public Guid Id => _draft.Id;
    public WeaponKind Kind => _draft.Kind;
    public string KindName => _label("WeaponType_" + Kind);
    public string StateText => _label("Smart_State_" + _draft.State);
    public string FallbackIcon => Kind is WeaponKind.Bow or WeaponKind.Crossbow ? "➶" : "⚔";
    public string StepText => _label("Smart_Step_" + Step);
    public SmartModItemStep Step { get => _draft.Step; set { _draft = _draft with { Step = value }; OnPropertyChanged(); OnPropertyChanged(nameof(StepText)); NavigationChanged?.Invoke(); } }
    public uint? ConfirmedFormId => _draft.ConfirmedFormId;
    public PluginRecordReference? Template => _draft.Template;
    public WeaponDraftFields Fields => _draft.Weapon;
    public event Action? Changed;
    public event Action? NavigationChanged;
    public ObservableCollection<SmartModRecipeRow> Recipes { get; } = [];
    public Dictionary<string, string> Errors { get; } = [];
    public string Error => string.Join(Environment.NewLine, Errors.Values);
    public string this[string columnName] => Errors.GetValueOrDefault(columnName, "");
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private string _previewNotice = "";

    public SmartModItemRow(SmartModItemDraft draft, Func<string, string> label)
    {
        _draft = draft; _label = label;
        foreach (var recipe in draft.Recipes) Recipes.Add(new(recipe, label));
    }
    public SmartModItemDraft Capture() => _draft with { Recipes = Recipes.Select(r => r.Capture()).ToArray() };
    public void Replace(SmartModItemDraft draft)
    {
        _draft = draft;
        foreach (var name in new[] { nameof(Name), nameof(EditorId), nameof(Damage), nameof(Weight), nameof(Value), nameof(ModelPath), nameof(FirstPersonPath), nameof(UseSharedModel), nameof(Description), nameof(Speed), nameof(Reach), nameof(EnchantmentCharge), nameof(Sound), nameof(Enchantment), nameof(Fields), nameof(Template), nameof(StateText), nameof(ConfirmedFormId) })
            OnPropertyChanged(name);
        Changed?.Invoke();
    }
    private void Edit(WeaponDraftFields fields, string property)
    {
        _draft = _draft with { Weapon = fields, State = SmartModItemState.InProgress };
        Errors.Remove(property); OnPropertyChanged(property); OnPropertyChanged(nameof(Fields));
        OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(Errors)); Changed?.Invoke();
    }
    public string Name { get => Fields.Name; set => Edit(Fields with { Name = value }, nameof(Name)); }
    public string EditorId { get => Fields.EditorId; set => Edit(Fields with { EditorId = value }, nameof(EditorId)); }
    public string Damage { get => Fields.Damage; set => Edit(Fields with { Damage = value }, nameof(Damage)); }
    public string Weight { get => Fields.Weight; set => Edit(Fields with { Weight = value }, nameof(Weight)); }
    public string Value { get => Fields.Value; set => Edit(Fields with { Value = value }, nameof(Value)); }
    public string Description { get => Fields.Description; set => Edit(Fields with { Description = value }, nameof(Description)); }
    public string Speed { get => Fields.Speed; set => Edit(Fields with { Speed = value }, nameof(Speed)); }
    public string Reach { get => Fields.Reach; set => Edit(Fields with { Reach = value }, nameof(Reach)); }
    public string EnchantmentCharge { get => Fields.EnchantmentCharge; set => Edit(Fields with { EnchantmentCharge = value }, nameof(EnchantmentCharge)); }
    public PluginRecordReference? Sound { get => Fields.Sound; set => Edit(Fields with { Sound = value }, nameof(Sound)); }
    public PluginRecordReference? Enchantment { get => Fields.Enchantment; set => Edit(Fields with { Enchantment = value }, nameof(Enchantment)); }
    public bool UseSharedModel
    {
        get => Fields.UseSharedModel;
        set { Edit(Fields with { UseSharedModel = value, FirstPersonPath = value ? ModelPath : FirstPersonPath,
            FirstPersonEdited = value || Fields.FirstPersonEdited, RestoreFirstPerson = value ? false : Fields.RestoreFirstPerson }, nameof(UseSharedModel)); OnPropertyChanged(nameof(FirstPersonPath)); }
    }
    public string ModelPath { get => Fields.ModelPath; set { Edit(Fields with { ModelPath = value, FirstPersonPath = UseSharedModel ? value : FirstPersonPath }, nameof(ModelPath)); OnPropertyChanged(nameof(FirstPersonPath)); } }
    public string FirstPersonPath { get => Fields.FirstPersonPath; set { Edit(Fields with { FirstPersonPath = value, UseSharedModel = Fields.UseSharedModel && value.Equals(ModelPath, StringComparison.OrdinalIgnoreCase), FirstPersonEdited = true, RestoreFirstPerson = false }, nameof(FirstPersonPath)); OnPropertyChanged(nameof(UseSharedModel)); } }
    public void SetKeywords(IReadOnlyList<PluginRecordReference> values) => Edit(Fields with { Keywords = values }, nameof(Fields));
    public void SetState(SmartModItemState state) { _draft = _draft with { State = state }; OnPropertyChanged(nameof(StateText)); }
    public bool Validate(Func<string, string> message)
    {
        Errors.Clear();
        void Check(bool valid, string property, string key) { if (!valid) Errors[property] = message(key); }
        Check(!string.IsNullOrWhiteSpace(Name) && !Name.Contains('\0'), nameof(Name), "Smart_InvalidName");
        Check(EditorId.Length is > 0 and <= 128 && char.IsAsciiLetter(EditorId[0]) && EditorId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), nameof(EditorId), "Wizard_InvalidEditorId");
        Check(ushort.TryParse(Damage, out _), nameof(Damage), "Wizard_InvalidDamage");
        Check(Number(Weight, out var weight) && weight >= 0, nameof(Weight), "Wizard_InvalidWeight");
        Check(uint.TryParse(Value, out _), nameof(Value), "Wizard_InvalidValue");
        Check(Number(Speed, out var speed) && speed > 0, nameof(Speed), "Wizard_InvalidPositive");
        Check(Number(Reach, out var reach) && reach > 0, nameof(Reach), "Wizard_InvalidPositive");
        Check(ushort.TryParse(EnchantmentCharge, out var charge) && (Enchantment == null ? charge == 0 : charge > 0), nameof(EnchantmentCharge), "Wizard_InvalidCharge");
        Check(Template != null || ConfirmedFormId != null, nameof(Template), "Smart_SelectTemplate");
        Check(!string.IsNullOrWhiteSpace(ModelPath), nameof(ModelPath), "Wizard_InvalidModel");
        OnPropertyChanged(nameof(Errors)); OnPropertyChanged(nameof(Error));
        SetState(Errors.Count > 0 ? SmartModItemState.NeedsCorrection : _draft.State == SmartModItemState.Ready ? SmartModItemState.Ready : SmartModItemState.InProgress);
        return Errors.Count == 0;
    }
    public static bool Number(string value, out float number) =>
        (float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number) || float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && float.IsFinite(number);
}

public sealed record SmartModIssue(string Message, Guid? ItemId = null, Guid? RecipeId = null, string? ResourcePath = null, bool IsError = true, SmartModItemStep? Step = null);
public sealed record SmartModRecipeLink(SmartModItemRow Item, SmartModRecipeRow Recipe, string DisplayName);
