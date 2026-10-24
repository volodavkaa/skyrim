using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    public bool HasWorkspace => !string.IsNullOrWhiteSpace(PluginPath);
    public string ProjectItemsSummary => Text("Items_ProjectCount", "Saved items in this project:") + " " + Weapons.Count +
        Environment.NewLine + string.Join(Environment.NewLine, Weapons.Select(w =>
            $"{w.Name} — {Text("WeaponType_" + w.Kind, w.Kind.ToString())} [{w.EditorId}, {w.FormId:X6}]" + Environment.NewLine +
            $"{Text("Weapons_Damage", "Damage")}: {w.Damage}; {Text("Weapons_Weight", "Weight")}: {w.Weight}; {Text("Weapons_Value", "Value")}: {w.Value}" + Environment.NewLine +
            $"{Text("Models_Third", "Third person")}: {w.Details.ModelPath}" + Environment.NewLine +
            $"{Text("Models_First", "First person")}: {w.Details.FirstPersonModelPath ?? w.Details.FirstPersonModel?.ToString() ?? "—"}; " +
            $"{Text("Weapons_Crafting", "Recipes")}: {(_weaponRecipeCounts.TryGetValue(w.FormId, out var count) ? count.ToString() : "?")}"));

    [RelayCommand]
    private async Task AddWeaponAsync()
    {
        if (IsEditorBusy || !await RequestLeaveEditorAsync()) return;
        if (!HasWorkspace) { ErrorMessage = Text("Weapons_SelectPlugin", "Create or open a project first."); return; }
        var template = SelectedTemplate ?? FilteredTemplates.FirstOrDefault();
        if (template != null)
        {
            SelectedTemplate = template;
            OnSelectedTemplateChanged(template);
        }
        else { SelectedWeapon = null; ResetDraft(); }
        _newWeaponPending = true; UpdateUnsavedChanges(); ResetEditorHistory();
        CurrentStep = 1;
        StatusMessage = Text("Items_AddHint", "Choose a type and vanilla template, set up the new item, then save it. Existing items stay in this project.");
    }

    [RelayCommand]
    private void EditWeapon()
    {
        if (IsEditorBusy || SelectedWeapon == null) return;
        CurrentStep = 3;
        StatusMessage = Text("Items_EditHint", "Editing the selected item. Use Save selected weapon to write your changes.");
    }

    [RelayCommand]
    private Task DuplicateWeaponAsync() => GuardEditorReplacementAsync(() => RunOperationAsync(async token =>
    {
        var source = RequireSelectedSword();
        var result = await _builder.DuplicateWeaponAsync(new DuplicateWeaponRequest(PluginPath, GameDataPath, source,
            string.IsNullOrWhiteSpace(ProjectFilePath) ? null : ProjectFilePath), token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        await RefreshWeaponsAsync(result.Value.FormId, token);
        CurrentStep = 3;
        StatusMessage = Text("Items_Duplicated", "Independent weapon and recipe records saved. Model and texture files are shared. Rename the selected copy as needed.");
    }, write: true));
}
