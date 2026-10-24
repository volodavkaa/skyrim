using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed record SmartListTarget(PluginRecordReference? Reference, string DisplayName);

public sealed partial class SmartModViewModel
{
    [ObservableProperty] private string _listSearch = "";
    [ObservableProperty] private string _listCategory = "";
    [ObservableProperty] private string _listContents = "";
    private int _listGeneration;
    private readonly HashSet<Task> _activeListReads = [];
    [ObservableProperty] private string _listTargetSearch = "";
    [ObservableProperty] private SmartListTarget? _selectedListTarget;
    public bool ShowListChoices => ListCategory.Length > 0 || SelectedLeveledList != null;
    public bool HasListTargetSelection => ListCategory is "Equipment" or "Containers" or "Merchants";
    public bool CanShowTargetLists => !HasListTargetSelection || SelectedListTarget != null;
    public bool IsAdvancedListSelection => ListCategory is "All" or "Unknown";
    public string ListTargetResultText => Label(SelectedListTarget?.Reference == null ? "Smart_ListPickTargetHint" : FilteredLeveledLists.Take(2).Count() switch
        { 0 => "Smart_ListNoTargetLists", 1 => "Smart_ListOneTargetList", _ => "Smart_ListManyTargetLists" });
    public string ListScenarioHelp => Label("Smart_ListHelp_" + (ListCategory.Length == 0 ? "Start" : ListCategory));
    public IEnumerable<SmartListTarget> ListTargets => new[] { new SmartListTarget(null, Label("Smart_ListAllTargets")) }.Concat(
        Options.LeveledLists.SelectMany(l => l.ListUsages).Where(u => u.Category == ListCategory && (ListCategory != "Equipment" || u.Kind is "Npc" or ""))
            .GroupBy(u => u.Owner).Select(g => g.First()).Select(u => new SmartListTarget(u.Owner, (u.OwnerName.Length == 0 ? u.OwnerEditorId : u.OwnerName) + " [" + u.OwnerEditorId + "; " + u.Owner.PluginName + "]"))
            .Where(t => t.DisplayName.Contains(ListTargetSearch, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.DisplayName).Take(100));
    public IEnumerable<RecordOption> FilteredLeveledLists => Options.LeveledLists.Where(l =>
        (ListSearch.Length == 0 || (l.DisplayName + " " + l.EditorId + " " + l.Reference.PluginName).Contains(ListSearch, StringComparison.OrdinalIgnoreCase)) &&
        (ListCategory.Length == 0 || ListCategory == "All" || (ListCategory == "Unknown" ? l.ListUsages.Count == 0 : l.ListUsages.Any(u => u.Category == ListCategory))) &&
        (SelectedListTarget?.Reference == null || l.ListUsages.Any(u => u.Owner == SelectedListTarget.Reference && u.Category == ListCategory))).Take(50);
    public string LeveledListPurpose => SelectedLeveledList is not { } list || list.ListUsages.Count == 0 ? Label("Smart_ListUnknown") :
        string.Join(Environment.NewLine, list.ListUsages.Take(20).Select(u => Label("Smart_ListCategory_" + u.Category) + ": " +
                (u.OwnerName.Length == 0 ? u.OwnerEditorId : u.OwnerName) + " [" + u.OwnerEditorId + "; " + u.Owner + "]" + (u.ViaRecord == null ? "" : " → " + u.ViaRecord)));
    public string LeveledListSummary => SelectedItem == null || SelectedLeveledList == null ? Label("Smart_ListChoose") :
        string.Format(Label("Smart_ListSummary"), SelectedItem.Name, SelectedLeveledList.DisplayName + " [" + SelectedLeveledList.EditorId + "; " + SelectedLeveledList.Reference + "]", ListLevel, ListCount);
    partial void OnListSearchChanged(string value) { OnPropertyChanged(nameof(FilteredLeveledLists)); OnPropertyChanged(nameof(ListTargetResultText)); }
    partial void OnListCategoryChanged(string value)
    { SelectedListTarget = null; ListTargetSearch = ""; ListSearch = ""; SelectedLeveledList = null; RefreshListChoices(); }
    partial void OnListTargetSearchChanged(string value) => OnPropertyChanged(nameof(ListTargets));
    partial void OnSelectedListTargetChanged(SmartListTarget? value)
    {
        SelectedLeveledList = null; OnPropertyChanged(nameof(FilteredLeveledLists));
        var matches = FilteredLeveledLists.Take(2).ToArray();
        if (value?.Reference != null && matches.Length == 1) SelectedLeveledList = matches[0];
        OnPropertyChanged(nameof(ListTargetResultText));
        OnPropertyChanged(nameof(CanShowTargetLists));
    }
    private void RefreshListChoices()
    { foreach (var name in new[] { nameof(FilteredLeveledLists), nameof(ListTargets), nameof(ShowListChoices), nameof(ListScenarioHelp), nameof(HasListTargetSelection), nameof(IsAdvancedListSelection), nameof(ListTargetResultText), nameof(CanShowTargetLists) }) OnPropertyChanged(name); }
    [RelayCommand] private void FilterList(string? category) { ListCategory = string.IsNullOrWhiteSpace(category) ? "All" : category; RefreshListChoices(); }
    [RelayCommand] private void SkipList() { ListCategory = ""; SelectedLeveledList = null; ListContents = ""; RefreshListChoices(); }
    public async Task RefreshSelectedListAsync()
    {
        var generation = ++_listGeneration; var selection = SelectedLeveledList; ListContents = "";
        OnPropertyChanged(nameof(LeveledListPurpose)); OnPropertyChanged(nameof(LeveledListSummary));
        OnPropertyChanged(nameof(ShowListChoices));
        if (selection == null || !HasProject || _disposed) return;
        ListContents = Label("Smart_ListLoading");
        try
        {
            var read = _builder.GetLeveledListDetailsAsync(PluginPath, _gameData, selection.Reference, _lifetime.Token);
            lock (_activeListReads) _activeListReads.Add(read);
            SkyrimModPacker.Core.Common.Result<LeveledListDetails> result;
            try { result = await read; }
            finally { lock (_activeListReads) _activeListReads.Remove(read); }
            if (generation != _listGeneration || _disposed) return;
            if (result.IsFailure) { ListContents = result.Error; return; }
            var details = result.Value;
            var rules = details.SelectionFlags is { } flags && (flags & ~7u) == 0 ?
                Label((flags & 1) != 0 ? "Smart_ListRuleAllLevels" : "Smart_ListRuleHighestLevel") + "; " +
                Label((flags & 4) != 0 ? "Smart_ListRuleUseAll" : "Smart_ListRuleRandom") + "; " +
                Label((flags & 2) != 0 ? "Smart_ListRuleEach" : "Smart_ListRuleRepeat") : Label("Smart_ListRuleUnknown");
            ListContents = rules + Environment.NewLine + Label("Smart_ListChanceNone") + ": " + details.ChanceNone + "%" + Environment.NewLine +
                (details.Entries.Count == 0 ? Label("Smart_ListEmpty") : string.Join(Environment.NewLine, details.Entries.Select(e =>
                    e.Name + " [" + e.EditorId + "; " + e.Reference + "] — " + Label("Smart_ListMinLevel") + ": " + e.Level + "; " + Label("Smart_Quantity") + ": " + e.Count +
                    (e.Nested ? " (" + Label("Smart_ListNested") + ")" : "") + (e.Error == null ? "" : " — " + e.Error))));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _listGeneration && !_disposed) ListContents = ex.Message; }
    }
}
