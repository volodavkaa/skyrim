using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

/// <summary>Independent experimental export; never edits a mod project or the game.</summary>
public static class ModelPreparationRunner
{
    public static async Task<string> RunAsync(string archivePath, string gameDataDirectory,
        string blenderPath, string addonParent, string outputDirectory, bool includeScabbard,
        CancellationToken cancellationToken = default, string? scriptPath = null,
        ModelPreparationAdjustments? adjustments = null, string weaponType = "sword1h",
        ModelPreparationTextureOptions? textures = null)
    {
        using var draft = await PrepareDraftAsync(archivePath, gameDataDirectory, blenderPath,
            addonParent, outputDirectory, includeScabbard, cancellationToken, scriptPath, adjustments, weaponType, textures);
        cancellationToken.ThrowIfCancellationRequested();
        return draft.Publish();
    }

    public static async Task<ModelPreparationDraft> PrepareDraftAsync(string archivePath, string gameDataDirectory,
        string blenderPath, string addonParent, string outputDirectory, bool includeScabbard,
        CancellationToken cancellationToken = default, string? scriptPath = null,
        ModelPreparationAdjustments? adjustments = null, string weaponType = "sword1h",
        ModelPreparationTextureOptions? textures = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = WeaponTemplateCatalog.GetExportable(weaponType);
        if (includeScabbard && !definition.SupportsScabbard)
            throw new ArgumentException("Selected weapon template has no scabbard.");
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
            throw new ArgumentException("The current exporter accepts ZIP with one FBX or GLB model.");
        var fullOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var fullData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDirectory));
        if (fullOutput.Equals(fullData, StringComparison.OrdinalIgnoreCase) ||
            fullOutput.StartsWith(fullData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an output folder outside the game Data folder.");
        var script = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "ModelPreparation", "model_preparation_export_probe.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Model preparation script was not packaged", script);
        var inspectScript = Path.Combine(Path.GetDirectoryName(script)!, "model_preparation_template_inspect.py");
        if (!File.Exists(inspectScript)) throw new FileNotFoundException("NIF inspection script was not packaged", inspectScript);
        var materialScript = Path.Combine(Path.GetDirectoryName(script)!, "model_preparation_material_probe.py");
        if (!File.Exists(materialScript)) throw new FileNotFoundException("Material inspection script was not packaged", materialScript);
        PackingPathGuard.EnsureNoLinks(archivePath);
        PackingPathGuard.EnsureNoLinks(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var staging = Path.Combine(outputDirectory, ".model-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var slug = ModelSlug(archivePath);
            var meshFolder = Path.Combine(staging, "meshes", slug);
            Directory.CreateDirectory(meshFolder);
            var adjustmentsPath = Path.Combine(staging, "adjustments.json");
            await File.WriteAllTextAsync(adjustmentsPath,
                JsonSerializer.Serialize(adjustments, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            var materialReport = Path.Combine(staging, "source-materials.json");
            await RunBlenderAsync(blenderPath, materialScript, cancellationToken, archivePath, materialReport);
            if (!File.Exists(materialReport))
                throw new InvalidOperationException("Blender material inspection did not produce a report.");
            using var materialDocument = JsonDocument.Parse(await File.ReadAllTextAsync(materialReport, cancellationToken));
            var sourceMesh = materialDocument.RootElement.GetProperty("meshes").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("name").GetString()!.EndsWith("LOD0", StringComparison.OrdinalIgnoreCase));
            if (sourceMesh.ValueKind == JsonValueKind.Undefined)
                sourceMesh = materialDocument.RootElement.GetProperty("meshes").EnumerateArray().FirstOrDefault();
            if (sourceMesh.ValueKind != JsonValueKind.Object || sourceMesh.GetProperty("uv_layers").GetInt32() < 1)
                throw new InvalidDataException("Source weapon mesh needs UV coordinates for materials.");
            var sourceMaterialCount = sourceMesh.GetProperty("materials").GetArrayLength();
            if (sourceMaterialCount > 1)
                throw new InvalidDataException("Source weapon has multiple material slots; splitting them into NIF shapes is not supported yet.");
            if (textures != null && sourceMaterialCount != 1)
                throw new InvalidDataException("Textured export currently needs one source material slot.");
            var texturePaths = textures == null ? ((string Diffuse, string Normal)?)null :
                await ModelPreparationTextures.PrepareAsync(archivePath, staging, slug, textures, cancellationToken);
            var reports = new List<JsonElement>();
            var hashes = new Dictionary<string, string>();
            if (texturePaths != null)
                foreach (var path in new[] { texturePaths.Value.Diffuse, texturePaths.Value.Normal })
                    hashes[path] = Convert.ToHexString(SHA256.HashData(
                        File.ReadAllBytes(Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar)))));
            foreach (var (view, suffix) in new[]
                     { (definition.ThirdPerson, ".nif"), (definition.FirstPerson, "_1stperson.nif") })
            {
                var relativePath = view.Source;
                var outputName = slug + suffix;
                var relativeOutput = "meshes/" + slug + "/" + outputName;
                cancellationToken.ThrowIfCancellationRequested();
                var template = Path.Combine(staging, "template-" + outputName);
                ExtractVanillaTemplate(gameDataDirectory, relativePath, template);
                var output = Path.Combine(meshFolder, outputName);
                var report = Path.Combine(staging, outputName + ".json");
                var configPath = Path.Combine(staging, "template-config-" + outputName + ".json");
                await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
                {
                    weapon_mesh = view.WeaponMesh,
                    blood_effect = view.BloodEffect,
                    blood_lighting = view.BloodLighting,
                    convex_collision = definition.ConvexCollision,
                    blood_mode = definition.BloodMode,
                    grip_from_collision = definition.GripFromCollision,
                    scabbard_fit = definition.ScabbardFit,
                    diffuse_texture = texturePaths?.Diffuse,
                    normal_texture = texturePaths?.Normal,
                    texture_root = texturePaths == null ? null : staging
                }), cancellationToken);
                await RunBlenderAsync(blenderPath, script, cancellationToken, addonParent, archivePath,
                    template, output, report, includeScabbard ? "keep" : "omit", adjustmentsPath, configPath);
                Validate(output, report, includeScabbard, view, texturePaths);
                var inspection = Path.Combine(staging, "inspection-" + outputName + ".json");
                await RunBlenderAsync(blenderPath, inspectScript, cancellationToken, addonParent, output, inspection);
                ValidateReimport(inspection, report, includeScabbard, view,
                    definition.ConvexCollision ? 1 : definition.CollisionBoxCount,
                    definition.BloodMode, definition.ScabbardFit);
                hashes[relativeOutput] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
                var normalized = JsonNode.Parse(await File.ReadAllTextAsync(report, cancellationToken))!.AsObject();
                normalized["template"] = relativePath;
                normalized["output_nif"] = relativeOutput;
                await File.WriteAllTextAsync(report, normalized.ToJsonString(new JsonSerializerOptions
                    { WriteIndented = true }), cancellationToken);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report, cancellationToken));
                reports.Add(document.RootElement.Clone());
                File.Delete(template);
            }
            var summary = new
            {
                status = "experimental; Skyrim game test pending",
                weapon_type = definition.Id,
                source = archivePath,
                model = slug,
                adjustments,
                scabbard = includeScabbard ? definition.ScabbardFit : "omitted",
                blood = "Template blood effect and lighting fitted to source geometry",
                source_materials = "source-materials.json",
                textures = texturePaths == null ? null : new { diffuse = texturePaths.Value.Diffuse,
                    normal = texturePaths.Value.Normal, invert_normal_y = textures!.InvertNormalY },
                limitations = texturePaths == null
                    ? new[] { "Game appearance and collision behavior not verified", "Textures not converted or copied" }
                    : new[] { "Game appearance and collision behavior not verified", "Texture appearance not game tested" },
                views = reports,
                sha256 = hashes
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "report.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            return new ModelPreparationDraft(staging, outputDirectory, slug,
                "meshes/" + slug + "/" + slug + ".nif",
                "meshes/" + slug + "/" + slug + "_1stperson.nif");
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    private static string ModelSlug(string archivePath)
    {
        var value = Regex.Replace(Path.GetFileNameWithoutExtension(archivePath).ToLowerInvariant(),
            "[^a-z0-9_-]+", "-").Trim('-', '_');
        if (value.Length > 40) value = value[..40].TrimEnd('-', '_');
        return string.IsNullOrEmpty(value) ? "weapon" : value;
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Blender did not start");
        using var registration = linked.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(shutdown.Token); }
            catch (OperationCanceledException) { }
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("Blender did not finish within 10 minutes.");
            throw;
        }
        var outputText = await stdout;
        var errorText = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Blender model preparation failed: " +
                (outputText + "\n" + errorText)[^Math.Min(3000, outputText.Length + errorText.Length + 1)..]);
    }

    private static void ValidateReimport(string inspectionPath, string exportReportPath, bool includeScabbard,
        WeaponTemplateView view, int collisionShapeCount, string bloodMode, string scabbardFit)
    {
        using var imported = JsonDocument.Parse(File.ReadAllText(inspectionPath));
        using var exported = JsonDocument.Parse(File.ReadAllText(exportReportPath));
        var objects = imported.RootElement.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var weapon = objects.Values.FirstOrDefault(item => item.GetProperty("name").GetString() == view.WeaponMesh &&
            item.GetProperty("type").GetString() == "MESH");
        if (weapon.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Reimported NIF lacks the weapon mesh");
        var weaponTop = weapon.GetProperty("bounds_max")[1].GetDouble();
        var bladeStart = exported.RootElement.GetProperty("blood_start_y").GetDouble();
        foreach (var name in new[] { view.BloodEffect, view.BloodLighting })
        {
            if (!objects.TryGetValue(name, out var layer) || layer.GetProperty("vertices").GetInt32() < 1 ||
                layer.GetProperty("uv_layers").GetInt32() < 1)
                throw new InvalidDataException("Reimported NIF lacks usable " + name);
            var layerBottom = layer.GetProperty("bounds_min")[1].GetDouble();
            var layerTop = layer.GetProperty("bounds_max")[1].GetDouble();
            if (layerBottom < bladeStart - 0.2 ||
                (bloodMode != "bounded_head" && layerTop < weaponTop - 0.3) ||
                layerTop > weaponTop + 0.3)
                throw new InvalidDataException("Blood layer does not follow the full blade: " + name);
        }
        if (objects.ContainsKey("Scb") != includeScabbard)
            throw new InvalidDataException("Reimported NIF has unexpected scabbard state");
        if (includeScabbard && objects["Scb"].GetProperty("vertices").GetInt32() < 1)
            throw new InvalidDataException("Reimported scabbard has no vertices");
        if (includeScabbard && scabbardFit == "blade")
        {
            var sheath = objects["Scb"];
            var blood = objects[view.BloodEffect];
            for (var axis = 0; axis < 3; axis += 2)
            {
                var sheathMin = sheath.GetProperty("bounds_min")[axis].GetDouble();
                var sheathMax = sheath.GetProperty("bounds_max")[axis].GetDouble();
                var bloodMin = blood.GetProperty("bounds_min")[axis].GetDouble();
                var bloodMax = blood.GetProperty("bounds_max")[axis].GetDouble();
                if (sheathMin > bloodMin + 0.05 || sheathMax < bloodMax - 0.05)
                    throw new InvalidDataException("Fitted scabbard does not enclose blade bounds");
            }
        }
        if (includeScabbard && scabbardFit == "template_scaled")
        {
            var expectedSheath = exported.RootElement.GetProperty("scabbard_bounds");
            var actualSheath = objects["Scb"];
            for (var axis = 0; axis < 3; axis++)
                if (Math.Abs(expectedSheath[0][axis].GetDouble() - actualSheath.GetProperty("bounds_min")[axis].GetDouble()) > 0.05 ||
                    Math.Abs(expectedSheath[1][axis].GetDouble() - actualSheath.GetProperty("bounds_max")[axis].GetDouble()) > 0.05)
                    throw new InvalidDataException("Fitted axe scabbard bounds changed after NIF export");
        }
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
        if (expected.EnumerateObject().Count() != collisionShapeCount)
            throw new InvalidDataException("Unexpected number of fitted collision shapes");
    }

    private static void Validate(string nif, string reportPath, bool includeScabbard,
        WeaponTemplateView view, (string Diffuse, string Normal)? textures)
    {
        if (NifCompatibilityInspector.Read(nif).Family != NifFormatFamily.SkyrimSe)
            throw new InvalidDataException("Exported NIF is not Skyrim SE format: " + nif);
        var bytes = File.ReadAllBytes(nif);
        var scene = SseNifReader.Read(bytes);
        if (scene.Meshes.Count == 0 || scene.Meshes.Sum(mesh => mesh.Indices.Length) == 0)
            throw new InvalidDataException("Exported NIF has no visible weapon geometry: " + nif);
        if (textures != null)
            foreach (var expected in new[] { textures.Value.Diffuse, textures.Value.Normal })
                if (!scene.TexturePaths.Any(path => path.Replace('\\', '/').Equals(expected,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Exported NIF does not reference generated texture: " + expected);
        var strings = Encoding.Latin1.GetString(bytes);
        foreach (var name in new[] { view.BloodEffect, view.BloodLighting })
            if (!strings.Contains(name, StringComparison.Ordinal))
                throw new InvalidDataException("Exported NIF lacks " + name);
        if (strings.Contains("Scb", StringComparison.Ordinal) != includeScabbard)
            throw new InvalidDataException("Exported NIF scabbard state differs from the selected option");
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var blood = report.RootElement.GetProperty("blood_geometry");
        foreach (var name in new[] { view.BloodEffect, view.BloodLighting })
            if (blood.GetProperty(name).GetProperty("polygons").GetInt32() < 1)
                throw new InvalidDataException("No fitted geometry for " + name);
        if (includeScabbard && report.RootElement.GetProperty("scabbard_geometry")
                .GetProperty("polygons").GetInt32() < 1)
            throw new InvalidDataException("Fitted scabbard is empty");
    }
}
