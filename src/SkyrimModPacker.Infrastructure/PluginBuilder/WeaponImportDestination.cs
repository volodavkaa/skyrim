using SkyrimModPacker.Core.Common;
namespace SkyrimModPacker.Infrastructure.PluginBuilder;
public static class WeaponImportDestination
{
    public static string ProjectPath(string parent, string? name)
    {
        parent = PackingPathGuard.GetAbsolutePath(parent.Trim()); name = name?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new IOException("Назва моду має містити від 1 до 100 символів / Enter a mod name of 1–100 characters.");
        PackingPathGuard.NormalizeEntryPath(name); PackingPathGuard.NormalizeEntryPath(name + ".esp");
        if (Path.GetFileName(name) != name) throw new IOException("Вкажи назву, а не шлях / Enter a name, not a path.");
        return PackingPathGuard.ResolveUnderRoot(parent, name + "/mod-project.json");
    }
    public static string AvailableName(string parent, string? preferred)
    {
        preferred = preferred?.Trim() ?? "";
        for (var index = 1; index <= 10000; index++)
        {
            var suffix = index == 1 ? "" : "_" + index;
            var candidate = preferred.Trim()[..Math.Min(preferred.Trim().Length, 100 - suffix.Length)] + suffix;
            var directory = Path.GetDirectoryName(ProjectPath(parent, candidate))!;
            if (!Directory.Exists(directory) && !File.Exists(directory)) return candidate;
        }
        throw new IOException("Обери іншу папку збереження / Choose another output directory.");
    }
}
