using Microsoft.Extensions.Logging;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

/// <summary>Owns the destination lock from reading the current plugin through verified publication.</summary>
internal sealed class PluginFileTransaction : IDisposable
{
    private readonly IDisposable _lease;
    private readonly string _path;
    private readonly string _gameDataPath;
    private readonly WorkspaceFileSnapshot _original;

    private PluginFileTransaction(IDisposable lease, string path, string gameDataPath)
    {
        _lease = lease;
        _path = path;
        _gameDataPath = gameDataPath;
        WorkspaceWriteExpectation.Check(path);
        _original = new WorkspaceFileSnapshot(path);
    }

    public static async Task<PluginFileTransaction> AcquireAsync(string path, string gameDataPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = PackingPathGuard.GetAbsolutePath(path);
        PluginWriteGuard.EnsureOutsideGame(path, gameDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lease = await WorkspaceFileGate.AcquireAsync(path, token).ConfigureAwait(false);
        try { return new PluginFileTransaction(lease, path, gameDataPath); }
        catch { lease.Dispose(); throw; }
    }

    public void Write(ISkyrimModGetter expected, Action<string> write, string? backupPath, bool replace, bool replaceBackup,
        CancellationToken token, ILogger? logger = null)
    {
        var stage = Path.Combine(Path.GetDirectoryName(_path)!, ".plugin-" + Guid.NewGuid().ToString("N"));
        PluginWriteGuard.EnsureOutsideGame(stage, _gameDataPath);
        if (backupPath != null)
        {
            backupPath = PackingPathGuard.GetAbsolutePath(backupPath);
            PluginWriteGuard.EnsureOutsideGame(backupPath, _gameDataPath);
            if (backupPath.Equals(_path, StringComparison.OrdinalIgnoreCase) || backupPath.Equals(_path + ".lock", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The backup must be a different file from the plugin and its lock.");
            if (!replaceBackup && (File.Exists(backupPath) || Directory.Exists(backupPath)))
                throw new IOException("The backup already exists; it was not overwritten.");
        }
        Exception? failure = null;
        string? temporaryBackup = null;
        Directory.CreateDirectory(stage);
        try
        {
            token.ThrowIfCancellationRequested();
            var temporary = Path.Combine(stage, Path.GetFileName(_path));
            write(temporary);
            token.ThrowIfCancellationRequested();
            PluginWriteGuard.EnsureOutsideGame(temporary, _gameDataPath);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);
            Validate(temporary, expected, Path.Combine(Path.GetDirectoryName(_path)!, "Strings"), token);
            token.ThrowIfCancellationRequested();
            PluginWriteGuard.EnsureOutsideGame(_path, _gameDataPath);
            _original.EnsureUnchanged();
            if (backupPath != null)
            {
                PluginWriteGuard.EnsureOutsideGame(backupPath, _gameDataPath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                if (!replaceBackup)
                {
                    // ESL keeps its historical no-overwrite backup contract, including cross-volume backups.
                    temporaryBackup = Path.Combine(Path.GetDirectoryName(backupPath)!, ".esl-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
                    PluginWriteGuard.EnsureOutsideGame(temporaryBackup, _gameDataPath);
                    File.Copy(_path, temporaryBackup, overwrite: false);
                    using (var backup = new FileStream(temporaryBackup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        backup.Flush(flushToDisk: true);
                    token.ThrowIfCancellationRequested();
                    PluginWriteGuard.EnsureOutsideGame(backupPath, _gameDataPath);
                    File.Move(temporaryBackup, backupPath);
                }
            }
            token.ThrowIfCancellationRequested();
            _original.EnsureUnchanged();
            token.ThrowIfCancellationRequested();
            if (replace) File.Replace(temporary, _path, replaceBackup ? backupPath : null);
            else File.Move(temporary, _path);
            WorkspaceWriteExpectation.Written(_path);
            // No cancellation after commit: the new plugin is already the published result.
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            if (temporaryBackup != null)
                Cleanup(() =>
                {
                    PluginWriteGuard.EnsureOutsideGame(temporaryBackup, _gameDataPath);
                    if (File.Exists(temporaryBackup)) File.Delete(temporaryBackup);
                }, temporaryBackup);
            Cleanup(() =>
            {
                PluginWriteGuard.EnsureOutsideGame(stage, _gameDataPath);
                // Only our unique directory and direct files; never follow a nested link.
                foreach (var file in Directory.EnumerateFiles(stage)) File.Delete(file);
                Directory.Delete(stage);
            }, stage);
        }

        void Cleanup(Action action, string path)
        {
            try { action(); }
            catch (Exception cleanupError)
            {
                logger?.LogWarning(cleanupError, "Failed to clean plugin temporary path {Path}", path);
                if (failure != null)
                {
                    failure.Data["PluginCleanupFailure:" + path] = cleanupError.Message;
                }
                else if (logger == null) throw;
            }
        }
    }

    private static void Validate(string temporary, ISkyrimModGetter expected, string stringsFolder, CancellationToken token)
    {
        // Mutable construction eagerly parses records rather than merely accepting an overlay header.
        var actual = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(temporary)
            .WithStringsFolder(stringsFolder).WithUtf8Encoding().Mutable().Construct();
        if (actual.ModKey != expected.ModKey || actual.IsSmallMaster != expected.IsSmallMaster || actual.IsMaster != expected.IsMaster ||
            actual.UsingLocalization != expected.UsingLocalization || actual.ModHeader.Stats.Version != expected.ModHeader.Stats.Version ||
            actual.ModHeader.Stats.NextFormID != expected.ModHeader.Stats.NextFormID ||
            !actual.ModHeader.MasterReferences.Select(m => m.Master).SequenceEqual(expected.ModHeader.MasterReferences.Select(m => m.Master)))
            throw new IOException("The temporary plugin header does not match the intended plugin.");
        var expectedRecords = expected.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
        var records = actual.EnumerateMajorRecords().ToList();
        if (records.Count != expectedRecords.Count) throw new IOException("The temporary plugin record count changed during writing.");
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (!expectedRecords.TryGetValue(record.FormKey, out var original) || record.GetType() != original.GetType() ||
                record.EditorID != original.EditorID || record.IsDeleted != original.IsDeleted ||
                !record.EnumerateFormLinks().Select(l => l.FormKey).SequenceEqual(original.EnumerateFormLinks().Select(l => l.FormKey)))
                throw new IOException($"The temporary plugin record differs from the intended record: {record.FormKey}.");
            if (record is IWeaponGetter weapon && original is IWeaponGetter expectedWeapon &&
                ((weapon.Name?.String ?? string.Empty) != (expectedWeapon.Name?.String ?? string.Empty) ||
                 (weapon.Description?.String ?? string.Empty) != (expectedWeapon.Description?.String ?? string.Empty) ||
                 weapon.BasicStats?.Damage != expectedWeapon.BasicStats?.Damage || weapon.BasicStats?.Weight != expectedWeapon.BasicStats?.Weight ||
                 weapon.BasicStats?.Value != expectedWeapon.BasicStats?.Value || weapon.Data?.AnimationType != expectedWeapon.Data?.AnimationType ||
                 weapon.Data?.Speed != expectedWeapon.Data?.Speed || weapon.Data?.Reach != expectedWeapon.Data?.Reach ||
                 weapon.Model?.File.GivenPath != expectedWeapon.Model?.File.GivenPath || weapon.EnchantmentAmount != expectedWeapon.EnchantmentAmount))
                throw new IOException($"The temporary weapon properties differ from the intended record: {record.FormKey}.");
            if (record is IConstructibleObjectGetter recipe && original is IConstructibleObjectGetter expectedRecipe &&
                (recipe.CreatedObjectCount != expectedRecipe.CreatedObjectCount ||
                 !(recipe.Items ?? []).Select(i => i.Item.Count).SequenceEqual((expectedRecipe.Items ?? []).Select(i => i.Item.Count))))
                throw new IOException($"The temporary recipe counts differ from the intended record: {record.FormKey}.");
            if (record is IStaticGetter stat && original is IStaticGetter expectedStat &&
                stat.Model?.File.GivenPath != expectedStat.Model?.File.GivenPath)
                throw new IOException($"The temporary STAT model differs from the intended record: {record.FormKey}.");
            if (record is ILeveledItemGetter list && original is ILeveledItemGetter expectedList &&
                (list.Flags != expectedList.Flags || list.ChanceNone != expectedList.ChanceNone ||
                 !(list.Entries ?? []).Select(e => (e.Data?.Level, e.Data?.Count)).SequenceEqual(
                     (expectedList.Entries ?? []).Select(e => (e.Data?.Level, e.Data?.Count)))))
                throw new IOException($"The temporary leveled list differs from the intended record: {record.FormKey}.");
        }
    }

    public void Dispose() => _lease.Dispose();
}
