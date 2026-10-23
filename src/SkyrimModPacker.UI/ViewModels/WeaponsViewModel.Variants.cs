using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty] private SwordTemplate? _variantTemplate;
    [ObservableProperty] private string _variantLabel = "";
    [ObservableProperty] private string _variantMaterialNotes = "";
    [ObservableProperty] private bool _shareVariantModels;
    [ObservableProperty] private RecordOption? _variantMaterialKeyword;
    public IReadOnlyList<RecordOption> VariantMaterials => Options.Keywords.Where(k => k.EditorId.StartsWith("WeapMaterial", StringComparison.OrdinalIgnoreCase)).ToArray();
    [ObservableProperty] private ObservableCollection<WeaponVariant> _variants = [];
    [ObservableProperty] private string? _variantError;

    private async Task RefreshVariantsAsync(CancellationToken token)
    {
        Variants = []; VariantError = null;
        var result = await _projects.GetVariantsAsync(ProjectFilePath, GameDataPath, token);
        if (result == null) return;
        if (result.IsFailure) { VariantError = result.Error; return; }
        Variants = new(result.Value);
    }

    [RelayCommand]
    private Task CreateVariantAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
    {
        RequireProject();
        var parent = SelectedWeapon ?? throw new InvalidOperationException(Text("Weapons_SelectRecord", "Select a weapon."));
        var template = VariantTemplate ?? throw new InvalidOperationException(Text("Weapons_SelectTemplate", "Select a vanilla template."));
        var details = template.Details;
        var keywords = details.Keywords.ToList();
        if (VariantMaterialKeyword != null)
        {
            keywords.RemoveAll(k => Options.Keywords.Any(o => o.Reference == k && o.EditorId.StartsWith("WeapMaterial", StringComparison.OrdinalIgnoreCase)));
            keywords.Add(VariantMaterialKeyword.Reference);
        }
        var models = ShareVariantModels ? await _builder.GetWeaponModelsAsync(PluginPath, GameDataPath, parent.FormId, token) : null;
        if (models?.IsFailure == true || models?.Value.Error != null) throw new InvalidOperationException(models?.Error ?? models?.Value.Error);
        var result = await _builder.CloneSwordAsync(new CloneSwordRequest {
            PluginPath = PluginPath, ProjectFilePath = ProjectFilePath, GameDataPath = GameDataPath,
            TemplateFormId = template.FormId, TemplatePluginName = template.PluginName, EditorId = SuggestEditorId(parent.EditorId + "Variant"),
            Name = parent.Name + " — " + VariantLabel, VariantParentFormId = parent.FormId, VariantLabel = VariantLabel.Trim(), VariantMaterialNotes = VariantMaterialNotes.Trim(),
            Details = new WeaponDetails { Description = details.Description, ModelPath = models?.Value.ThirdPersonPath ?? details.ModelPath,
                FirstPersonModel = models == null ? details.FirstPersonModel : models.Value.FirstPersonReference, ClearFirstPersonModel = models != null && models.Value.FirstPersonReference == null,
                Speed = details.Speed, Reach = details.Reach,
                Keywords = keywords, AttackSound = details.AttackSound, Enchantment = details.Enchantment, EnchantmentCharge = details.EnchantmentCharge }
        }, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshWeaponsAsync(result.Value.FormId, token);
        StatusMessage = Text("Stage4_VariantCreated", "Variant created with independent statistics and recipe output identity.");
    }, write: true));
}
