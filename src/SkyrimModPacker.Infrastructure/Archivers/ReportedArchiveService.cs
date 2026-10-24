using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.Archivers;

/// <summary>Verifies a payload ZIP, describes that check, then verifies and atomically publishes ZIP plus report.</summary>
public sealed class ReportedArchiveService(IPluginValidationService validator) : IReportedArchiveService
{
    public const string ReportEntry = "mod-report.json";
    public async Task CreateAsync(IArchiver archiver, PackingContext context, IReadOnlyList<(string SourcePath, string EntryPath)> files, CancellationToken token = default)
    {
        var output = PackingPathGuard.GetAbsolutePath(context.OutputArchivePath);
        PluginWriteGuard.EnsureOutsideGame(output, context.Settings.SkyrimDataPath);
        PackingPathGuard.EnsureNoLinks(output);
        var names = PackingPathGuard.ValidateEntries(files.Select(f => f.EntryPath));
        files = files.Select((f, i) => (f.SourcePath, EntryPath: names[i])).ToArray();
        if (files.Any(f => f.EntryPath.Equals(ReportEntry, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(f.SourcePath).Equals(output, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An archive source conflicts with the output or reserved report name.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var lease = await WorkspaceFileGate.AcquireAsync(output, token);
        var previous = new WorkspaceFileSnapshot(output);
        var stage = Path.Combine(Path.GetDirectoryName(output)!, ".report-" + Guid.NewGuid().ToString("N"));
        PluginWriteGuard.EnsureOutsideGame(stage, context.Settings.SkyrimDataPath);
        PackingPathGuard.EnsureNoLinks(stage);
        Directory.CreateDirectory(stage);
        try
        {
            var manifest = new List<ZipManifestEntry>();
            foreach (var file in files) manifest.Add(await ZipManifest.CaptureAsync(file.SourcePath, file.EntryPath, token));
            var records = new List<object>(); var recipes = new List<object>(); var models = new List<object>();
            var masters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var diagnostics = new List<PluginDiagnostic>();
            foreach (var file in files.Where(f => new[] { ".esp", ".esl", ".esm" }.Contains(Path.GetExtension(f.EntryPath), StringComparer.OrdinalIgnoreCase)))
            {
                diagnostics.AddRange(await validator.ValidateAsync(file.SourcePath, context.Settings.SkyrimDataPath, context.Profile.EnableEsl, cancellationToken: token));
                using var mod = SkyrimMod.CreateFromBinaryOverlay(file.SourcePath, SkyrimRelease.SkyrimSE);
                foreach (var master in mod.ModHeader.MasterReferences) masters.Add(master.Master.FileName.String);
                foreach (var record in mod.EnumerateMajorRecords())
                    records.Add(new { Plugin = file.EntryPath, FormKey = record.FormKey.ToString(), record.EditorID,
                        Type = record.GetType().Name.Replace("BinaryOverlay", ""), Origin = record.FormKey.ModKey == mod.ModKey ? "Created" : "Override" });
                foreach (var weapon in mod.Weapons)
                    models.Add(new { Weapon = weapon.FormKey.ToString(), ThirdPerson = weapon.Model?.File.GivenPath,
                        FirstPersonStatic = weapon.FirstPersonModel.FormKey.ToString() });
                foreach (var stat in mod.Statics) models.Add(new { Static = stat.FormKey.ToString(), Model = stat.Model?.File.GivenPath });
                foreach (var recipe in mod.ConstructibleObjects)
                    recipes.Add(new { FormKey = recipe.FormKey.ToString(), recipe.EditorID, CreatedObject = recipe.CreatedObject.FormKey.ToString(),
                        recipe.CreatedObjectCount, Workbench = recipe.WorkbenchKeyword.FormKey.ToString(),
                        Ingredients = recipe.Items?.Select(i => new { Item = i.Item.Item.FormKey.ToString(), i.Item.Count }).ToArray(),
                        Conditions = recipe.Conditions.Select(c => new { Type = c.Data.GetType().Name.Replace("BinaryOverlay", ""),
                            CompareOperator = c.CompareOperator.ToString(), Flags = c.Flags.ToString(),
                            Value = c is IConditionFloatGetter value ? (float?)value.ComparisonValue : null,
                            Perk = c.Data is IHasPerkConditionDataGetter perk ? perk.Perk.Link.FormKey.ToString() : null }).ToArray() });
            }
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidOperationException("Plugin validation failed while creating the export report.");
            var probe = Path.Combine(stage, "payload.zip");
            await archiver.CreateArchiveAsync(probe, files, cancellationToken: token);
            await ZipManifest.VerifyAsync(probe, manifest, token);
            var report = new
            {
                SchemaVersion = 1, CreatedUtc = DateTimeOffset.UtcNow, context.Profile.Name, context.Profile.Version,
                Records = records, Masters = masters.OrderBy(m => m).ToArray(), Models = models, Recipes = recipes,
                Textures = manifest.Where(m => m.EntryPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).Select(m => m.EntryPath).ToArray(),
                Files = manifest.Select(m => new { Path = m.EntryPath, m.Length, Sha256 = Convert.ToHexString(m.Hash) }).ToArray(),
                PluginValidation = new { Status = "Passed", Diagnostics = diagnostics },
                ZipValidation = new { PayloadStatus = "Passed", Checks = new[] { "Canonical paths", "Exact entry set", "Lengths", "SHA-256" },
                    Publication = "The final archive including this report must pass the same checks before atomic publication.", ReportHash = "Excluded from its own manifest to avoid a circular hash." },
                Warnings = context.Warnings.Concat(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).Select(d => d.ToString())).ToArray(),
                Unsupported = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Unsupported).ToArray(),
                SkippedChecks = new[] { "Skyrim runtime", "SSEEdit (not invoked by export)", "NIF geometry and internal DDS references", "DDS image contents" }
            };
            var reportPath = Path.Combine(stage, ReportEntry);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            manifest.Add(await ZipManifest.CaptureAsync(reportPath, ReportEntry, token));
            var finalFiles = files.Concat(new[] { (SourcePath: reportPath, EntryPath: ReportEntry) }).ToArray();
            var candidate = Path.Combine(stage, "complete.zip");
            await archiver.CreateArchiveAsync(candidate, finalFiles, context.Progress, token);
            var candidateHash = await ZipManifest.CaptureAsync(candidate, "", token);
            await ZipManifest.VerifyAsync(candidate, manifest, token);
            foreach (var entry in manifest)
            {
                var current = await ZipManifest.CaptureAsync(entry.SourcePath, entry.EntryPath, token);
                if (current.Length != entry.Length || !current.Hash.AsSpan().SequenceEqual(entry.Hash)) throw new IOException("An export source changed during packing.");
            }
            previous.EnsureUnchanged(); token.ThrowIfCancellationRequested();
            var publicationHash = await ZipManifest.CaptureAsync(candidate, "", token);
            if (publicationHash.Length != candidateHash.Length || !publicationHash.Hash.AsSpan().SequenceEqual(candidateHash.Hash))
                throw new IOException("The verified report archive changed before publication.");
            PackingPathGuard.EnsureNoLinks(output);
            if (File.Exists(output)) File.Replace(candidate, output, null); else File.Move(candidate, output);
            context.LogMessages.Add("Verified ZIP and embedded mod-report.json before publication.");
        }
        finally
        {
            // Only this operation's direct files; no recursive traversal or project cleanup.
            try
            {
                foreach (var file in Directory.EnumerateFiles(stage)) { PackingPathGuard.EnsureNoLinks(file); File.Delete(file); }
                Directory.Delete(stage);
            }
            catch (Exception ex) { context.Warnings.Add($"Export staging cleanup failed: {stage}: {ex.Message}"); }
        }
    }
}
