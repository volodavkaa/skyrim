using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed record SmartModStepLink(SmartModItemStep Step, string Name, bool IsCurrent);

public sealed partial class SmartModViewModel
{
    public bool CanCreateProject => !IsBusy && !HasProject && Types.Count > 0 && Types.All(t => int.TryParse(t.Count, out var count) && count >= 0 && count <= 100) && Types.Sum(t => int.Parse(t.Count)) is > 0 and <= 100;
    public string SetupCountText => string.Format(Label("Smart_SetupCountFormat"), Types.Sum(t => int.TryParse(t.Count, out var count) ? Math.Clamp(count, 0, 100) : 0));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCreateProject));
    public IEnumerable<SmartModStepLink> StepLinks => Enum.GetValues<SmartModItemStep>().Select(step =>
        new SmartModStepLink(step, Label("Smart_Step_" + step), SelectedItem?.Step == step));
    public string StepHelp => SelectedItem == null ? "" : Label("Smart_Help_" + SelectedItem.Step);
    public string ProgressText => string.Format(Label("Smart_ProgressFormat"), Items.Count,
        Items.Count(i => i.StateText == Label("Smart_State_Ready")));
    public string NextActionText => !IsEditing ? Label("Smart_Continue") : SelectedItem?.Step == SmartModItemStep.Summary && SelectedItem == Items.LastOrDefault()
        ? Label("Smart_GoChecks") : IsEditing && SelectedItem?.Step == SmartModItemStep.Summary ? Label("Smart_NextItem") : Label("Smart_Next");
    public string SelectedSummary => SelectedItem is not { } item ? "" :
        item.Name + Environment.NewLine + item.KindName + Environment.NewLine + Environment.NewLine +
        Label("Weapons_Damage") + ": " + item.Damage + Environment.NewLine + Label("Weapons_Weight") + ": " + item.Weight + Environment.NewLine +
        Label("Weapons_Value") + ": " + item.Value + Environment.NewLine + Environment.NewLine +
        Label("Models_Third") + ": " + item.ModelPath + Environment.NewLine + Label("Smart_Recipes") + ": " + item.Recipes.Count;
    [RelayCommand] private void GoToStep(SmartModItemStep step) { if (!IsBusy && SelectedItem != null) { SelectedItem.Step = step; Section = SmartModSection.Weapons; } }
    [RelayCommand] private void IncreaseType(SmartModTypeRow? row) { if (!IsBusy && row != null) row.Count = (Math.Clamp(int.TryParse(row.Count, out var count) ? count : 0, 0, 99) + 1).ToString(); }
    [RelayCommand] private void DecreaseType(SmartModTypeRow? row) { if (!IsBusy && row != null) row.Count = Math.Max(0, (int.TryParse(row.Count, out var count) ? count : 0) - 1).ToString(); }
}
