using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.FileSystem;

/// <summary>
/// Scans the Skyrim Data directory for meshes, textures, and plugins matching profile keywords.
/// Preserves relative directory paths and only reads from the filesystem.
/// </summary>
public sealed class ModScanner : IModScanner
{
    private static readonly string[] PluginExtensions = [".esp", ".esl", ".esm"];
    private readonly ILogger<ModScanner>? _logger;

    public ModScanner(ILogger<ModScanner>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<List<ScannedFile>>> ScanAsync(
        string dataPath,
        ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(dataPath))
                return Result<List<ScannedFile>>.Failure("Skyrim Data path is not specified.");

            if (!Directory.Exists(dataPath))
                return Result<List<ScannedFile>>.Failure($"Skyrim Data directory not found: '{dataPath}'");

            var validationResult = ModProfileValidator.Validate(profile);
            if (validationResult.IsFailure)
                return Result<List<ScannedFile>>.Failure(validationResult.Error);

            cancellationToken.ThrowIfCancellationRequested();

            var keywords = profile.Keywords
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .ToList();

            var results = new Dictionary<string, ScannedFile>(StringComparer.OrdinalIgnoreCase);

            _logger?.LogInformation("Starting scan in '{DataPath}' for mod '{ModName}' with {KeywordCount} keywords",
                dataPath, profile.Name, keywords.Count);

            // 1. Scan plugin file
            ScanPlugin(dataPath, profile, keywords, results);
            cancellationToken.ThrowIfCancellationRequested();

            // 2. Scan meshes directory
            var meshesPath = Path.Combine(dataPath, "meshes");
            if (Directory.Exists(meshesPath) && keywords.Count > 0)
            {
                ScanDirectoryRecursively(dataPath, meshesPath, keywords, FileCategory.Mesh, results, cancellationToken);
            }

            // 3. Scan textures directory
            var texturesPath = Path.Combine(dataPath, "textures");
            if (Directory.Exists(texturesPath) && keywords.Count > 0)
            {
                ScanDirectoryRecursively(dataPath, texturesPath, keywords, FileCategory.Texture, results, cancellationToken);
            }

            // 4. Optional: Scan other common mod directories (sound, SKSE)
            ScanOptionalDirectory(dataPath, "sound", keywords, FileCategory.Other, results, cancellationToken);
            ScanOptionalDirectory(dataPath, "SKSE", keywords, FileCategory.Other, results, cancellationToken);

            var fileList = results.Values
                .OrderBy(f => f.Category)
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger?.LogInformation("Scan completed. Found {Count} matching files.", fileList.Count);

            return Result<List<ScannedFile>>.Success(fileList);
        }, cancellationToken);
    }

    private void ScanPlugin(
        string dataPath,
        ModProfile profile,
        List<string> keywords,
        Dictionary<string, ScannedFile> results)
    {
        if (!string.IsNullOrWhiteSpace(profile.PluginFileName))
        {
            var pluginName = profile.PluginFileName.Trim();
            var candidatePath = Path.Combine(dataPath, pluginName);

            if (File.Exists(candidatePath))
            {
                AddFile(dataPath, candidatePath, FileCategory.Plugin, results);
                return;
            }

            // Try appending standard extensions if extension was omitted
            foreach (var ext in PluginExtensions)
            {
                var withExt = Path.Combine(dataPath, $"{pluginName}{ext}");
                if (File.Exists(withExt))
                {
                    AddFile(dataPath, withExt, FileCategory.Plugin, results);
                    return;
                }
            }

            // Try case-insensitive file search in Data root
            try
            {
                var rootFiles = Directory.EnumerateFiles(dataPath, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in rootFiles)
                {
                    var fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, pluginName, StringComparison.OrdinalIgnoreCase))
                    {
                        AddFile(dataPath, file, FileCategory.Plugin, results);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error searching for plugin in '{DataPath}'", dataPath);
            }
        }
        else if (keywords.Count > 0)
        {
            // If plugin name was not specified, check if any plugin file in Data matches keywords
            try
            {
                foreach (var file in Directory.EnumerateFiles(dataPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file);
                    if (PluginExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                        if (keywords.Any(k => fileNameNoExt.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        {
                            AddFile(dataPath, file, FileCategory.Plugin, results);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error enumerating plugins in '{DataPath}'", dataPath);
            }
        }
    }

    private void ScanDirectoryRecursively(
        string dataRoot,
        string currentDir,
        List<string> keywords,
        FileCategory category,
        Dictionary<string, ScannedFile> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dirName = Path.GetFileName(currentDir);
        var dirMatchesKeyword = keywords.Any(k => dirName.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (dirMatchesKeyword)
        {
            // Entire directory matches: include all files in this subtree
            IncludeAllFilesUnder(dataRoot, currentDir, category, results, cancellationToken);
            return;
        }

        // Check individual files in this directory
        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                if (keywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    AddFile(dataRoot, file, category, results);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Access denied to files in '{CurrentDir}'", currentDir);
        }

        // Recurse into subdirectories
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                ScanDirectoryRecursively(dataRoot, subDir, keywords, category, results, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Access denied to directories in '{CurrentDir}'", currentDir);
        }
    }

    private void IncludeAllFilesUnder(
        string dataRoot,
        string folder,
        FileCategory category,
        Dictionary<string, ScannedFile> results,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddFile(dataRoot, file, category, results);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Access denied while enumerating files under '{Folder}'", folder);
        }
    }

    private void ScanOptionalDirectory(
        string dataRoot,
        string subFolderName,
        List<string> keywords,
        FileCategory category,
        Dictionary<string, ScannedFile> results,
        CancellationToken cancellationToken)
    {
        var targetDir = Path.Combine(dataRoot, subFolderName);
        if (Directory.Exists(targetDir) && keywords.Count > 0)
        {
            ScanDirectoryRecursively(dataRoot, targetDir, keywords, category, results, cancellationToken);
        }
    }

    private static void AddFile(
        string dataRoot,
        string fullPath,
        FileCategory category,
        Dictionary<string, ScannedFile> results)
    {
        var relativePath = Path.GetRelativePath(dataRoot, fullPath);
        var normalizedKey = relativePath.Replace('/', '\\');

        if (results.ContainsKey(normalizedKey))
            return;

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            return;

        results[normalizedKey] = new ScannedFile
        {
            AbsolutePath = fullPath,
            RelativePath = normalizedKey,
            SizeBytes = fileInfo.Length,
            IsSelected = true,
            Category = category
        };
    }
}
