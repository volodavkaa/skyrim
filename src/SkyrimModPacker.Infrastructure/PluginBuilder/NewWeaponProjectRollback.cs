using System.Security.Cryptography;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

/// <summary>Only for a directory just created by this operation. Foreign or changed files prevent cleanup.</summary>
public sealed class NewWeaponProjectRollback
{
    private readonly string _root;
    private readonly Dictionary<string, string> _files;
    private readonly string[] _requiredFiles;
    public NewWeaponProjectRollback(string justCreatedDirectory)
    {
        _root = PackingPathGuard.GetAbsolutePath(justCreatedDirectory); PackingPathGuard.EnsureNoLinks(_root);
        _files = ReadTree().Files.ToDictionary(p => p, Hash, StringComparer.OrdinalIgnoreCase); _requiredFiles = _files.Keys.ToArray();
        foreach (var name in new[] { "mod-project.json.lock", "smart-mod-draft.json.lock" })
            _files.TryAdd(Path.Combine(_root, name), Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
    }
    private (List<string> Files, List<string> Directories) ReadTree()
    {
        var files = new List<string>(); var directories = new List<string>(); Visit(_root, 0); return (files, directories);
        void Visit(string path, int depth)
        {
            if (depth > 64) throw new IOException("Unexpected project directory depth."); PackingPathGuard.EnsureNoLinks(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                PackingPathGuard.EnsureNoLinks(entry);
                if (Directory.Exists(entry)) { directories.Add(entry); Visit(entry, depth + 1); } else files.Add(entry);
            }
        }
    }
    private static string Hash(string file)
    { PackingPathGuard.EnsureNoLinks(file); using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    public void Rollback()
    {
        PackingPathGuard.EnsureNoLinks(_root);
        var (current, directories) = ReadTree();
        if (_requiredFiles.Any(p => !File.Exists(p)) || current.Any(p => !_files.TryGetValue(p, out var hash) || Hash(p) != hash))
            throw new IOException("Автоматичне очищення зупинено: папка містить змінені або додаткові файли / Changed files preserved: " + _root);
        // Delete only captured files, then empty directories. A concurrently added foreign file is never recursively deleted.
        foreach (var file in current) { if (Hash(file) != _files[file]) throw new IOException("Changed file preserved: " + file); File.Delete(file); }
        foreach (var directory in directories.OrderByDescending(p => p.Length)) Directory.Delete(directory, false);
        Directory.Delete(_root, false);
    }
}
