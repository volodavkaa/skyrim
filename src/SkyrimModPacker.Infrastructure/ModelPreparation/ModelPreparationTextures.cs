using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

public sealed record ModelPreparationMaterialTexture(int Slot, string DiffuseEntry, string NormalEntry);

public sealed record ModelPreparationTextureOptions(string DiffuseEntry, string NormalEntry,
    string TexconvPath, bool InvertNormalY = false,
    IReadOnlyList<ModelPreparationMaterialTexture>? Materials = null);

internal static class ModelPreparationTextures
{
    internal static async Task<(string Diffuse, string Normal)> PrepareAsync(string archivePath,
        string staging, string slug, ModelPreparationTextureOptions options, CancellationToken token,
        int? materialSlot = null)
    {
        if (!File.Exists(options.TexconvPath))
            throw new FileNotFoundException("Texconv executable was not found", options.TexconvPath);
        if (string.IsNullOrWhiteSpace(options.DiffuseEntry) || string.IsNullOrWhiteSpace(options.NormalEntry) ||
            options.DiffuseEntry.Equals(options.NormalEntry, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose distinct diffuse and normal images from the ZIP archive.");
        var stem = slug + (materialSlot is null ? "" : "_m" + materialSlot.Value);
        var relativeDiffuse = $"textures/{slug}/{stem}.dds";
        var relativeNormal = $"textures/{slug}/{stem}_n.dds";
        var output = Path.Combine(staging, "textures", slug);
        var input = Path.Combine(staging, ".source-textures");
        var convertedFolder = Path.Combine(input, "converted");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(convertedFolder);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var (entryName, role, format, destination) in new[]
                     {
                         (options.DiffuseEntry, "diffuse", "BC7_UNORM_SRGB", Path.Combine(output, stem + ".dds")),
                         (options.NormalEntry, "normal", "BC7_UNORM", Path.Combine(output, stem + "_n.dds"))
                     })
            {
                token.ThrowIfCancellationRequested();
                var matches = archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name) &&
                    item.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("ZIP image entry is missing or ambiguous: " + entryName);
                var entry = matches[0];
                if (entry.Length is <= 0 or > 64 * 1024 * 1024)
                    throw new InvalidDataException("Image size is outside the 64 MiB limit: " + entryName);
                var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".dds"))
                    throw new InvalidDataException("Unsupported image extension: " + entryName);
                var source = Path.Combine(input, role + extension);
                await using (var sourceStream = entry.Open())
                await using (var targetStream = File.Create(source))
                    await sourceStream.CopyToAsync(targetStream, token);
                await ConvertAsync(options.TexconvPath, source, convertedFolder, format,
                    role == "normal" && options.InvertNormalY, token);
                var converted = Path.Combine(convertedFolder, role + ".dds");
                if (!File.Exists(converted)) throw new IOException("Texconv did not create " + role + ".dds");
                File.Move(converted, destination);
                var info = DdsResourceInspector.Inspect(destination, token);
                if (info.Format != "BC7" || (Math.Max(info.Width, info.Height) > 1 && info.MipLevels < 2))
                    throw new InvalidDataException("Converted DDS lacks BC7 or mipmaps: " + destination);
                using var dds = File.OpenRead(destination);
                var header = new byte[132];
                dds.ReadExactly(header);
                var actualFormat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(128));
                var expectedFormat = role == "diffuse" ? 99u : 98u;
                if (actualFormat != expectedFormat)
                    throw new InvalidDataException("Converted DDS has the wrong color space: " + destination);
            }
            return (relativeDiffuse, relativeNormal);
        }
        finally
        {
            var fullStage = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(input).StartsWith(fullStage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary texture path escaped staging directory.");
            await DeleteTemporaryDirectoryAsync(input);
        }
    }

    internal static async Task DeleteTemporaryDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 7)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 7)
            {
                await Task.Delay(100);
            }
        }
    }

    private static async Task ConvertAsync(string executable, string source, string output,
        string format, bool invertNormalY, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var value in new[] { "-nologo", "-y", "-f", format, "-m", "0", "-o", output })
            start.ArgumentList.Add(value);
        if (invertNormalY) start.ArgumentList.Add("--invert-y");
        start.ArgumentList.Add(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Texconv did not start.");
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
            if (timeout.IsCancellationRequested && !token.IsCancellationRequested)
                throw new TimeoutException("Texconv did not finish within five minutes.");
            throw;
        }
        var outputText = await stdout;
        var errorText = await stderr;
        if (process.ExitCode != 0)
        {
            var details = outputText + "\n" + errorText;
            throw new InvalidOperationException("Texconv failed: " + details[^Math.Min(2000, details.Length)..]);
        }
    }
}
