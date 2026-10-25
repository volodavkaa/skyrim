using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.Preview;

/// <summary>Read-only preview of scanned files before there is a workspace or normalized resource tree.</summary>
public sealed class FolderModelPreviewService(WeaponFolderScan scan) : IModelPreviewService
{
    public Task<ModelPreview> LoadAsync(string projectDirectory, string modelPath, string gameDataPath, CancellationToken token = default) => Task.Run(() =>
    {
        var source = scan.Files.SingleOrDefault(f => f.RelativePath.Equals(modelPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) && f.SourcePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("The selected NIF is not in the scanned folder: " + modelPath);
        byte[] Read(WeaponFolderSource file, long limit)
        {
            var path = PackingPathGuard.ResolveUnderRoot(scan.Root, file.RelativePath); PackingPathGuard.EnsureNoLinks(path);
            if (!path.Equals(Path.GetFullPath(file.SourcePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("Scanned resource path changed.");
            using var stream = File.OpenRead(path); if (stream.Length > limit) throw new InvalidDataException("Preview resource size limit: " + file.RelativePath);
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); token.ThrowIfCancellationRequested(); return bytes;
        }
        var scene = SseNifReader.Read(Read(source, SseNifReader.MaxFileBytes), token);
        var textures = new List<PreviewTexture>(); var warnings = scene.Warnings.ToList(); long pixels = 0;
        foreach (var path in scene.Meshes.Select(m => m.DiffuseTexture).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var relative = path.Replace('\\', '/'); if (!relative.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) relative = "textures/" + relative;
                var matches = scan.Files.Where(f => f.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase) || f.RelativePath.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new IOException(matches.Length == 0 ? "Texture missing from archive: " + relative : "Ambiguous texture path: " + relative);
                var texture = DdsPreviewDecoder.Decode(path, Read(matches[0], 32 * 1024 * 1024), token); pixels += texture.BgraPixels.Length;
                if (pixels > 64 * 1024 * 1024) throw new IOException("Decoded texture preview budget exceeded."); textures.Add(texture);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(ex.Message); }
        }
        return scene with { Textures = textures, Warnings = warnings };
    }, token);
}
