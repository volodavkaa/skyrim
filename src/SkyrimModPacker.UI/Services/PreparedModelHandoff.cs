using System.IO;
using System.Text.Json;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.Services;

/// <summary>Reads the exporter receipt so a prepared pair never needs to be selected again.</summary>
public sealed record PreparedModelHandoff(string Folder, string ThirdPerson, string FirstPerson,
    string Name, WeaponKind Kind, SmartWeaponSubtype? Subtype)
{
    public static PreparedModelHandoff Read(string folder)
    {
        var root = Path.GetFullPath(folder);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "report.json")));
        var document = report.RootElement;
        var kind = document.GetProperty("weapon_type").GetString() switch
        {
            "sword1h" => WeaponKind.OneHandSword,
            "dagger" => WeaponKind.Dagger,
            "sword2h" => WeaponKind.Greatsword,
            "axe1h" => WeaponKind.WarAxe,
            "axe2h" or "hammer" => WeaponKind.BattleaxeWarhammer,
            "mace" => WeaponKind.Mace,
            var id => throw new InvalidDataException("Непідтримуваний тип підготовленої зброї: " + id)
        };
        var subtype = document.GetProperty("weapon_type").GetString() switch
        {
            "axe2h" => SmartWeaponSubtype.Battleaxe,
            "hammer" => SmartWeaponSubtype.Warhammer,
            _ => (SmartWeaponSubtype?)null
        };
        var views = document.GetProperty("views").EnumerateArray().Select(item =>
            AssetPath(root, item.GetProperty("output_nif").GetString())).ToArray();
        if (views.Length != 2 || views[0].Equals(views[1], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Звіт має містити окремі NIF для третьої та першої особи.");
        var name = document.GetProperty("model").GetString()?.Replace('_', ' ').Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(root);
        return new(root, views[0], views[1], name, kind, subtype);
    }

    public WeaponFolderRequest Request(string projectPath, string? newProjectPluginName, bool createNewItem)
    {
        var request = new WeaponFolderRequest(Guid.NewGuid(), projectPath, Folder,
            ThirdPerson, FirstPerson, false)
        {
            NewProjectPluginName = newProjectPluginName
        };
        if (createNewItem)
            request = request with
            {
                Items = [new WeaponFolderItemRequest(Guid.NewGuid(), Name, Kind,
                    ThirdPerson, FirstPerson, false)
                { EditorId = "SMP_" + Guid.NewGuid().ToString("N"), Subtype = Subtype }]
            };
        return request;
    }

    private static string AssetPath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Некоректний шлях NIF у звіті.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
            !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NIF зі звіту відсутній або виходить за межі комплекту: " + relative);
        return path;
    }
}
