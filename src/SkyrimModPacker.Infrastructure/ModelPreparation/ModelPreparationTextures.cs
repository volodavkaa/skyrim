using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

public sealed record ModelPreparationMaterialTexture(int Slot, string DiffuseEntry, string NormalEntry,
    string? MetallicEntry = null, string? CubemapEntry = null);

public sealed record ModelPreparationTextureOptions(string DiffuseEntry, string NormalEntry,
    string TexconvPath, bool InvertNormalY = false,
    IReadOnlyList<ModelPreparationMaterialTexture>? Materials = null,
    string? MetallicEntry = null, string? CubemapEntry = null,
    string? GameCubemapPath = null, int MaxTextureSize = 0);

internal sealed record PreparedMaterialTextures(string Diffuse, string Normal,
    string? EnvMask = null, string? EnvMap = null);

internal static class ModelPreparationTextures
{
    internal static async Task<PreparedMaterialTextures> PrepareAsync(string archivePath,
        string staging, string slug, ModelPreparationTextureOptions options, CancellationToken token,
        string gameDataDirectory, int? materialSlot = null)
    {
        if (!File.Exists(options.TexconvPath))
            throw new FileNotFoundException("Texconv executable was not found", options.TexconvPath);
        if (options.MaxTextureSize != 0 && options.MaxTextureSize is < 4 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(options.MaxTextureSize));
        if (string.IsNullOrWhiteSpace(options.DiffuseEntry) || string.IsNullOrWhiteSpace(options.NormalEntry) ||
            options.DiffuseEntry.Equals(options.NormalEntry, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose distinct diffuse and normal images from the ZIP archive or disk.");
        var metallicEntry = string.IsNullOrWhiteSpace(options.MetallicEntry) ? null : options.MetallicEntry.Trim();
        var cubemapEntry = string.IsNullOrWhiteSpace(options.CubemapEntry) ? null : options.CubemapEntry.Trim();
        var gameCubemap = cubemapEntry == null && !string.IsNullOrWhiteSpace(options.GameCubemapPath)
            ? options.GameCubemapPath.Trim() : null;
        if ((cubemapEntry != null || gameCubemap != null) && metallicEntry == null)
            throw new ArgumentException("Choose a metallic/environment mask before a custom cubemap.");
        var entries = new[] { options.DiffuseEntry, options.NormalEntry, metallicEntry, cubemapEntry }
            .Where(value => value != null).ToArray();
        if (entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new ArgumentException("Each texture role needs a different image.");
        var stem = slug + (materialSlot is null ? "" : "_m" + materialSlot.Value);
        var relativeDiffuse = $"textures/{slug}/{stem}.dds";
        var relativeNormal = $"textures/{slug}/{stem}_n.dds";
        var relativeEnvMask = metallicEntry == null ? null : $"textures/{slug}/{stem}_em.dds";
        var relativeEnvMap = cubemapEntry == null && gameCubemap == null ? null : $"textures/{slug}/{stem}_e.dds";
        var output = Path.Combine(staging, "textures", slug);
        var input = Path.Combine(staging, ".source-textures");
        var convertedFolder = Path.Combine(input, "converted");
        var rawFolder = Path.Combine(input, "raw");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(convertedFolder);
        Directory.CreateDirectory(rawFolder);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var (entryName, role, format, destination) in new[]
                     {
                         (options.DiffuseEntry, "diffuse", "BC7_UNORM_SRGB", Path.Combine(output, stem + ".dds")),
                         (options.NormalEntry, "normal", "BC7_UNORM", Path.Combine(output, stem + "_n.dds")),
                         (metallicEntry, "metallic", "BC7_UNORM", Path.Combine(output, stem + "_em.dds"))
                     }.Where(item => item.Item1 != null))
            {
                token.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(entryName!).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".dds"))
                    throw new InvalidDataException("Unsupported image extension: " + entryName);
                var source = Path.Combine(input, role + extension);
                await CopyTextureAsync(archive, entryName!, source, token);
                var size = TextureSizePlanner.Inspect(archivePath, source, options.MaxTextureSize);
                var resize = size.SourceWidth != size.OutputWidth || size.SourceHeight != size.OutputHeight;
                if (role == "normal")
                {
                    await ConvertAsync(options.TexconvPath, source, rawFolder, "R8G8B8A8_UNORM",
                        options.InvertNormalY, token, mipLevels: 1, width: resize ? size.OutputWidth : 0,
                        height: resize ? size.OutputHeight : 0);
                    var raw = Path.Combine(rawFolder, "normal.dds");
                    FixOpaqueNormalAlpha(raw);
                    await ConvertAsync(options.TexconvPath, raw, convertedFolder, format,
                        false, token, separateAlpha: true);
                }
                else
                    await ConvertAsync(options.TexconvPath, source, convertedFolder, format,
                        false, token, separateAlpha: true, width: resize ? size.OutputWidth : 0,
                        height: resize ? size.OutputHeight : 0);
                var converted = Path.Combine(convertedFolder, role + ".dds");
                if (!File.Exists(converted)) throw new IOException("Texconv did not create " + role + ".dds");
                File.Move(converted, destination);
                var info = DdsResourceInspector.Inspect(destination, token);
                if (info.Format != "BC7" || info.MipLevels != size.MipLevels ||
                    info.Width != size.OutputWidth || info.Height != size.OutputHeight)
                    throw new InvalidDataException("Converted DDS has unexpected BC7 dimensions or mipmaps: " + destination);
                using var dds = File.OpenRead(destination);
                var header = new byte[132];
                dds.ReadExactly(header);
                var actualFormat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(128));
                var expectedFormat = role == "diffuse" ? 99u : 98u;
                if (actualFormat != expectedFormat)
                    throw new InvalidDataException("Converted DDS has the wrong color space: " + destination);
            }
            if (cubemapEntry != null)
            {
                token.ThrowIfCancellationRequested();
                if (!cubemapEntry.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Choose a DDS cubemap: " + cubemapEntry);
                var destination = Path.Combine(output, stem + "_e.dds");
                await CopyTextureAsync(archive, cubemapEntry, destination, token);
                ValidateCubemap(destination, token);
            }
            else if (gameCubemap != null)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(output, stem + "_e.dds");
                await File.WriteAllBytesAsync(destination,
                    GameCubemapCatalog.Read(gameDataDirectory, gameCubemap), token);
                ValidateCubemap(destination, token);
            }
            return new PreparedMaterialTextures(relativeDiffuse, relativeNormal,
                relativeEnvMask, relativeEnvMap);
        }
        finally
        {
            var fullStage = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(input).StartsWith(fullStage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary texture path escaped staging directory.");
            await DeleteTemporaryDirectoryAsync(input);
        }
    }

    private static async Task CopyTextureAsync(ZipArchive archive, string name, string destination, CancellationToken token)
    {
        Stream source;
        long length;
        if (Path.IsPathFullyQualified(name))
        {
            var path = PackingPathGuard.GetAbsolutePath(name);
            PackingPathGuard.EnsureNoLinks(path);
            var file = File.OpenRead(path);
            source = file; length = file.Length;
        }
        else
        {
            var matches = archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name) &&
                item.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("ZIP image entry is missing or ambiguous: " + name);
            length = matches[0].Length; source = matches[0].Open();
        }
        await using (source)
        {
            const int limit = 64 * 1024 * 1024;
            if (length is <= 0 or > limit) throw new InvalidDataException("Image size is outside the 64 MiB limit: " + name);
            await using var target = File.Create(destination);
            var buffer = new byte[81920]; long copied = 0; int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            {
                copied += count;
                if (copied > limit) throw new InvalidDataException("Image exceeds the 64 MiB limit: " + name);
                await target.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (copied != length) throw new InvalidDataException("Image length changed while reading: " + name);
        }
    }

    private static void ValidateCubemap(string path, CancellationToken token)
    {
        var info = DdsResourceInspector.Inspect(path, token);
        if (info.Width != info.Height)
            throw new InvalidDataException("Cubemap faces must be square: " + path);
        using var input = File.OpenRead(path);
        Span<byte> header = stackalloc byte[148];
        input.ReadExactly(header[..128]);
        var dx10 = System.Text.Encoding.ASCII.GetString(header[84..88]) == "DX10";
        if (dx10) input.ReadExactly(header[128..]);
        var isCube = dx10
            ? (BinaryPrimitives.ReadUInt32LittleEndian(header[136..]) & 4) != 0
            : (BinaryPrimitives.ReadUInt32LittleEndian(header[112..]) & 0xFE00) == 0xFE00;
        if (!isCube)
            throw new InvalidDataException("Selected DDS is not a complete six-face cubemap: " + path);
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

    internal static async Task ConvertAsync(string executable, string source, string output,
        string format, bool invertNormalY, CancellationToken token,
        int mipLevels = 0, bool separateAlpha = false, int width = 0, int height = 0)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var value in new[] { "-nologo", "-y", "-f", format, "-m", mipLevels.ToString(), "-o", output })
            start.ArgumentList.Add(value);
        if (invertNormalY) start.ArgumentList.Add("--invert-y");
        if (separateAlpha) start.ArgumentList.Add("-sepalpha");
        if (width > 0 && height > 0)
        {
            start.ArgumentList.Add("-w"); start.ArgumentList.Add(width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-h"); start.ArgumentList.Add(height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (Path.GetExtension(source).Equals(".dds", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add("--ignore-mips");
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

    private static void FixOpaqueNormalAlpha(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 128 || !data.AsSpan(0, 4).SequenceEqual("DDS "u8))
            throw new InvalidDataException("Texconv did not create an RGBA8 normal intermediate.");
        var dx10 = data.AsSpan(84, 4).SequenceEqual("DX10"u8);
        var pixelOffset = dx10 ? 148 : 128;
        var rgba = dx10
            ? data.Length >= 148 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(128)) == 28
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(88)) == 32 &&
              BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(92)) == 255 &&
              BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96)) == 65280 &&
              BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(100)) == 16711680 &&
              BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(104)) == 4278190080;
        if (!rgba) throw new InvalidDataException("Texconv did not create RGBA8 pixels.");
        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
        var pixels = checked(width * height);
        if (data.Length < pixelOffset + pixels * 4L)
            throw new InvalidDataException("Normal intermediate is truncated.");
        var opaque = true;
        for (var i = 0; i < pixels; i++)
            if (data[pixelOffset + i * 4 + 3] < 250) { opaque = false; break; }
        if (!opaque) return;
        for (var i = 0; i < pixels; i++) data[pixelOffset + i * 4 + 3] = 128;
        File.WriteAllBytes(path, data);
    }
}
