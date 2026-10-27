using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

public sealed record GameCubemap(string Name, string Path);

public static class GameCubemapCatalog
{
    public static IReadOnlyList<GameCubemap> Choices { get; } =
    [
        new("Шаблон зброї", ""),
        new("ShinyDull — м’яке відбиття", "textures/cubemaps/ShinyDull_e.dds"),
        new("ShinyBright — яскраве", "textures/cubemaps/ShinyBright_e.dds"),
        new("Chrome — хром", "textures/cubemaps/Chrome_e.dds"),
        new("Bronze — бронза", "textures/cubemaps/Bronze_e.dds"),
        new("ShinyContrast — контрастне", "textures/cubemaps/ShinyContrast_e.dds"),
        new("SimpleGrey — нейтральне", "textures/cubemaps/SimpleGrey_e.dds")
    ];

    public static byte[] Read(string dataDirectory, string path)
    {
        if (!Choices.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && item.Path.Length > 0))
            throw new ArgumentException("Unknown game cubemap: " + path);
        var relative = path.Replace('/', Path.DirectorySeparatorChar);
        var loose = Path.Combine(dataDirectory, relative);
        if (File.Exists(loose))
        {
            if (new FileInfo(loose).Length is <= 0 or > 64 * 1024 * 1024)
                throw new InvalidDataException("Game cubemap exceeds the 64 MiB limit: " + path);
            return File.ReadAllBytes(loose);
        }
        foreach (var bsa in Directory.EnumerateFiles(dataDirectory, "Skyrim - Textures*.bsa"))
        {
            var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
            try
            {
                if (!archive.TryGetFolder(Path.GetDirectoryName(relative)!, out var folder)) continue;
                var entry = folder.Files.FirstOrDefault(file => file.Path.Replace('/', Path.DirectorySeparatorChar)
                    .Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;
                using var stream = entry.AsStream();
                using var output = new MemoryStream();
                stream.CopyTo(output);
                if (output.Length is <= 0 or > 64 * 1024 * 1024)
                    throw new InvalidDataException("Game cubemap exceeds the 64 MiB limit: " + path);
                return output.ToArray();
            }
            finally { (archive as IDisposable)?.Dispose(); }
        }
        throw new FileNotFoundException("Game cubemap was not found in Skyrim Data: " + path);
    }
}
