using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.Esl;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using System.Buffers.Binary;
using Mutagen.Bethesda.Skyrim;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Infrastructure.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.Esl;

/// <summary>ESL analysis and flagging of workspace copies without FormID compaction.</summary>
public sealed class EslService : IEslService
{
    private readonly ILogger<EslService>? _logger;

    public EslService(ILogger<EslService>? logger = null)
    {
        _logger = logger;
    }

    public async Task<Result<string>> SetFlagAsync(string pluginPath, string gameDataPath, string? backupPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            PluginWriteGuard.EnsureOutsideGame(pluginPath, gameDataPath);
            backupPath = Path.GetFullPath(backupPath ?? pluginPath + ".bak");
            PluginWriteGuard.EnsureOutsideGame(backupPath, gameDataPath);
            if (Path.GetFullPath(pluginPath).Equals(backupPath, StringComparison.OrdinalIgnoreCase))
                return Result<string>.Failure("The backup must be a different file.");

            using var transaction = await PluginFileTransaction.AcquireAsync(pluginPath, gameDataPath, cancellationToken).ConfigureAwait(false);
            // Analyze only after acquiring the same destination lock used by the editor.
            var analysis = await AnalyzeAsync(pluginPath, cancellationToken);
            if (analysis.IsFailure)
                return Result<string>.Failure(analysis.Error);
            if (!analysis.Value.IsCompatible)
                return Result<string>.Failure(string.Join(Environment.NewLine, analysis.Value.Reasons));
            if (analysis.Value.IsAlreadyEsl)
                return Result<string>.Success(string.Empty);

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Mutable().Construct();
                mod.IsSmallMaster = true;
                transaction.Write(mod, temporaryPath =>
                {
                    // Set only TES4's ESL bit, preserving string encodings, localized string IDs,
                    // record bytes, master order and NextFormID exactly. Mutagen verifies the copy afterwards.
                    File.Copy(pluginPath, temporaryPath, overwrite: false);
                    using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Span<byte> header = stackalloc byte[12];
                    stream.ReadExactly(header);
                    if (!header[..4].SequenceEqual("TES4"u8)) throw new IOException("The plugin has no TES4 header.");
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) | 0x200u;
                    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], flags);
                    stream.Position = 8;
                    stream.Write(header[8..]);
                }, backupPath, replace: true, replaceBackup: false, cancellationToken, _logger);
                _logger?.LogInformation("ESL flag set on {Plugin}; backup: {Backup}", pluginPath, backupPath);
                return Result<string>.Success(backupPath);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to set ESL flag on {Plugin}", pluginPath);
            return Result<string>.Failure($"Unable to set ESL flag: {ex.Message}");
        }
    }

    public Task<Result<EslAnalysisResult>> AnalyzeAsync(string pluginPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(pluginPath) || !File.Exists(pluginPath))
                return Result<EslAnalysisResult>.Failure("Plugin file not found.");

            var extension = Path.GetExtension(pluginPath);
            if (!new[] { ".esp", ".esm", ".esl" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return Result<EslAnalysisResult>.Failure("Expected an ESP, ESM or ESL plugin.");

            try
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
                var headerVersion = mod.ModHeader.Stats.Version;
                // FormID 0 is reserved; version 1.71 permits the lower range.
                var minimumFormId = headerVersion >= 1.71f ? 0x001u : 0x800u;
                var ownRecordCount = 0;
                var problematicRecordCount = 0;
                foreach (var record in mod.EnumerateMajorRecords())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A record with the reserved null FormID has no owning ModKey in Mutagen.
                    // Include it as problematic rather than silently treating it as an override.
                    if (record.FormKey != FormKey.Null && record.FormKey.ModKey != mod.ModKey)
                        continue;

                    ownRecordCount++;
                    if (record.FormKey.ID < minimumFormId || record.FormKey.ID > 0xFFF)
                        problematicRecordCount++;
                }

                var reasons = new List<string>();
                if (ownRecordCount > 4096)
                    reasons.Add($"Own record count {ownRecordCount} exceeds the ESL limit of 4096.");
                if (problematicRecordCount > 0)
                    reasons.Add($"{problematicRecordCount} own records have FormIDs outside 0x{minimumFormId:X3}-0xFFF (header {headerVersion:0.00}).");

                var analysis = new EslAnalysisResult
                {
                    IsCompatible = reasons.Count == 0,
                    OwnRecordCount = ownRecordCount,
                    ProblematicRecordCount = problematicRecordCount,
                    HeaderVersion = headerVersion,
                    IsAlreadyEsl = mod.IsSmallMaster,
                    Reasons = reasons
                };
                _logger?.LogInformation("ESL analysis of {Plugin}: compatible={Compatible}, own records={Count}, problematic={Problems}",
                    pluginPath, analysis.IsCompatible, ownRecordCount, problematicRecordCount);
                return Result<EslAnalysisResult>.Success(analysis);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to analyze plugin {Plugin}", pluginPath);
                return Result<EslAnalysisResult>.Failure($"Unable to analyze plugin: {ex.Message}");
            }
        }, cancellationToken);
    }
}
