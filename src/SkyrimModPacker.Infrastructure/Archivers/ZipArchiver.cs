using System.IO.Compression;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.Archivers;

/// <summary>Publishes only a fully written ZIP matching prepared SHA-256 fingerprints.</summary>
public sealed class ZipArchiver(ILogger<ZipArchiver>? logger = null) : IArchiver
{
    public string Extension => ".zip";

    public Task CreateArchiveAsync(string outputPath, IReadOnlyList<(string SourcePath, string EntryPath)> files,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(outputPath, files, progress, cancellationToken, overwrite: true);

    public Task CreateNewArchiveAsync(string outputPath, IReadOnlyList<(string SourcePath, string EntryPath)> files,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(outputPath, files, progress, cancellationToken, overwrite: false);

    private async Task CreateCoreAsync(string outputPath, IReadOnlyList<(string SourcePath, string EntryPath)> files,
        IProgress<double>? progress, CancellationToken cancellationToken, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
        if (files == null || files.Count == 0) throw new ArgumentException("No files specified for archive creation.", nameof(files));
        cancellationToken.ThrowIfCancellationRequested();
        outputPath = PackingPathGuard.GetAbsolutePath(outputPath);
        PackingPathGuard.EnsureNoLinks(outputPath);
        var names = PackingPathGuard.ValidateEntries(files.Select(f => f.EntryPath));
        var prepared = files.Select((f, i) => (Source: PackingPathGuard.GetAbsolutePath(f.SourcePath), Entry: names[i])).ToArray();
        foreach (var file in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipManifest.EnsureAllowed(file.Entry);
            PackingPathGuard.EnsureNoLinks(file.Source);
            if (file.Source.Equals(outputPath, StringComparison.OrdinalIgnoreCase) || file.Source.Equals(outputPath + ".lock", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Archive output or its lock must not overwrite a source file.");
            if (!File.Exists(file.Source)) throw new FileNotFoundException("Archive source is missing.", file.Source);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var lease = await WorkspaceFileGate.AcquireAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (!overwrite && File.Exists(outputPath)) throw new IOException("Archive already exists: " + outputPath);
        var previous = new WorkspaceFileSnapshot(outputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(outputPath)!, ".zip-" + Guid.NewGuid().ToString("N") + ".tmp");
        Exception? failure = null;
        var created = false;
        try
        {
            var manifest = new List<ZipManifestEntry>();
            foreach (var file in prepared)
                manifest.Add(await ZipManifest.CaptureAsync(file.Source, file.Entry, cancellationToken).ConfigureAwait(false));
            PackingPathGuard.EnsureNoLinks(temporary);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                created = true;
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var i = 0; i < manifest.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var file = manifest[i];
                        PackingPathGuard.EnsureNoLinks(file.SourcePath);
                        var entry = zip.CreateEntry(file.EntryPath, CompressionLevel.Optimal);
                        await using (var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                        await using (var destination = entry.Open())
                            await input.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                        progress?.Report(0.6 * (i + 1) / manifest.Count);
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            progress?.Report(0.65);
            var verifiedZip = await ZipManifest.CaptureAsync(temporary, "", cancellationToken).ConfigureAwait(false);
            await ZipManifest.VerifyAsync(temporary, manifest, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.85);
            progress?.Report(1);
            foreach (var file in manifest)
            {
                var current = await ZipManifest.CaptureAsync(file.SourcePath, file.EntryPath, cancellationToken).ConfigureAwait(false);
                if (current.Length != file.Length || !current.Hash.AsSpan().SequenceEqual(file.Hash))
                    throw new IOException($"Archive source changed during packing: '{file.SourcePath}'. Scan and pack again.");
                logger?.LogInformation("Verified ZIP entry {Entry}: SHA-256 {Hash}", file.EntryPath, Convert.ToHexString(file.Hash));
            }
            logger?.LogInformation("Validated temporary ZIP SHA-256 {Hash}; publication pending", Convert.ToHexString(verifiedZip.Hash));
            cancellationToken.ThrowIfCancellationRequested();
            PackingPathGuard.EnsureNoLinks(outputPath);
            PackingPathGuard.EnsureNoLinks(temporary);
            previous.EnsureUnchanged();
            var finalZip = await ZipManifest.CaptureAsync(temporary, "", cancellationToken).ConfigureAwait(false);
            if (finalZip.Length != verifiedZip.Length || !finalZip.Hash.AsSpan().SequenceEqual(verifiedZip.Hash))
                throw new IOException("The verified temporary ZIP changed before publication.");
            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(outputPath)) File.Replace(temporary, outputPath, null);
            else File.Move(temporary, outputPath);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            if (created && File.Exists(temporary))
                try { PackingPathGuard.EnsureNoLinks(temporary); File.Delete(temporary); }
                catch (Exception cleanup) when (failure != null)
                {
                    failure.Data["ZipTemporaryFile"] = temporary;
                    failure.Data["ZipCleanupFailure"] = cleanup.Message;
                    logger?.LogWarning(cleanup, "Unable to clean temporary ZIP {Path}; original failure preserved", temporary);
                }
        }
    }
}
