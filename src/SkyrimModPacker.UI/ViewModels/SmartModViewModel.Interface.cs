using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed record SmartModStepLink(SmartModItemStep Step, string Name, bool IsCurrent);

public sealed partial class SmartModViewModel
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ShowTemplateSetup))]
    private bool _gameTemplateSetupExpanded;
    public bool ShowTemplateSetup => ShowSetup && (GameTemplateSetupExpanded || Types.Any(t => int.TryParse(t.Count, out var count) && count > 0));
    public bool CanCreateProject => !IsBusy && !HasProject && Types.Count > 0 && Types.All(t => int.TryParse(t.Count, out var count) && count >= 0 && count <= 100) && Types.Sum(t => int.Parse(t.Count)) is > 0 and <= 100;
    public string SetupCountText => string.Format(Label("Smart_SetupCountFormat"), Types.Sum(t => int.TryParse(t.Count, out var count) ? Math.Clamp(count, 0, 100) : 0));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanCreateProject)); OnPropertyChanged(nameof(CanStartFromResources)); }
    public IEnumerable<SmartModStepLink> StepLinks => Enum.GetValues<SmartModItemStep>().Select(step =>
        new SmartModStepLink(step, Label("Smart_Step_" + step), SelectedItem?.Step == step));
    public string StepHelp => SelectedItem == null ? "" : Label("Smart_Help_" + SelectedItem.Step);
    public string ProgressText => string.Format(Label("Smart_ProgressFormat"), Items.Count,
        Items.Count(i => i.StateText == Label("Smart_State_Ready")));
    public string InheritanceSource
    {
        get
        {
            var template = Templates.FirstOrDefault(t => t.FormId == SelectedItem?.Template?.FormId && t.PluginName == SelectedItem.Template.PluginName);
            return template == null ? Label("Smart_UnknownTemplate") : template.Name + " [" + template.PluginName + ":" + template.FormId.ToString("X6") + "]";
        }
    }
    public string NextActionText => !IsEditing ? Label("Smart_Continue") : SelectedItem?.Step == SmartModItemStep.Summary && SelectedItem == Items.LastOrDefault()
        ? Label("Smart_GoChecks") : IsEditing && SelectedItem?.Step == SmartModItemStep.Summary ? Label("Smart_NextItem") : Label("Smart_Next");
    public string SelectedSummary => SelectedItem is not { } item ? "" :
        item.Name + " [" + item.EditorId + "]" + Environment.NewLine + item.KindName + " / " + CurrentSubtype + Environment.NewLine + Environment.NewLine +
        Label("Weapons_Damage") + ": " + item.Damage + Environment.NewLine + Label("Weapons_Weight") + ": " + item.Weight + Environment.NewLine +
        Label("Weapons_Value") + ": " + item.Value + Environment.NewLine + Environment.NewLine +
        Label("Models_Third") + ": " + item.ModelPath + Environment.NewLine + Label("Models_First") + ": " + item.FirstPersonPath +
        (item.UseSharedModel ? " (" + Label("Smart_SharedModel") + ")" : "") + Environment.NewLine + Environment.NewLine + TuningSummary + Environment.NewLine +
        string.Join(Environment.NewLine, BalanceComparison.Select(r => r.Parameter + ": " + r.Reference + " → " + r.Current + "; " + r.Difference)) + Environment.NewLine + Environment.NewLine +
        string.Join(Environment.NewLine, item.Recipes.Select(r => Label(r.IsTempering ? "Smart_Tempering" : "Smart_Crafting") + ": " +
            string.Join(", ", r.Ingredients.Select(i => SummaryReference(i.Reference) + " × " + i.Count)) + "; " + SummaryReference(r.Workbench) + "; " + SummaryReference(r.RequiredPerk)));
    private string SummaryReference(PluginRecordReference? reference) => reference == null ? "—" :
        (Options.Ingredients.Concat(Options.Keywords).Concat(Options.Perks).FirstOrDefault(o => o.Reference == reference)?.DisplayName ?? reference.ToString());
    [RelayCommand] private void GoToStep(SmartModItemStep step) { if (!IsBusy && SelectedItem != null) { SelectedItem.Step = step; Section = SmartModSection.Weapons; } }
    [RelayCommand] private void IncreaseType(SmartModTypeRow? row) { if (!IsBusy && row != null) row.Count = (Math.Clamp(int.TryParse(row.Count, out var count) ? count : 0, 0, 99) + 1).ToString(); }
    [RelayCommand] private void DecreaseType(SmartModTypeRow? row) { if (!IsBusy && row != null) row.Count = Math.Max(0, (int.TryParse(row.Count, out var count) ? count : 0) - 1).ToString(); }
}
