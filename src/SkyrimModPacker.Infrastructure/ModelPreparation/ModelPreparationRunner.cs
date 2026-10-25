using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

/// <summary>Independent experimental export; never edits a mod project or the game.</summary>
public static class ModelPreparationRunner
{
    private static readonly (string Source, string Output)[] Templates =
    [
        (@"meshes\weapons\iron\longsword.nif", "rapier.nif"),
        (@"meshes\weapons\iron\1stpersonlongsword.nif", "rapier_1stperson.nif")
    ];

    public static async Task<string> RunAsync(string archivePath, string gameDataDirectory,
        string blenderPath, string addonParent, string outputDirectory, bool includeScabbard,
        CancellationToken cancellationToken = default, string? scriptPath = null,
        ModelPreparationAdjustments? adjustments = null)
    {
        using var draft = await PrepareDraftAsync(archivePath, gameDataDirectory, blenderPath,
            addonParent, outputDirectory, includeScabbard, cancellationToken, scriptPath, adjustments);
        cancellationToken.ThrowIfCancellationRequested();
        return draft.Publish();
    }

    public static async Task<ModelPreparationDraft> PrepareDraftAsync(string archivePath, string gameDataDirectory,
        string blenderPath, string addonParent, string outputDirectory, bool includeScabbard,
        CancellationToken cancellationToken = default, string? scriptPath = null,
        ModelPreparationAdjustments? adjustments = null)
    {
        adjustments ??= new ModelPreparationAdjustments();
        adjustments.Validate();
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Choose an output folder.");
        foreach (var path in new[] { archivePath, blenderPath })
            if (!File.Exists(path)) throw new FileNotFoundException("Required file was not found", path);
        if (!Directory.Exists(gameDataDirectory)) throw new DirectoryNotFoundException(gameDataDirectory);
        if (!Directory.Exists(Path.Combine(addonParent, "io_scene_nifly")))
            throw new DirectoryNotFoundException("PyNifly add-on folder was not found: " + addonParent);
        if (!archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The current exporter accepts ZIP with one FBX model.");
        var fullOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var fullData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDirectory));
        if (fullOutput.Equals(fullData, StringComparison.OrdinalIgnoreCase) ||
            fullOutput.StartsWith(fullData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an output folder outside the game Data folder.");
        var script = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "ModelPreparation", "model_preparation_export_probe.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Model preparation script was not packaged", script);
        var inspectScript = Path.Combine(Path.GetDirectoryName(script)!, "model_preparation_template_inspect.py");
        if (!File.Exists(inspectScript)) throw new FileNotFoundException("NIF inspection script was not packaged", inspectScript);
        PackingPathGuard.EnsureNoLinks(archivePath);
        PackingPathGuard.EnsureNoLinks(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var staging = Path.Combine(outputDirectory, ".model-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var meshFolder = Path.Combine(staging, "meshes", "rapier");
            Directory.CreateDirectory(meshFolder);
            var adjustmentsPath = Path.Combine(staging, "adjustments.json");
            await File.WriteAllTextAsync(adjustmentsPath,
                JsonSerializer.Serialize(adjustments, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            var reports = new List<JsonElement>();
            var hashes = new Dictionary<string, string>();
            foreach (var (relativePath, outputName) in Templates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var template = Path.Combine(staging, "template-" + outputName);
                ExtractVanillaTemplate(gameDataDirectory, relativePath, template);
                var output = Path.Combine(meshFolder, outputName);
                var report = Path.Combine(staging, outputName + ".json");
                await RunBlenderAsync(blenderPath, script, cancellationToken, addonParent, archivePath,
                    template, output, report, includeScabbard ? "keep" : "omit", adjustmentsPath);
                Validate(output, report, includeScabbard);
                var inspection = Path.Combine(staging, "inspection-" + outputName + ".json");
                await RunBlenderAsync(blenderPath, inspectScript, cancellationToken, addonParent, output, inspection);
                ValidateReimport(inspection, report, includeScabbard);
                hashes["meshes/rapier/" + outputName] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
                var normalized = JsonNode.Parse(await File.ReadAllTextAsync(report, cancellationToken))!.AsObject();
                normalized["template"] = relativePath;
                normalized["output_nif"] = "meshes/rapier/" + outputName;
                await File.WriteAllTextAsync(report, normalized.ToJsonString(new JsonSerializerOptions
                    { WriteIndented = true }), cancellationToken);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report, cancellationToken));
                reports.Add(document.RootElement.Clone());
                File.Delete(template);
            }
            var summary = new
            {
                status = "experimental; Skyrim game test pending",
                source = archivePath,
                adjustments,
                scabbard = includeScabbard ? "fitted to blade" : "omitted",
                blood = "BloodEffects and BloodLighting fitted to blade geometry",
                limitations = new[] { "Game appearance and collision behavior not verified", "Textures not converted or copied" },
                views = reports,
                sha256 = hashes
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "report.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            return new ModelPreparationDraft(staging, outputDirectory);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    private static void ExtractVanillaTemplate(string dataDirectory, string relativePath, string target)
    {
        foreach (var bsa in Directory.EnumerateFiles(dataDirectory, "Skyrim - Meshes*.bsa")
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
            try
            {
                if (!archive.TryGetFolder(Path.GetDirectoryName(relativePath)!, out var folder)) continue;
                var entry = folder.Files.FirstOrDefault(file =>
                    string.Equals(file.Path.Replace('/', '\\'), relativePath, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                using var source = entry.AsStream();
                using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                source.CopyTo(destination);
                return;
            }
            finally { (archive as IDisposable)?.Dispose(); }
        }
        throw new FileNotFoundException("Vanilla Skyrim SE template was not found in the game archives", relativePath);
    }

    private static async Task RunBlenderAsync(string blender, string script,
        CancellationToken cancellationToken, params string[] scriptArguments)
    {
        var start = new ProcessStartInfo(blender) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-b", "--factory-startup", "--python", script, "--" }
                     .Concat(scriptArguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Blender did not start");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(shutdown.Token); }
            catch (OperationCanceledException) { }
            throw;
        }
        var outputText = await stdout;
        var errorText = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Blender model preparation failed: " +
                (outputText + "\n" + errorText)[^Math.Min(3000, outputText.Length + errorText.Length + 1)..]);
    }

    private static void ValidateReimport(string inspectionPath, string exportReportPath, bool includeScabbard)
    {
        using var imported = JsonDocument.Parse(File.ReadAllText(inspectionPath));
        using var exported = JsonDocument.Parse(File.ReadAllText(exportReportPath));
        var objects = imported.RootElement.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!, StringComparer.Ordinal);
        foreach (var name in new[] { "BloodEffects", "BloodLighting" })
        {
            if (!objects.TryGetValue(name, out var layer) || layer.GetProperty("vertices").GetInt32() < 1 ||
                layer.GetProperty("uv_layers").GetInt32() < 1)
                throw new InvalidDataException("Reimported NIF lacks usable " + name);
        }
        if (objects.ContainsKey("Scb") != includeScabbard)
            throw new InvalidDataException("Reimported NIF has unexpected scabbard state");
        if (includeScabbard && objects["Scb"].GetProperty("vertices").GetInt32() < 1)
            throw new InvalidDataException("Reimported scabbard has no vertices");
        var expected = exported.RootElement.GetProperty("collision_bounds");
        foreach (var collider in expected.EnumerateObject())
        {
            if (!objects.TryGetValue(collider.Name, out var actual) ||
                actual.GetProperty("rigid_body").GetProperty("collision_shape").GetString() != "CONVEX_HULL")
                throw new InvalidDataException("Reimported NIF lacks collision box " + collider.Name);
            for (var axis = 0; axis < 3; axis++)
            {
                var wantMin = collider.Value.GetProperty("after")[0][axis].GetDouble();
                var wantMax = collider.Value.GetProperty("after")[1][axis].GetDouble();
                var gotMin = actual.GetProperty("bounds_min")[axis].GetDouble();
                var gotMax = actual.GetProperty("bounds_max")[axis].GetDouble();
                if (Math.Abs(wantMin - gotMin) > 0.02 || Math.Abs(wantMax - gotMax) > 0.02)
                    throw new InvalidDataException("Collision bounds changed after NIF export: " + collider.Name);
            }
        }
        if (expected.EnumerateObject().Count() != 3)
            throw new InvalidDataException("Expected three fitted collision boxes");
    }

    private static void Validate(string nif, string reportPath, bool includeScabbard)
    {
        if (NifCompatibilityInspector.Read(nif).Family != NifFormatFamily.SkyrimSe)
            throw new InvalidDataException("Exported NIF is not Skyrim SE format: " + nif);
        var bytes = File.ReadAllBytes(nif);
        var scene = SseNifReader.Read(bytes);
        if (scene.Meshes.Count == 0 || scene.Meshes.Sum(mesh => mesh.Indices.Length) == 0)
            throw new InvalidDataException("Exported NIF has no visible weapon geometry: " + nif);
        var strings = Encoding.Latin1.GetString(bytes);
        foreach (var name in new[] { "BloodEffects", "BloodLighting" })
            if (!strings.Contains(name, StringComparison.Ordinal))
                throw new InvalidDataException("Exported NIF lacks " + name);
        if (strings.Contains("Scb", StringComparison.Ordinal) != includeScabbard)
            throw new InvalidDataException("Exported NIF scabbard state differs from the selected option");
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var blood = report.RootElement.GetProperty("blood_geometry");
        foreach (var name in new[] { "BloodEffects", "BloodLighting" })
            if (blood.GetProperty(name).GetProperty("polygons").GetInt32() < 1)
                throw new InvalidDataException("No fitted geometry for " + name);
        if (includeScabbard && report.RootElement.GetProperty("scabbard_geometry")
                .GetProperty("polygons").GetInt32() < 1)
            throw new InvalidDataException("Fitted scabbard is empty");
    }
}
