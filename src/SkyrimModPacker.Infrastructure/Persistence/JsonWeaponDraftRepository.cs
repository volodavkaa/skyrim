using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>One atomic, revision-checked file per plugin and writer session. No recovery or plugin writes.</summary>
public sealed class JsonWeaponDraftRepository(string appDataDirectory) : IWeaponDraftRepository
{
    private readonly string _root = Path.GetFullPath(Path.Combine(appDataDirectory, "Drafts"));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public async Task<Result<DraftRecoveryScan>> FindPendingAsync(string pluginPath, string gameDataPath, Guid excludedSession, CancellationToken cancellationToken = default)
    {
        try
        {
            pluginPath = PackingPathGuard.GetAbsolutePath(pluginPath);
            PluginWriteGuard.EnsureOutsideGame(pluginPath, gameDataPath); PluginWriteGuard.EnsureOutsideGame(_root, gameDataPath);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pluginPath.ToUpperInvariant())));
            var folder = Path.Combine(_root, key);
            PackingPathGuard.EnsureNoLinks(folder);
            var candidates = new List<DraftRecoveryCandidate>(); var errors = new List<string>();
            if (!Directory.Exists(folder)) return Result<DraftRecoveryScan>.Success(new(candidates, errors));
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PackingPathGuard.EnsureNoLinks(path);
                    using var lease = await WorkspaceFileGate.AcquireAsync(path, cancellationToken);
                    await using var stream = File.OpenRead(path);
                    var draft = await JsonSerializer.DeserializeAsync<WeaponEditorDraft>(stream, Options, cancellationToken)
                        ?? throw new JsonException("Null draft.");
                    if (draft.Version != 1 || draft.Revision < 1 || draft.WriterSessionId == Guid.Empty || !Enum.IsDefined(draft.State) ||
                        draft.Weapon == null || draft.Recipe == null || !draft.PluginPath.Equals(pluginPath, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(path) != draft.WriterSessionId.ToString("N")) throw new IOException("Invalid draft identity/version.");
                    if (draft.WriterSessionId == excludedSession || draft.State != EditorDraftState.Pending ||
                        (!draft.WeaponIsDirty && !draft.RecipeIsDirty) || IsActive(draft)) continue;
                    candidates.Add(new(path, draft));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            return Result<DraftRecoveryScan>.Success(new(candidates.OrderByDescending(c => c.Draft.SavedAtUtc).ToArray(), errors));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<DraftRecoveryScan>.Failure(ex.Message); }
    }

    private static bool IsActive(WeaponEditorDraft draft)
    {
        if (draft.WriterProcessId <= 0 || draft.WriterProcessStartedUtc == null) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(draft.WriterProcessId);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - draft.WriterProcessStartedUtc.Value.UtcDateTime).TotalSeconds) < 1;
        }
        catch (ArgumentException) { return false; }
        catch { return true; } // Cannot prove a writer is dead: do not offer its file as an abandoned draft.
    }

    public async Task<Result<DraftWriteReceipt>> SaveAsync(WeaponEditorDraft draft, long expectedRevision, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (draft.Version != 1 || draft.WriterSessionId == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue ||
                !Enum.IsDefined(draft.State) || draft.Weapon == null || draft.Recipe == null)
                throw new InvalidOperationException("Invalid draft identity, version or revision.");
            var plugin = PackingPathGuard.GetAbsolutePath(draft.PluginPath);
            PluginWriteGuard.EnsureOutsideGame(plugin, draft.GameDataPath);
            PluginWriteGuard.EnsureOutsideGame(_root, draft.GameDataPath);
            if (!string.IsNullOrEmpty(draft.ProjectFilePath)) PluginWriteGuard.EnsureOutsideGame(draft.ProjectFilePath, draft.GameDataPath);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plugin.ToUpperInvariant())));
            var directory = Path.Combine(_root, key);
            var path = Path.Combine(directory, draft.WriterSessionId.ToString("N") + ".json");
            PluginWriteGuard.EnsureOutsideGame(path, draft.GameDataPath);
            Directory.CreateDirectory(directory);
            using var lease = await WorkspaceFileGate.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
            var snapshot = new WorkspaceFileSnapshot(path);
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                var current = await JsonSerializer.DeserializeAsync<WeaponEditorDraft>(stream, Options, cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("The existing draft is null; it was not overwritten.");
                if (current.Version != 1 || current.Revision < 1 || !Enum.IsDefined(current.State) || current.WriterSessionId != draft.WriterSessionId ||
                    !string.Equals(current.PluginPath, plugin, StringComparison.OrdinalIgnoreCase) || current.Revision != expectedRevision)
                    throw new InvalidOperationException("The draft changed or has an unsupported version. It was not overwritten.");
            }
            else if (expectedRevision != 0) throw new InvalidOperationException("The draft disappeared. It was not recreated over an unknown state.");
            var updated = draft with { PluginPath = plugin, Revision = expectedRevision + 1, SavedAtUtc = DateTimeOffset.UtcNow };
            var file = new AtomicJsonFile<WeaponEditorDraft>(path, () => new(), Options);
            await file.WriteUnderLeaseAsync(updated, cancellationToken, snapshot.EnsureUnchanged).ConfigureAwait(false);
            return Result<DraftWriteReceipt>.Success(new(path, updated.Revision, updated.SavedAtUtc));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<DraftWriteReceipt>.Failure(ex.Message); }
    }
}
