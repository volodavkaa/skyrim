using System.IO.Compression;
using System.Security.Cryptography;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.Archivers;

internal sealed record ZipManifestEntry(string SourcePath, string EntryPath, long Length, byte[] Hash);

internal static class ZipManifest
{
    public static void EnsureAllowed(string entry)
    {
        foreach (var part in entry.Split('/'))
            if (part.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || part.Equals("mod-project.json", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".backups", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".plugin-", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith(".assets-", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".project-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Service or backup file cannot be archived: '{entry}'.");
    }

    public static async Task<ZipManifestEntry> CaptureAsync(string source, string entry, CancellationToken token)
    {
        PackingPathGuard.EnsureNoLinks(source);
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return new(source, entry, stream.Length, await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    public static async Task VerifyAsync(string zipPath, IReadOnlyList<ZipManifestEntry> expected, CancellationToken token)
    {
        PackingPathGuard.EnsureNoLinks(zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        var names = PackingPathGuard.ValidateEntries(zip.Entries.Select(e => e.FullName));
        if (zip.Entries.Count != expected.Count) throw new InvalidDataException("ZIP entry count differs from the prepared manifest.");
        var entries = expected.ToDictionary(e => e.EntryPath, StringComparer.Ordinal);
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = zip.Entries[i];
            EnsureAllowed(names[i]);
            if (entry.FullName != names[i] || !entries.Remove(entry.FullName, out var original))
                throw new InvalidDataException($"Unexpected or non-canonical ZIP entry: '{entry.FullName}'.");
            if (entry.Length != original.Length) throw new InvalidDataException($"ZIP length mismatch: '{entry.FullName}'.");
            await using var stream = entry.Open();
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            if (!hash.AsSpan().SequenceEqual(original.Hash)) throw new InvalidDataException($"ZIP SHA-256 mismatch: '{entry.FullName}'.");
        }
        if (entries.Count != 0) throw new InvalidDataException("Required ZIP entries are missing.");
    }
}
