using System.Security.Cryptography;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>Detects a destination changed by an editor that does not honor our lock.</summary>
internal sealed class WorkspaceFileSnapshot(string path)
{
    private readonly byte[]? _hash = Hash(path);

    public void EnsureUnchanged()
    {
        var current = Hash(path);
        if (_hash == null ? current != null : current == null || !_hash.AsSpan().SequenceEqual(current))
            throw new IOException($"File changed during the operation; reopen it and retry: '{path}'.");
    }

    private static byte[]? Hash(string path)
    {
        PackingPathGuard.EnsureNoLinks(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }
}
