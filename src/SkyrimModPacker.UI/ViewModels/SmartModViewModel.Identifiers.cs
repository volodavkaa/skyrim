using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    private WeaponOptions? _identifierSource;
    private Dictionary<string, string[]> _reservedIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private string IdentifierConflict(SmartModItemRow item)
    {
        var id = item.EditorId;
        if (id.Length is 0 or > 128 || !char.IsAsciiLetter(id[0]) || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) return Label("Smart_InvalidId");
        if (!ReferenceEquals(_identifierSource, Options))
        {
            _reservedIdentifiers = Options.ExistingEditorIds.GroupBy(o => o.EditorId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(o => o.Reference.ToString()).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
            _identifierSource = Options;
        }
        if (Items.Any(other => other.Id != item.Id && other.EditorId.Equals(item.EditorId, StringComparison.OrdinalIgnoreCase))) return Label("Smart_IdDraftConflict");
        return _reservedIdentifiers.TryGetValue(item.EditorId, out var sources) ? Label("Smart_IdMasterConflict") + " " + string.Join(", ", sources.Take(4)) : "";
    }
    public string IdentifierNotice => SelectedItem is not { } item ? "" : IdentifierConflict(item) is { Length: > 0 } conflict ? conflict : Label("Smart_IdAvailable");
    [RelayCommand]
    private void GenerateIdentifier()
    {
        if (IsBusy || SelectedItem == null) return;
        var prefix = new string(Name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(35).ToArray());
        SelectedItem.EditorId = "SMP_" + (prefix.Length == 0 ? "" : prefix + "_") + Guid.NewGuid().ToString("N");
    }
}
