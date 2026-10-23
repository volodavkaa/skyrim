using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Core.Common;

/// <summary>Shared path rules for packing sources, staging destinations and ZIP entries.</summary>
public static class PackingPathGuard
{
    public static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("A non-empty relative file path is required.");
        var normalized = path.Replace('\\', '/');
        foreach (var part in normalized.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidOperationException($"Unsafe relative file path: '{path}'. Use a path without roots, '..', empty components or Windows filename aliases.");
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidOperationException($"Reserved Windows filename in path: '{path}'.");
        }
        return normalized;
    }

    public static string GetAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.Replace('\\', '/').StartsWith("//?/") || path.Replace('\\', '/').StartsWith("//./"))
            throw new InvalidOperationException("Use an absolute filesystem path without a device prefix.");
        var root = Path.GetPathRoot(path)!;
        var relative = path[root.Length..].Replace('\\', '/').TrimEnd('/');
        if (relative.Length > 0) NormalizeEntryPath(relative);
        return Path.GetFullPath(path);
    }

    public static void EnsureNoLinks(string path)
    {
        for (var current = GetAbsolutePath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            // LinkTarget also detects a dangling link, which Exists deliberately does not follow.
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget != null)
                throw new InvalidOperationException($"Packing paths must not contain symbolic links or junctions: '{current}'.");
        }
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(GetAbsolutePath(root));
        var relative = NormalizeEntryPath(relativePath);
        var target = GetAbsolutePath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"File path escapes its allowed directory: '{relativePath}'.");
        EnsureNoLinks(target);
        return target;
    }

    public static void EnsureStagingDirectory(string path, string gameDataPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(GetAbsolutePath(path));
        PluginWriteGuard.EnsureOutsideGame(fullPath, gameDataPath);
        var stagingRoot = Path.TrimEndingDirectorySeparator(GetAbsolutePath(Path.Combine(Path.GetTempPath(), "SkyrimModPacker", "staging")));
        if (!string.Equals(Path.GetDirectoryName(fullPath), stagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _))
            throw new InvalidOperationException("Staging must be a unique workspace prepared by the copy step.");
    }

    public static IReadOnlyList<string> ValidateEntries(IEnumerable<string> paths)
    {
        var normalized = paths.Select(NormalizeEntryPath).ToList();
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in normalized)
            if (!entries.Add(path))
                throw new InvalidOperationException($"Conflicting archive paths: '{path}'. Each file must have a unique path.");
        foreach (var path in normalized)
        {
            for (var separator = path.LastIndexOf('/'); separator >= 0; separator = path.LastIndexOf('/', separator - 1))
                if (entries.Contains(path[..separator]))
                    throw new InvalidOperationException($"A file conflicts with a directory in the archive: '{path}'.");
        }
        return normalized;
    }

    public static void DeleteStagingDirectory(string path, string gameDataPath)
    {
        EnsureStagingDirectory(path, gameDataPath);
        if (!Directory.Exists(path)) return;
        DeleteContents(path);
        Directory.Delete(path);

        void DeleteContents(string directory)
        {
            EnsureStagingDirectory(path, gameDataPath);
            EnsureNoLinks(directory);
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                EnsureStagingDirectory(path, gameDataPath);
                if (entry is DirectoryInfo)
                {
                    if (entry.LinkTarget == null) DeleteContents(entry.FullName);
                    // Remove directory links themselves, without entering their targets.
                    Directory.Delete(entry.FullName);
                }
                else File.Delete(entry.FullName);
            }
        }
    }

    public static void EnsureSource(string sourcePath, string relativePath, string root)
    {
        var expected = ResolveUnderRoot(root, relativePath);
        if (!GetAbsolutePath(sourcePath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Source file does not match its allowed directory and relative path: '{relativePath}'.");
        EnsureNoLinks(sourcePath);
    }

    public static void ValidateSources(IReadOnlyList<ScannedFile> files, ModProfile profile, string gameDataPath)
    {
        ValidateEntries(files.Select(file => file.RelativePath));
        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(profile.WeaponProjectPath))
                EnsureSource(file.AbsolutePath, file.RelativePath, Path.GetDirectoryName(GetAbsolutePath(profile.WeaponProjectPath.Trim()))!);
            else if (!string.IsNullOrWhiteSpace(profile.PluginSourcePath) && file.Category == FileCategory.Plugin &&
                     GetAbsolutePath(file.AbsolutePath).Equals(GetAbsolutePath(profile.PluginSourcePath.Trim()), StringComparison.OrdinalIgnoreCase) &&
                     NormalizeEntryPath(file.RelativePath).Equals(Path.GetFileName(profile.PluginSourcePath.Trim()), StringComparison.OrdinalIgnoreCase))
                EnsureNoLinks(file.AbsolutePath);
            else
                EnsureSource(file.AbsolutePath, file.RelativePath, gameDataPath);
        }
    }
}
