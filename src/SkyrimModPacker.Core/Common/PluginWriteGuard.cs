namespace SkyrimModPacker.Core.Common;

/// <summary>Protects the game directory when writing plugin workspaces and backups.</summary>
public static class PluginWriteGuard
{
    public static void EnsureOutsideGame(string path, string gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))
            throw new InvalidOperationException("The game Data path must be configured before writing plugins.");

        var dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataPath));
        var gameDirectory = Path.GetFileName(dataDirectory).Equals("Data", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dataDirectory) ?? dataDirectory
            : dataDirectory;
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Equals(gameDirectory, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(Path.TrimEndingDirectorySeparator(gameDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Writing inside the game directory is not allowed.");

        // Do not allow a workspace junction or symbolic link to redirect writes into the game.
        for (var current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (entry.LinkTarget != null)
                    throw new InvalidOperationException("Plugin output and backup paths must not contain symbolic links or junctions.");
            }
        }
    }
}
