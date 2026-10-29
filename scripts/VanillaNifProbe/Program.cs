using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.Infrastructure.ModelPreparation;
using System.Globalization;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--find-animation")
{
    foreach (var bsa in Directory.EnumerateFiles(args[1], "Skyrim - Animations.bsa"))
    {
        var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
        try { foreach (var file in archive.Files.Where(file => file.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase)).Take(60)) Console.WriteLine(file.Path); }
        finally { (archive as IDisposable)?.Dispose(); }
    }
    return 0;
}

if (args.Length == 4 && args[0] == "--extract-raw")
{
    var relative = args[2].Replace('/', '\\');
    foreach (var bsa in Directory.EnumerateFiles(args[1], "Skyrim - *.bsa"))
    {
        var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
        try
        {
            if (!archive.TryGetFolder(Path.GetDirectoryName(relative)!, out var folder)) continue;
            var entry = folder.Files.FirstOrDefault(item => item.Path.Replace('/', '\\').Equals(relative, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            using var input = entry.AsStream(); using var output = File.Create(args[3]);
            input.CopyTo(output); Console.WriteLine(entry.Path); return 0;
        }
        finally { (archive as IDisposable)?.Dispose(); }
    }
    return 1;
}

if (args.Length == 2 && args[0] == "--list-cubemaps")
{
    foreach (var bsa in Directory.EnumerateFiles(args[1], "Skyrim - Textures*.bsa"))
    {
        var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
        foreach (var file in archive.Files.Where(file =>
                     file.Path.Contains("cubemaps", StringComparison.OrdinalIgnoreCase) &&
                     file.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(Path.GetFileName(bsa) + "|" + file.Path);
        (archive as IDisposable)?.Dispose();
    }
    return 0;
}

if (args.Length == 3 && args[0] == "--extract-cubemaps")
{
    Directory.CreateDirectory(args[2]);
    foreach (var item in GameCubemapCatalog.Choices.Where(item => item.Path.Length > 0))
    {
        var target = Path.Combine(args[2], Path.GetFileName(item.Path));
        File.WriteAllBytes(target, GameCubemapCatalog.Read(args[1], item.Path));
        Console.WriteLine(item.Name + "|" + target + "|" + new FileInfo(target).Length);
    }
    return 0;
}

if (args.Length == 2 && args[0] == "--decode-cubemaps")
{
    foreach (var file in Directory.EnumerateFiles(args[1], "*.dds"))
    {
        var faces = DdsCubemapPreviewDecoder.Decode(file, File.ReadAllBytes(file));
        Console.WriteLine(Path.GetFileName(file) + "|" + faces.Count + "|" + faces[0].Width + "x" + faces[0].Height);
    }
    return 0;
}

if (args.Length == 2 && args[0] == "--decode-cubemap")
{
    var faces = DdsCubemapPreviewDecoder.Decode(args[1], File.ReadAllBytes(args[1]));
    Console.WriteLine(Path.GetFileName(args[1]) + "|" + faces.Count + "|" + faces[0].Width + "x" + faces[0].Height);
    return 0;
}

if (args.Length == 4 && args[0] == "--extract-texture")
{
    var relative = args[2].Replace('/', Path.DirectorySeparatorChar);
    foreach (var bsa in Directory.EnumerateFiles(args[1], "Skyrim - Textures*.bsa"))
    {
        var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
        try
        {
            if (!archive.TryGetFolder(Path.GetDirectoryName(relative)!, out var folder)) continue;
            var entry = folder.Files.FirstOrDefault(file => file.Path.Replace('/', Path.DirectorySeparatorChar)
                .Equals(relative, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            using var stream = entry.AsStream(); using var output = File.Create(args[3]);
            stream.CopyTo(output); Console.WriteLine(bsa + "|" + entry.Path);
            return 0;
        }
        finally { (archive as IDisposable)?.Dispose(); }
    }
    return 1;
}

if (args.Length is 4 or 5 or 6 && args[0] == "--inspect-materials")
{
    var report = await ModelPreparationRunner.InspectSourceMaterialsAsync(args[1], args[2],
        addonParent: args.Length >= 5 ? args[4] : null, scriptPath: args[3],
        modelEntry: args.Length == 6 ? args[5] : null);
    using var document = JsonDocument.Parse(report);
    foreach (var mesh in document.RootElement.GetProperty("meshes").EnumerateArray())
        Console.WriteLine(mesh.GetProperty("name").GetString() + ": " +
            mesh.GetProperty("materials").GetArrayLength() + " material slots");
    return 0;
}

if ((args.Length is 8 or 9 or 12 or 13 or 15 or 16 or 17 &&
     args[0] is "--prepare" or "--prepare-draft" or "--prepare-discard") ||
    (args.Length == 11 && args[0] is "--prepare-multi-discard" or "--prepare-multi-draft") ||
    (args.Length == 14 && args[0] is "--prepare-env-discard" or "--prepare-env-draft") ||
    (args.Length == 15 && args[0] is "--prepare-env-game-discard" or "--prepare-env-game-draft") ||
    (args.Length == 10 && args[0] == "--prepare-model-discard") ||
    (args.Length == 12 && args[0] == "--prepare-multi-model-discard"))
{
    var weaponType = args[0].StartsWith("--prepare-env", StringComparison.Ordinal) ||
        args.Length is 9 or 10 or 11 or 12 or 13 or 14 ? args[8] : args.Length == 17 ? args[^1] : "sword1h";
    var modelEntry = args[0] == "--prepare-model-discard" ? args[9] :
        args[0] == "--prepare-multi-model-discard" ? args[11] : null;
    var textures = args[0] is "--prepare-multi-discard" or "--prepare-multi-draft" or "--prepare-multi-model-discard"
        ? new ModelPreparationTextureOptions("", "", args[10], false,
            JsonSerializer.Deserialize<List<ModelPreparationMaterialTexture>>(File.ReadAllText(args[9]))!)
        : args[0] is "--prepare-env-discard" or "--prepare-env-draft" or
            "--prepare-env-game-discard" or "--prepare-env-game-draft"
        ? new ModelPreparationTextureOptions(args[9], args[10], args[11], false,
            null, args[12], args[13] == "-" ? null : args[13],
            args.Length == 15 ? args[14] : null)
        : args.Length is 12 or 13
        ? new ModelPreparationTextureOptions(args[9], args[10], args[11], args.Length == 13 && args[12] == "invert")
        : null;
    var adjustments = args.Length >= 15 && !args[0].StartsWith("--prepare-env", StringComparison.Ordinal)
        ? new ModelPreparationAdjustments(
            double.Parse(args[8], CultureInfo.InvariantCulture),
            double.Parse(args[9], CultureInfo.InvariantCulture),
            double.Parse(args[10], CultureInfo.InvariantCulture),
            double.Parse(args[11], CultureInfo.InvariantCulture),
            double.Parse(args[12], CultureInfo.InvariantCulture),
            double.Parse(args[13], CultureInfo.InvariantCulture),
            double.Parse(args[14], CultureInfo.InvariantCulture),
            args.Length >= 16 ? double.Parse(args[15], CultureInfo.InvariantCulture) : 0.11)
        : new ModelPreparationAdjustments();
    if (args[0] == "--prepare")
    {
        var folder = await ModelPreparationRunner.RunAsync(args[1], args[2], args[3], args[4],
            args[5], args[6] == "keep", scriptPath: args[7], adjustments: adjustments,
            weaponType: weaponType, textures: textures, modelEntry: modelEntry);
        Console.WriteLine(folder);
    }
    else
    {
        string staging;
        using (var draft = await ModelPreparationRunner.PrepareDraftAsync(args[1], args[2], args[3],
                   args[4], args[5], args[6] == "keep", scriptPath: args[7], adjustments: adjustments,
                   weaponType: weaponType, textures: textures, modelEntry: modelEntry))
        {
            staging = draft.StagingDirectory;
            if (!File.Exists(Path.Combine(staging, "report.json"))) throw new IOException("Draft report is missing");
            Console.WriteLine("Draft: " + staging);
            if (args[0] is "--prepare-draft" or "--prepare-multi-draft" or "--prepare-env-draft" or "--prepare-env-game-draft")
                Console.WriteLine("Published: " + draft.Publish());
        }
        if (args[0] is "--prepare-discard" or "--prepare-multi-discard" or "--prepare-env-discard" or "--prepare-env-game-discard" or "--prepare-model-discard" or "--prepare-multi-model-discard" && Directory.Exists(staging))
            throw new IOException("Discard left a staging directory");
    }
    return 0;
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: VanillaNifProbe <Skyrim Data directory> <relative NIF path> <output NIF path>");
    return 2;
}

var (dataPath, relativePath, outputPath) = (args[0], args[1].Replace('/', '\\'), args[2]);
if (relativePath == "--validate")
{
    var data = File.ReadAllBytes(outputPath);
    var scene = SseNifReader.Read(data);
    var header = NifCompatibilityInspector.Read(outputPath);
    Console.WriteLine($"{Path.GetFileName(outputPath)}: {header.Family}, {data.Length} bytes, " +
        $"{scene.Meshes.Count} visible meshes, {scene.Meshes.Sum(m => m.Positions.Length)} vertices, " +
        $"{scene.Meshes.Sum(m => m.Indices.Length / 3)} triangles");
    foreach (var mesh in scene.Meshes)
        Console.WriteLine($"  {mesh.Name}: {mesh.Positions.Length} vertices, diffuse={mesh.DiffuseTexture}");
    return header.Family == NifFormatFamily.SkyrimSe ? 0 : 1;
}
if ((!relativePath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) && !relativePath.StartsWith("--find=")) ||
    (!relativePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && !relativePath.StartsWith("--find=")) ||
    relativePath.Split('\\').Any(part => part is "" or "." or "..") ||
    !Path.IsPathFullyQualified(outputPath))
{
    Console.Error.WriteLine("Use a relative meshes/*.nif path and an absolute output path.");
    return 2;
}

if (relativePath.StartsWith("--find="))
{
    foreach (var bsa in Directory.EnumerateFiles(dataPath, "Skyrim - Meshes*.bsa").Order(StringComparer.OrdinalIgnoreCase))
    {
        var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
        try
        {
            foreach (var file in archive.Files.Where(f => f.Path.Contains(relativePath[7..], StringComparison.OrdinalIgnoreCase)).Take(40))
                Console.WriteLine(Path.GetFileName(bsa) + ": " + file.Path);
        }
        finally { (archive as IDisposable)?.Dispose(); }
    }
    return 0;
}

foreach (var bsa in Directory.EnumerateFiles(dataPath, "Skyrim - Meshes*.bsa").Order(StringComparer.OrdinalIgnoreCase))
{
    var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
    try
    {
        if (!archive.TryGetFolder(Path.GetDirectoryName(relativePath)!, out var folder)) continue;
        var entry = folder.Files.FirstOrDefault(file =>
            string.Equals(file.Path.Replace('/', '\\'), relativePath, StringComparison.OrdinalIgnoreCase));
        if (entry is null) continue;
        using var input = entry.AsStream();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        var bytes = memory.ToArray();
        var preview = SseNifReader.Read(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write)) output.Write(bytes);
        Console.WriteLine($"{Path.GetFileName(bsa)}: {relativePath}, {bytes.Length} bytes, {preview.Meshes.Count} visible meshes");
        return 0;
    }
    finally { (archive as IDisposable)?.Dispose(); }
}

Console.Error.WriteLine("NIF was not found in Skyrim base mesh archives: " + relativePath);
return 1;
