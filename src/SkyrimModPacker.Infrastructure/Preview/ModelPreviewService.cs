using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using System.Text.Json;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Preview;

public sealed class ModelPreviewService : IModelPreviewService
{
    public Task<ModelPreview> LoadAsync(string projectDirectory, string modelPath, string gameDataPath, CancellationToken token = default) => Task.Run(() =>
    {
        var path = Resource(modelPath, "meshes", ".nif");
        var scene = SseNifReader.Read(ReadResource(projectDirectory, path, gameDataPath, SseNifReader.MaxFileBytes, token), token);
        var textures = new List<PreviewTexture>(); var warnings = scene.Warnings.ToList(); long total = 0;
        foreach (var source in scene.Meshes.Select(m => m.DiffuseTexture).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var relative = Resource(source!, "textures", ".dds");
                var data = ReadResource(projectDirectory, relative, gameDataPath, 32 * 1024 * 1024, token);
                var texture = DdsPreviewDecoder.Decode(source!, data, token);
                total += texture.BgraPixels.Length;
                if (total > 64 * 1024 * 1024) { warnings.Add("Decoded texture preview budget exceeded; remaining textures use neutral material."); break; }
                textures.Add(texture);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"Texture '{source}': {ex.Message}"); }
        }
        var dependencies = new List<PreviewTextureDependency>();
        foreach (var source in scene.TexturePaths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ReadResource(projectDirectory, Resource(source, "textures", ".dds"), gameDataPath, 32 * 1024 * 1024, token, probe: true);
                dependencies.Add(new(source, true));
            }
            catch (FileNotFoundException ex) { dependencies.Add(new(source, false, ex.Message)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { dependencies.Add(new(source, null, ex.Message)); }
        }
        return scene with { Textures = textures, Warnings = warnings, TextureDependencies = dependencies };
    }, token);

    private static string Resource(string path, string root, string extension)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) path = root + "/" + path;
        _ = PackingPathGuard.ValidateEntries([path]);
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported resource extension.");
        return path;
    }

    private static byte[] ReadResource(string project, string relative, string data, int limit, CancellationToken token, bool probe = false)
    {
        foreach (var root in new[] { project, data }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = PackingPathGuard.ResolveUnderRoot(root, relative);
            if (File.Exists(target)) { if (probe) return []; using var input = File.OpenRead(target); return ReadBounded(input, limit, token); }
            if (Directory.Exists(target)) throw new IOException("A resource path points to a directory: " + relative);
            var manifest = Path.Combine(root, "mod-project.json");
            if (root == project && File.Exists(manifest))
            {
                PackingPathGuard.EnsureNoLinks(manifest);
                if (new FileInfo(manifest).Length > 16 * 1024 * 1024) throw new InvalidDataException("Project metadata exceeds preview limits.");
                using var input = File.OpenRead(manifest); using var json = JsonDocument.Parse(input);
                if (json.RootElement.TryGetProperty("Resources", out var resources) && resources.EnumerateArray().Any(p => string.Equals(p.GetString()?.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase)))
                    throw new FileNotFoundException("Registered project resource is missing: " + relative);
            }
        }
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(data, "*.bsa").Where(p => Path.GetFileName(p).StartsWith("Skyrim", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("Dawnguard", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                PackingPathGuard.EnsureNoLinks(path);
                var archive = Archive.CreateReader(GameRelease.SkyrimSE, path);
                try
                {
                    if (!archive.TryGetFolder(Path.GetDirectoryName(relative.Replace('/', '\\'))!, out var folder)) continue;
                    var file = folder.Files.FirstOrDefault(f => Path.GetFileName(f.Path.Replace('/', '\\')).Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase));
                    if (file == null) continue;
                    if (probe) return [];
                    using var stream = file.AsStream(); return ReadBounded(stream, limit, token);
                }
                finally { (archive as IDisposable)?.Dispose(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        if (errors.Count > 0) throw new IOException("BSA read failed: " + string.Join("; ", errors));
        throw new FileNotFoundException("Preview resource not found: " + relative);
    }

    private static byte[] ReadBounded(Stream input, int limit, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            token.ThrowIfCancellationRequested(); var read = input.Read(buffer);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("Resource exceeds the preview memory limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
