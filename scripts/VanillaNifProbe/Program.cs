using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.Infrastructure.ModelPreparation;
using System.Globalization;

if (args.Length is 8 or 9 or 12 or 13 or 15 or 16 or 17 && args[0] is "--prepare" or "--prepare-draft" or "--prepare-discard")
{
    var weaponType = args.Length is 9 or 12 or 13 ? args[8] : args.Length == 17 ? args[^1] : "sword1h";
    var textures = args.Length is 12 or 13
        ? new ModelPreparationTextureOptions(args[9], args[10], args[11], args.Length == 13 && args[12] == "invert")
        : null;
    var adjustments = args.Length >= 15
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
            weaponType: weaponType, textures: textures);
        Console.WriteLine(folder);
    }
    else
    {
        string staging;
        using (var draft = await ModelPreparationRunner.PrepareDraftAsync(args[1], args[2], args[3],
                   args[4], args[5], args[6] == "keep", scriptPath: args[7], adjustments: adjustments,
                   weaponType: weaponType, textures: textures))
        {
            staging = draft.StagingDirectory;
            if (!File.Exists(Path.Combine(staging, "report.json"))) throw new IOException("Draft report is missing");
            Console.WriteLine("Draft: " + staging);
            if (args[0] == "--prepare-draft") Console.WriteLine("Published: " + draft.Publish());
        }
        if (args[0] == "--prepare-discard" && Directory.Exists(staging))
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
