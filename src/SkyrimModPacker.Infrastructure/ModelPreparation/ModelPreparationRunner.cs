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
    public static async Task<string> InspectSourceMaterialsAsync(string archivePath, string blenderPath,
        string? addonParent = null, CancellationToken cancellationToken = default, string? scriptPath = null,
        string? modelEntry = null)
    {
        if (!File.Exists(archivePath) || !archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose a ZIP archive with one model", archivePath);
        if (!File.Exists(blenderPath)) throw new FileNotFoundException("Blender was not found", blenderPath);
        var script = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "ModelPreparation",
            "model_preparation_export_probe.py");
        var materialScript = Path.Combine(Path.GetDirectoryName(script)!, "model_preparation_material_probe.py");
        if (!File.Exists(materialScript)) throw new FileNotFoundException("Material inspection script was not packaged", materialScript);
        var report = Path.Combine(Path.GetTempPath(), "skyrim-material-audit-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await RunBlenderAsync(blenderPath, materialScript, cancellationToken,
                archivePath, report, addonParent ?? "", modelEntry ?? "");
            return await File.ReadAllTextAsync(report, cancellationToken);
        }
        finally { if (File.Exists(report)) File.Delete(report); }
    }

    public static async Task<string> RunAsync(string archivePath, string gameDataDirectory,
        string blenderPath, string addonParent, string outputDirectory, bool includeScabbard,
        CancellationToken cancellationToken = default, string? scriptPath = null,
        ModelPreparationAdjustments? adjustments = null, string weaponType = "sword1h",
        ModelPreparationTextureOptions? textures = null, string? modelEntry = null, bool defaultShader = false, double? vertexColor = null,
        ModelPreparationShaderSettings? shaderSettings = null)
    {
        using var draft = await PrepareDraftAsync(archivePath, gameDataDirectory, blenderPath,
            addonParent, outputDirectory, includeScabbard, cancellationToken, scriptPath, adjustments,
            weaponType, textures, modelEntry, defaultShader, vertexColor, shaderSettings);
        cancellationToken.ThrowIfCancellationRequested();
        return draft.Publish();
    }

    public static async Task<ModelPreparationDraft> PrepareDraftAsync(string archivePath, string gameDataDirectory,
        string blenderPath, string addonParent, string outputDirectory, bool includeScabbard,
        CancellationToken cancellationToken = default, string? scriptPath = null,
        ModelPreparationAdjustments? adjustments = null, string weaponType = "sword1h",
        ModelPreparationTextureOptions? textures = null, string? modelEntry = null, bool defaultShader = false, double? vertexColor = null,
        ModelPreparationShaderSettings? shaderSettings = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (vertexColor is double color && (!double.IsFinite(color) || color is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(vertexColor));
        shaderSettings?.Validate();
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
            throw new ArgumentException("The current exporter accepts ZIP with one BLEND, FBX, OBJ, GLB, glTF or NIF model.");
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
            await RunBlenderAsync(blenderPath, materialScript, cancellationToken,
                archivePath, materialReport, addonParent, modelEntry ?? "");
            if (!File.Exists(materialReport))
                throw new InvalidOperationException("Blender material inspection did not produce a report.");
            using var materialDocument = JsonDocument.Parse(await File.ReadAllTextAsync(materialReport, cancellationToken));
            var selectedMesh = materialDocument.RootElement.GetProperty("selected_mesh").GetString();
            var sourceMesh = materialDocument.RootElement.GetProperty("meshes").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("name").GetString() == selectedMesh);
            if (sourceMesh.ValueKind != JsonValueKind.Object || sourceMesh.GetProperty("uv_layers").GetInt32() < 1)
                throw new InvalidDataException("Source weapon mesh needs UV coordinates for materials.");
            var sourceMaterialCount = sourceMesh.GetProperty("materials").GetArrayLength();
            if (sourceMaterialCount > 8)
                throw new InvalidDataException("At most eight source material slots are supported.");
            if (sourceMaterialCount > 1 && !sourceMesh.GetProperty("polygon_material_indices").EnumerateArray()
                .Select(item => item.GetInt32()).ToHashSet().SetEquals(Enumerable.Range(0, sourceMaterialCount)))
                throw new InvalidDataException("Every source material slot must be used by weapon faces.");
            if (sourceMaterialCount > 1 && (textures?.Materials == null ||
                textures.Materials.Count != sourceMaterialCount ||
                textures.Materials.Select(item => item.Slot).Distinct().Count() != sourceMaterialCount ||
                textures.Materials.Any(item => item.Slot < 0 || item.Slot >= sourceMaterialCount)))
                throw new InvalidDataException("Every source material slot needs its own BaseColor and Normal ZIP entries.");
            if (sourceMaterialCount <= 1 && textures?.Materials is { Count: > 0 })
                throw new InvalidDataException("Per-slot texture mapping requires multiple source materials.");
            var texturePaths = new List<PreparedMaterialTextures>();
            if (defaultShader && textures != null)
                textures = textures with { MetallicEntry = null, CubemapEntry = null, GameCubemapPath = null,
                    Materials = textures.Materials?.Select(item => item with
                        { MetallicEntry = null, CubemapEntry = null }).ToArray() };
            if (textures != null)
            {
                if (sourceMaterialCount > 1)
                {
                    foreach (var item in textures.Materials!.OrderBy(item => item.Slot))
                        texturePaths.Add(await ModelPreparationTextures.PrepareAsync(archivePath, staging, slug,
                            new ModelPreparationTextureOptions(item.DiffuseEntry, item.NormalEntry,
                                textures.TexconvPath, textures.InvertNormalY, null,
                                item.MetallicEntry, item.CubemapEntry, textures.GameCubemapPath, textures.MaxTextureSize),
                            cancellationToken, gameDataDirectory, item.Slot));
                }
                else
                    texturePaths.Add(await ModelPreparationTextures.PrepareAsync(archivePath, staging, slug,
                        textures, cancellationToken, gameDataDirectory));
            }
            var reports = new List<JsonElement>();
            var hashes = new Dictionary<string, string>();
            foreach (var pair in texturePaths)
                foreach (var path in new[] { pair.Diffuse, pair.Normal, pair.EnvMask, pair.EnvMap }
                             .OfType<string>())
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
                    default_shader = defaultShader,
                    vertex_color = vertexColor,
                    environment_strength = defaultShader ? null : shaderSettings?.EnvironmentStrength,
                    specular_strength = shaderSettings?.SpecularStrength,
                    specular_color = shaderSettings?.SpecularStrength == null ? null : new[] { shaderSettings.Red, shaderSettings.Green, shaderSettings.Blue },
                    diffuse_texture = texturePaths.Count == 1 ? texturePaths[0].Diffuse : null,
                    normal_texture = texturePaths.Count == 1 ? texturePaths[0].Normal : null,
                    env_mask_texture = texturePaths.Count == 1 ? texturePaths[0].EnvMask : null,
                    env_map_texture = texturePaths.Count == 1 ? texturePaths[0].EnvMap : null,
                    material_textures = texturePaths.Count > 1 ? texturePaths.Select(pair => new
                        { diffuse_texture = pair.Diffuse, normal_texture = pair.Normal,
                          env_mask_texture = pair.EnvMask, env_map_texture = pair.EnvMap }).ToArray() : null,
                    texture_root = texturePaths.Count == 0 ? null : staging,
                    selected_model_entry = modelEntry
                }), cancellationToken);
                await RunBlenderAsync(blenderPath, script, cancellationToken, addonParent, archivePath,
                    template, output, report, includeScabbard ? "keep" : "omit", adjustmentsPath, configPath);
                Validate(output, report, includeScabbard, view, texturePaths);
                var inspection = Path.Combine(staging, "inspection-" + outputName + ".json");
                await RunBlenderAsync(blenderPath, inspectScript, cancellationToken, addonParent, output, inspection);
                ValidateReimport(inspection, report, includeScabbard, view,
                    definition.ConvexCollision ? 1 : definition.CollisionBoxCount,
                    definition.BloodMode, definition.ScabbardFit, texturePaths, defaultShader, vertexColor, shaderSettings);
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
                weapon_shader = defaultShader ? "Default" : "template",
                vertex_color = vertexColor,
                shader_settings = shaderSettings,
                source = archivePath,
                model = slug,
                adjustments,
                scabbard = includeScabbard ? definition.ScabbardFit : "omitted",
                blood = "Template blood effect and lighting fitted to source geometry",
                source_materials = "source-materials.json",
                textures = texturePaths.Count == 0 ? null : new
                {
                    diffuse = texturePaths.Count == 1 ? texturePaths[0].Diffuse : null,
                    normal = texturePaths.Count == 1 ? texturePaths[0].Normal : null,
                    env_mask = texturePaths.Count == 1 ? texturePaths[0].EnvMask : null,
                    env_map = texturePaths.Count == 1 ? texturePaths[0].EnvMap : null,
                    cubemap_fallback = texturePaths.Any(pair => pair.EnvMask != null && pair.EnvMap == null)
                        ? "vanilla template" : null,
                    material_slots = texturePaths.Select(pair => new
                    { diffuse = pair.Diffuse, normal = pair.Normal,
                      env_mask = pair.EnvMask, env_map = pair.EnvMap }).ToArray(),
                    invert_normal_y = textures!.InvertNormalY,
                    max_texture_size = textures.MaxTextureSize
                },
                limitations = texturePaths.Count == 0
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
            await ModelPreparationTextures.DeleteTemporaryDirectoryAsync(staging);
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

    internal static async Task RunBlenderAsync(string blender, string script,
        CancellationToken cancellationToken, params string[] scriptArguments)
    {
        var start = new ProcessStartInfo(blender) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-b", "--factory-startup", "--disable-autoexec", "--python", script, "--" }
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
        WeaponTemplateView view, int collisionShapeCount, string bloodMode, string scabbardFit,
        IReadOnlyList<PreparedMaterialTextures> textures, bool defaultShader, double? vertexColor,
        ModelPreparationShaderSettings? shaderSettings)
    {
        using var imported = JsonDocument.Parse(File.ReadAllText(inspectionPath));
        using var exported = JsonDocument.Parse(File.ReadAllText(exportReportPath));
        var objects = imported.RootElement.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var weapon = objects.Values.FirstOrDefault(item => item.GetProperty("name").GetString() == view.WeaponMesh &&
            item.GetProperty("type").GetString() == "MESH");
        if (weapon.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Reimported NIF lacks the weapon mesh");
        var partNames = exported.RootElement.GetProperty("weapon_parts").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!).ToArray();
        foreach (var name in partNames)
            if (!objects.TryGetValue(name, out var part) || part.GetProperty("type").GetString() != "MESH" ||
                part.GetProperty("vertices").GetInt32() < 1 || part.GetProperty("uv_layers").GetInt32() < 1)
                throw new InvalidDataException("Reimported NIF lacks usable weapon material shape: " + name);
        if (defaultShader)
            foreach (var name in partNames)
            {
                var materials = objects[name].GetProperty("material_nodes").EnumerateArray().ToArray();
                if (materials.Length != 1 || materials[0].GetProperty("shader_type").GetString() != "Default" ||
                    materials[0].GetProperty("shader_flags1").GetString()!.Split('|')
                        .Any(flag => flag.Trim() == "ENVIRONMENT_MAPPING"))
                    throw new InvalidDataException("Reimported weapon does not have the Default shader: " + name +
                        "; materials: " + string.Join(", ", materials.Select(material =>
                            material.GetProperty("shader_type").GetString() + " / " + material.GetProperty("shader_flags1").GetString())));
            }
        if (shaderSettings != null)
            foreach (var name in partNames)
            {
                var materials = objects[name].GetProperty("material_nodes").EnumerateArray().ToArray();
                if (materials.Length != 1) throw new InvalidDataException("Missing weapon material: " + name);
                var material = materials[0];
                if (!defaultShader && shaderSettings.EnvironmentStrength is double env &&
                    material.GetProperty("shader_type").GetString() == "Environment_Map" &&
                    Math.Abs(material.GetProperty("environment_strength").GetDouble() - env) > 0.001)
                    throw new InvalidDataException("Reimported environment strength differs: " + name);
                if (shaderSettings.SpecularStrength is double spec)
                {
                    if (Math.Abs(material.GetProperty("specular_strength").GetDouble() - spec) > 0.001 ||
                        !material.GetProperty("shader_flags1").GetString()!.Split('|').Any(flag => flag.Trim() == "SPECULAR"))
                        throw new InvalidDataException("Reimported specular strength differs: " + name);
                    var actual = material.GetProperty("specular_color");
                    var expectedRgb = new[] { shaderSettings.Red, shaderSettings.Green, shaderSettings.Blue };
                    for (var channel = 0; channel < 3; channel++)
                        if (Math.Abs(actual[channel].GetDouble() - expectedRgb[channel]) > 0.001)
                            throw new InvalidDataException("Reimported specular color differs: " + name);
                }
            }
        if (vertexColor is double expectedColor)
            foreach (var name in partNames.Concat(includeScabbard ? new[] { "Scb" } : Array.Empty<string>()))
            {
                var part = objects[name];
                var color = part.GetProperty("vertex_color");
                if (color.ValueKind != JsonValueKind.Object ||
                    color.GetProperty("count").GetInt32() == 0 ||
                    Math.Abs(color.GetProperty("min").GetDouble() - expectedColor) > 0.03 ||
                    Math.Abs(color.GetProperty("max").GetDouble() - expectedColor) > 0.03)
                    throw new InvalidDataException("Reimported NIF has wrong vertex color: " + name);
                var materials = part.GetProperty("material_nodes").EnumerateArray().ToArray();
                if (materials.Length != 1 || !materials[0].GetProperty("shader_flags2").GetString()!
                        .Split('|').Any(flag => flag.Trim() == "VERTEX_COLORS"))
                    throw new InvalidDataException("Reimported NIF does not enable vertex colors: " + name);
            }
        if (textures.Count != 0 && textures.Count != partNames.Length)
            throw new InvalidDataException("Weapon material shape and texture mapping counts differ");
        for (var slot = 0; slot < textures.Count; slot++)
        {
            var materials = objects[partNames[slot]].GetProperty("material_nodes").EnumerateArray().ToArray();
            var nodes = materials
                .SelectMany(material => material.GetProperty("nodes").EnumerateArray())
                .ToArray();
            foreach (var (nodeName, texturePath) in new[]
                     { ("Diffuse_Texture", textures[slot].Diffuse), ("Normal_Texture", textures[slot].Normal) })
            {
                var image = nodes.FirstOrDefault(node => node.GetProperty("name").GetString() == nodeName);
                var path = image.ValueKind == JsonValueKind.Object &&
                    image.GetProperty("image").ValueKind == JsonValueKind.String
                    ? image.GetProperty("image").GetString()!.Replace('\\', '/') : "";
                if (!path.EndsWith(texturePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Reimported material shape has wrong " + nodeName + ": " + partNames[slot]);
            }
            if (textures[slot].EnvMask != null)
            {
                if (materials.Length != 1 ||
                    materials[0].GetProperty("shader_type").GetString() != "Environment_Map" ||
                    !materials[0].GetProperty("shader_flags1").GetString()!
                        .Contains("ENVIRONMENT_MAPPING", StringComparison.Ordinal))
                    throw new InvalidDataException("Reimported weapon lost Environment Map shader: " + partNames[slot]);
                var slots = materials[0].GetProperty("texture_paths");
                var mask = slots.GetProperty("EnvMask").GetString()!.Replace('\\', '/');
                var cube = slots.GetProperty("EnvMap").GetString()!.Replace('\\', '/');
                var expectedCube = exported.RootElement.GetProperty("weapon_textures")[slot]
                    .GetProperty("EnvMap").GetString();
                if (!mask.Equals(textures[slot].EnvMask, StringComparison.OrdinalIgnoreCase) ||
                    !cube.Equals(expectedCube, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Reimported weapon lost environment mask or cubemap: " + partNames[slot]);
            }
        }
        var weaponTop = exported.RootElement.GetProperty("exported_weapon_bounds")[1][1].GetDouble();
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
        WeaponTemplateView view, IReadOnlyList<PreparedMaterialTextures> textures)
    {
        if (NifCompatibilityInspector.Read(nif).Family != NifFormatFamily.SkyrimSe)
            throw new InvalidDataException("Exported NIF is not Skyrim SE format: " + nif);
        var bytes = File.ReadAllBytes(nif);
        var scene = SseNifReader.Read(bytes);
        if (scene.Meshes.Count == 0 || scene.Meshes.Sum(mesh => mesh.Indices.Length) == 0)
            throw new InvalidDataException("Exported NIF has no visible weapon geometry: " + nif);
        foreach (var pair in textures)
            foreach (var expected in new[] { pair.Diffuse, pair.Normal, pair.EnvMask, pair.EnvMap }
                         .OfType<string>())
                if (!scene.TexturePaths.Any(path => path.Replace('\\', '/').Equals(expected,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Exported NIF does not reference generated texture: " + expected);
        if (textures.Any(pair => pair.EnvMask != null))
        {
            using var materialReport = JsonDocument.Parse(File.ReadAllText(reportPath));
            foreach (var slot in materialReport.RootElement.GetProperty("weapon_textures").EnumerateArray())
            {
                if (slot.ValueKind != JsonValueKind.Object || !slot.TryGetProperty("EnvMap", out var cube))
                    continue;
                var expected = cube.GetString();
                if (string.IsNullOrWhiteSpace(expected) || !scene.TexturePaths.Any(path =>
                        path.Replace('\\', '/').Equals(expected, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Exported Environment Map shader has no cubemap");
            }
        }
        if (textures.Count > 1)
        {
            using var materialReport = JsonDocument.Parse(File.ReadAllText(reportPath));
            var parts = materialReport.RootElement.GetProperty("weapon_parts").EnumerateArray().ToArray();
            if (parts.Length != textures.Count)
                throw new InvalidDataException("Exported weapon material shape count differs from texture mapping");
            for (var slot = 0; slot < parts.Length; slot++)
            {
                var name = parts[slot].GetProperty("name").GetString();
                var mesh = scene.Meshes.FirstOrDefault(item => item.Name == name);
                if (mesh?.DiffuseTexture == null || !mesh.DiffuseTexture.Replace('\\', '/').Equals(
                        textures[slot].Diffuse, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Exported weapon shape has wrong diffuse texture: " + name);
            }
        }
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
