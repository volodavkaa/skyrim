using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>Serializes complete JSON transactions and publishes fully written files.</summary>
internal sealed class AtomicJsonFile<T>(string path, Func<T> createDefaults, JsonSerializerOptions options, ILogger? logger = null) where T : class
{
    private readonly string _path = PackingPathGuard.GetAbsolutePath(Path.GetFullPath(path));

    public async Task<T> LoadAsync(bool createIfMissing = false, CancellationToken token = default)
    {
        using var lease = await WorkspaceFileGate.AcquireAsync(_path, token).ConfigureAwait(false);
        var exists = File.Exists(_path);
        var value = await ReadAsync(token).ConfigureAwait(false);
        if (!exists && createIfMissing) await WriteUnderLeaseAsync(value, token).ConfigureAwait(false);
        return value;
    }

    public async Task UpdateAsync(Func<T, T> update, CancellationToken token = default)
    {
        using var lease = await WorkspaceFileGate.AcquireAsync(_path, token).ConfigureAwait(false);
        // Read under the same lease as the replacement; corrupt JSON is never silently reset.
        var current = await ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var updated = update(current);
        await WriteUnderLeaseAsync(updated, token).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PackingPathGuard.EnsureNoLinks(_path);
        if (!File.Exists(_path)) return createDefaults();
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, options, token).ConfigureAwait(false) ?? createDefaults();
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Cannot read JSON file '{_path}': {ex.Message}", ex);
        }
    }

    /// <summary>The caller must hold the destination lease or own a unique unpublished staging directory.</summary>
    internal async Task WriteUnderLeaseAsync(T value, CancellationToken token, Action? beforeCommit = null)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(_path)!, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        var backup = _path + ".bak";
        Exception? failure = null;
        var createdTemporary = false;
        try
        {
            token.ThrowIfCancellationRequested();
            PackingPathGuard.EnsureNoLinks(_path);
            PackingPathGuard.EnsureNoLinks(backup);
            PackingPathGuard.EnsureNoLinks(temporary);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                createdTemporary = true;
                await JsonSerializer.SerializeAsync(stream, value, options, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // Reopen the completed document before publishing it.
            await using (var verification = File.OpenRead(temporary))
                _ = await JsonSerializer.DeserializeAsync<T>(verification, options, token).ConfigureAwait(false)
                    ?? throw new JsonException("The temporary JSON document is null.");
            token.ThrowIfCancellationRequested();
            PackingPathGuard.EnsureNoLinks(_path);
            PackingPathGuard.EnsureNoLinks(backup);
            PackingPathGuard.EnsureNoLinks(temporary);
            // The temporary file is beside the destination, so publication stays on one volume.
            beforeCommit?.Invoke();
            token.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Replace(temporary, _path, backup);
            else File.Move(temporary, _path);
            // Once committed, report success even if cancellation arrives immediately afterwards.
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            if (createdTemporary && File.Exists(temporary))
            {
                try
                {
                    PackingPathGuard.EnsureNoLinks(temporary);
                    File.Delete(temporary);
                }
                catch (Exception cleanupError) when (failure != null)
                {
                    failure.Data["JsonTemporaryFile"] = temporary;
                    failure.Data["JsonCleanupFailure"] = cleanupError.Message;
                    logger?.LogWarning(cleanupError, "Failed to remove temporary JSON file {Path}; original operation error preserved", temporary);
                }
            }
        }
    }
}

