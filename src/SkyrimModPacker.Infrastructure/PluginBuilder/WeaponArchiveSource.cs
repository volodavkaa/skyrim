using System.IO.Compression;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

/// <summary>A short-lived, bounded ZIP extraction. Only NIF/DDS are materialized; nothing is executed.</summary>
public sealed class WeaponArchiveSource : IDisposable, IAsyncDisposable
{
    private readonly string _parent;
    public string Root { get; }
    private WeaponArchiveSource()
    {
        _parent = Path.GetFullPath(Path.GetTempPath());
        Root = Path.Combine(_parent, "SkyrimWeaponImport-" + Guid.NewGuid().ToString("N"));
    }
    public static Task<WeaponArchiveSource> OpenAsync(string path, CancellationToken token = default) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("Select a ZIP archive / Обери ZIP-архів.");
        PackingPathGuard.EnsureNoLinks(path);
        var source = new WeaponArchiveSource();
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > 50000) throw new IOException("ZIP exceeds 50000 entries / Забагато записів в архіві.");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var selected = new List<(ZipArchiveEntry Entry, string Path)>(); long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Unsafe ZIP entry / Небезпечний шлях в архіві: " + entry.FullName);
                if (name.EndsWith('/')) continue;
                var target = PackingPathGuard.ResolveUnderRoot(source.Root, name);
                if (!targets.Add(target)) throw new IOException("Duplicate ZIP path / Повторний шлях в архіві: " + name);
                if (Path.GetExtension(name).ToLowerInvariant() is not (".nif" or ".dds")) continue;
                total = checked(total + entry.Length);
                if (entry.Length > 512L * 1024 * 1024 || total > 2L * 1024 * 1024 * 1024 || selected.Count >= 10000 || name.Split('/').Length > 65)
                    throw new IOException("ZIP resource limits exceeded (10000 files, 512 MiB/file, 2 GiB total, 64 levels).");
                selected.Add((entry, target));
            }
            if (selected.Count == 0) throw new IOException("No NIF/DDS found in ZIP / В архіві немає NIF або DDS.");
            foreach (var (entry, target) in selected)
            {
                token.ThrowIfCancellationRequested(); PackingPathGuard.EnsureNoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var compressed = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; long copied = 0; int read;
                while ((read = await compressed.ReadAsync(buffer, token)) != 0)
                { copied += read; if (copied > entry.Length) throw new InvalidDataException(entry.FullName + ": invalid ZIP length."); await output.WriteAsync(buffer.AsMemory(0, read), token); }
                if (copied != entry.Length) throw new InvalidDataException(entry.FullName + ": truncated ZIP resource.");
            }
            return source;
        }
        catch { source.Dispose(); throw; }
    }, token);
    public void Dispose()
    {
        var root = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(_parent) || !Path.GetFileName(root).StartsWith("SkyrimWeaponImport-", StringComparison.Ordinal)) throw new IOException("Invalid import cleanup path.");
        if (!Directory.Exists(root)) return;
        PackingPathGuard.EnsureNoLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) PackingPathGuard.EnsureNoLinks(entry);
        Directory.Delete(root, true);
    }
    public ValueTask DisposeAsync() => new(Task.Run(Dispose));
}
