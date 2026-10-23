namespace SkyrimModPacker.Core.Common;

/// <summary>Protects the game directory when writing plugin workspaces and backups.</summary>
public static class PluginWriteGuard
{
    public static void EnsureOutsideGame(string path, string gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))
            throw new InvalidOperationException("The game Data path must be configured before writing plugins.");

        var dataDirectory = Path.TrimEndingDirectorySeparator(PackingPathGuard.GetAbsolutePath(gameDataPath));
        PackingPathGuard.EnsureNoLinks(dataDirectory);
        var gameDirectory = Path.GetFileName(dataDirectory).Equals("Data", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dataDirectory) ?? dataDirectory
            : dataDirectory;
        var fullPath = PackingPathGuard.GetAbsolutePath(path);
        var gamePrefix = Path.EndsInDirectorySeparator(gameDirectory) ? gameDirectory : gameDirectory + Path.DirectorySeparatorChar;
        if (fullPath.Equals(gameDirectory, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Writing inside the game directory is not allowed.");

        // Do not allow a workspace junction or symbolic link to redirect writes into the game.
        PackingPathGuard.EnsureNoLinks(fullPath);
    }
}
