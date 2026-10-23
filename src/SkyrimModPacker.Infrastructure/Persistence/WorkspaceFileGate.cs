using System.Collections.Concurrent;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>One in-process gate per normalized path plus an exclusive cross-process lock handle.</summary>
internal static class WorkspaceFileGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> AcquireAsync(string path, CancellationToken token)
    {
        path = PackingPathGuard.GetAbsolutePath(Path.GetFullPath(path));
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            PackingPathGuard.EnsureNoLinks(path);
            var lockPath = path + ".lock";
            PackingPathGuard.EnsureNoLinks(lockPath);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                PackingPathGuard.EnsureNoLinks(lockPath);
                try
                {
                    return new Lease(gate, new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
                {
                    await Task.Delay(25, token).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate, FileStream file) : IDisposable
    {
        public void Dispose()
        {
            try { file.Dispose(); }
            finally { gate.Release(); }
        }
    }
}
