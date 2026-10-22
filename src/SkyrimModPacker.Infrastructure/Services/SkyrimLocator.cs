using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;

namespace SkyrimModPacker.Infrastructure.Services;

/// <summary>
/// Automatically detects Skyrim installation and Data path using Registry, Steam library configurations, and default paths.
/// </summary>
public sealed class SkyrimLocator : ISkyrimLocator
{
    private readonly ILogger<SkyrimLocator>? _logger;

    public SkyrimLocator(ILogger<SkyrimLocator>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Result<string> LocateSkyrimDataPath()
    {
        _logger?.LogInformation("Attempting to locate Skyrim Data directory...");

        if (OperatingSystem.IsWindows())
        {
            // 1. Try Bethesda & GOG registry keys
            var registryPath = FindFromRegistry();
            if (!string.IsNullOrEmpty(registryPath))
            {
                _logger?.LogInformation("Found Skyrim via Windows Registry: {Path}", registryPath);
                return Result<string>.Success(registryPath);
            }

            // 2. Try Steam library folders
            var steamPath = FindFromSteamLibraries();
            if (!string.IsNullOrEmpty(steamPath))
            {
                _logger?.LogInformation("Found Skyrim via Steam Libraries: {Path}", steamPath);
                return Result<string>.Success(steamPath);
            }
        }

        // 3. Fallback to common drive paths
        var fallbackPath = FindFromCommonPaths();
        if (!string.IsNullOrEmpty(fallbackPath))
        {
            _logger?.LogInformation("Found Skyrim via common path: {Path}", fallbackPath);
            return Result<string>.Success(fallbackPath);
        }

        _logger?.LogWarning("Skyrim Data directory could not be located automatically.");
        return Result<string>.Failure(
            "Skyrim Data directory could not be located automatically. Please specify it manually in Settings.");
    }

    [SupportedOSPlatform("windows")]
    private string? FindFromRegistry()
    {
        string[] registryKeys =
        [
            @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition",
            @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition",
            @"SOFTWARE\Bethesda Softworks\Skyrim",
            @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim",
            @"SOFTWARE\Bethesda Softworks\Skyrim VR",
            @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim VR",
            @"SOFTWARE\GOG.com\Games\1458058109",
            @"SOFTWARE\WOW6432Node\GOG.com\Games\1458058109"
        ];

        foreach (var keyName in registryKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyName);
                if (key != null)
                {
                    var installedPath = key.GetValue("Installed Path") as string
                                        ?? key.GetValue("path") as string;

                    if (!string.IsNullOrEmpty(installedPath))
                    {
                        var dataDir = ResolveDataDirectory(installedPath);
                        if (IsValidSkyrimData(dataDir))
                            return dataDir;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Registry query failed for key: {Key}", keyName);
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private string? FindFromSteamLibraries()
    {
        string? steamRoot = null;

        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamRoot = userKey?.GetValue("SteamPath") as string;

            if (string.IsNullOrEmpty(steamRoot))
            {
                using var machineKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                                       ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                steamRoot = machineKey?.GetValue("InstallPath") as string;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to read Steam path from registry.");
        }

        if (string.IsNullOrEmpty(steamRoot) || !Directory.Exists(steamRoot))
            return null;

        var libraryFolders = new List<string> { steamRoot };

        // Parse libraryfolders.vdf
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                var content = File.ReadAllText(vdfPath);
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var libPath = match.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(libPath) && !libraryFolders.Contains(libPath, StringComparer.OrdinalIgnoreCase))
                        {
                            libraryFolders.Add(libPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to parse libraryfolders.vdf at: {Path}", vdfPath);
            }
        }

        string[] gameSubDirs =
        [
            Path.Combine("steamapps", "common", "Skyrim Special Edition", "Data"),
            Path.Combine("steamapps", "common", "Skyrim", "Data"),
            Path.Combine("steamapps", "common", "SkyrimVR", "Data")
        ];

        foreach (var library in libraryFolders)
        {
            foreach (var subDir in gameSubDirs)
            {
                var fullDataPath = Path.Combine(library, subDir);
                if (IsValidSkyrimData(fullDataPath))
                    return fullDataPath;
            }
        }

        return null;
    }

    private string? FindFromCommonPaths()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => d.RootDirectory.FullName);

        string[] candidates =
        [
            @"SteamLibrary\steamapps\common\Skyrim Special Edition\Data",
            @"Steam\steamapps\common\Skyrim Special Edition\Data",
            @"Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data",
            @"Program Files\Steam\steamapps\common\Skyrim Special Edition\Data",
            @"Games\Skyrim Special Edition\Data",
            @"Games\Skyrim\Data"
        ];

        foreach (var drive in drives)
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(drive, candidate);
                if (IsValidSkyrimData(path))
                    return path;
            }
        }

        return null;
    }

    private static string ResolveDataDirectory(string basePath)
    {
        var trimmed = basePath.TrimEnd('\\', '/');
        if (trimmed.EndsWith("Data", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return Path.Combine(trimmed, "Data");
    }

    public static bool IsValidSkyrimData(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        // Check for Skyrim.esm or at least standard folders
        var skyrimEsm = Path.Combine(path, "Skyrim.esm");
        if (File.Exists(skyrimEsm))
            return true;

        var meshes = Path.Combine(path, "meshes");
        var textures = Path.Combine(path, "textures");
        return Directory.Exists(meshes) || Directory.Exists(textures);
    }
}
