using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.ModelPreparation;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.Infrastructure.Archivers;

/// <summary>Creates a separate, verified archive from a completed mod ZIP without editing its sources.</summary>
public static class TextureVariantArchiveService
{
    public static async Task<string> CreateAsync(string originalZip, int maxSize, string texconv,
        string gameDataPath, CancellationToken token = default)
    {
        originalZip = PackingPathGuard.GetAbsolutePath(originalZip);
        PackingPathGuard.EnsureNoLinks(originalZip);
        PluginWriteGuard.EnsureOutsideGame(originalZip, gameDataPath);
        if (!File.Exists(originalZip)) throw new FileNotFoundException("Original mod archive is missing.", originalZip);
        if (!File.Exists(texconv)) throw new FileNotFoundException("texconv.exe was not found.", texconv);
        if (maxSize is not (256 or 512 or 1024 or 2048 or 4096)) throw new ArgumentOutOfRangeException(nameof(maxSize));
        var originalHash = await ZipManifest.CaptureAsync(originalZip, "", token);
        var outputDirectory = Path.GetDirectoryName(originalZip)!;
        var stem = Path.GetFileNameWithoutExtension(originalZip) + "-" + maxSize;
        var output = Path.Combine(outputDirectory, stem + ".zip");
        for (var suffix = 2; File.Exists(output); suffix++)
            output = Path.Combine(outputDirectory, stem + "-" + suffix + ".zip");
        PluginWriteGuard.EnsureOutsideGame(output, gameDataPath);
        var stage = Path.Combine(Path.GetTempPath(), "skyrim-texture-variant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var files = new List<(string SourcePath, string EntryPath)>();
            var changes = new JsonArray(); long total = 0;
            using (var zip = ZipFile.OpenRead(originalZip))
            {
                if (zip.Entries.Count is 0 or > 10000) throw new InvalidDataException("Unsupported number of ZIP entries.");
                var names = PackingPathGuard.ValidateEntries(zip.Entries.Select(entry => entry.FullName));
                for (var i = 0; i < zip.Entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = zip.Entries[i]; var name = names[i];
                    if (entry.FullName != name) throw new InvalidDataException("Non-canonical ZIP entry: " + entry.FullName);
                    ZipManifest.EnsureAllowed(name);
                    total += entry.Length;
                    if (entry.Length < 0 || entry.Length > 512L * 1024 * 1024 || total > 2L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("ZIP exceeds extraction limits.");
                    var target = PackingPathGuard.ResolveUnderRoot(stage, "files/" + name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var input = entry.Open())
                    await using (var targetStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920]; long copied = 0; int read;
                        while ((read = await input.ReadAsync(buffer, token)) != 0)
                        {
                            copied += read;
                            if (copied > entry.Length) throw new InvalidDataException("ZIP entry exceeds declared length: " + name);
                            await targetStream.WriteAsync(buffer.AsMemory(0, read), token);
                        }
                        if (copied != entry.Length) throw new InvalidDataException("ZIP entry is truncated: " + name);
                    }
                    if (name.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    {
                        var descriptor = ReadHeader(target);
                        if (!descriptor.Cubemap && Math.Max(descriptor.Width, descriptor.Height) > maxSize)
                        {
                            _ = DdsResourceInspector.Inspect(target, token);
                            var format = descriptor.Format ?? throw new InvalidDataException("Cannot safely resize DDS format: " + name);
                            var plan = TextureSizePlanner.Plan(descriptor.Width, descriptor.Height, maxSize);
                            var convertedDirectory = Path.Combine(stage, "converted", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            Directory.CreateDirectory(convertedDirectory);
                            await ModelPreparationTextures.ConvertAsync(texconv, target, convertedDirectory, format, false, token,
                                separateAlpha: true, width: plan.OutputWidth, height: plan.OutputHeight);
                            var converted = Path.Combine(convertedDirectory, Path.GetFileNameWithoutExtension(target) + ".dds");
                            if (!File.Exists(converted)) throw new IOException("texconv did not create DDS: " + name);
                            var info = DdsResourceInspector.Inspect(converted, token);
                            var result = ReadHeader(converted);
                            if (info.Width != plan.OutputWidth || info.Height != plan.OutputHeight || info.MipLevels != plan.MipLevels ||
                                result.Format != descriptor.Format || result.AlphaMode != descriptor.AlphaMode || result.Cubemap)
                                throw new InvalidDataException("Converted DDS changed format, alpha mode or dimensions: " + name);
                            File.Move(converted, target, overwrite: true);
                            changes.Add(new JsonObject { ["Path"] = name, ["OriginalWidth"] = descriptor.Width,
                                ["OriginalHeight"] = descriptor.Height, ["Width"] = info.Width, ["Height"] = info.Height, ["Format"] = format });
                        }
                    }
                    files.Add((target, name));
                }
            }
            var reportFile = files.SingleOrDefault(file => file.EntryPath == ReportedArchiveService.ReportEntry);
            if (reportFile.SourcePath == null) throw new InvalidDataException("Original archive has no mod-report.json.");
            if (new FileInfo(reportFile.SourcePath).Length > 16 * 1024 * 1024) throw new InvalidDataException("Export report is too large.");
            var report = JsonNode.Parse(await File.ReadAllTextAsync(reportFile.SourcePath, token))?.AsObject()
                ?? throw new InvalidDataException("Invalid export report.");
            var rows = report["Files"]?.AsArray() ?? throw new InvalidDataException("Export report has no file manifest.");
            var payload = files.Where(file => file.EntryPath != ReportedArchiveService.ReportEntry).ToDictionary(file => file.EntryPath, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var name = row?["Path"]?.GetValue<string>() ?? throw new InvalidDataException("Invalid report file entry.");
                if (!payload.Remove(name, out var file)) throw new InvalidDataException("Report does not match ZIP: " + name);
                var snapshot = await ZipManifest.CaptureAsync(file.SourcePath, name, token);
                row!["Length"] = snapshot.Length; row["Sha256"] = Convert.ToHexString(snapshot.Hash);
            }
            if (payload.Count != 0) throw new InvalidDataException("Report does not include all ZIP files.");
            report["TextureVariant"] = new JsonObject { ["MaximumDimension"] = maxSize,
                ["OriginalArchiveSha256"] = Convert.ToHexString(originalHash.Hash), ["Resized"] = changes,
                ["Cubemaps"] = "Preserved unchanged" };
            await File.WriteAllTextAsync(reportFile.SourcePath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
            var currentOriginal = await ZipManifest.CaptureAsync(originalZip, "", token);
            if (currentOriginal.Length != originalHash.Length || !currentOriginal.Hash.AsSpan().SequenceEqual(originalHash.Hash))
                throw new IOException("Original archive changed while creating its texture variant.");
            await new ZipArchiver().CreateNewArchiveAsync(output, files, cancellationToken: token);
            return output;
        }
        finally { await ModelPreparationTextures.DeleteTemporaryDirectoryAsync(stage); }
    }

    private sealed record DdsHeader(int Width, int Height, bool Cubemap, string? Format, uint AlphaMode);

    private static DdsHeader ReadHeader(string path)
    {
        using var file = File.OpenRead(path); Span<byte> header = stackalloc byte[148];
        file.ReadExactly(header[..128]);
        if (!header[..4].SequenceEqual("DDS "u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 124)
            throw new InvalidDataException("Invalid DDS header: " + path);
        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
        if (width is < 1 or > 16384 || height is < 1 or > 16384) throw new InvalidDataException("Invalid DDS dimensions: " + path);
        var code = System.Text.Encoding.ASCII.GetString(header[84..88]);
        if (code == "DX10")
        {
            file.ReadExactly(header[128..]);
            var dxgi = BinaryPrimitives.ReadUInt32LittleEndian(header[128..]);
            var format = dxgi switch { 71 => "BC1_UNORM", 72 => "BC1_UNORM_SRGB", 74 => "BC2_UNORM", 75 => "BC2_UNORM_SRGB",
                77 => "BC3_UNORM", 78 => "BC3_UNORM_SRGB", 80 => "BC4_UNORM", 81 => "BC4_SNORM", 83 => "BC5_UNORM", 84 => "BC5_SNORM",
                98 => "BC7_UNORM", 99 => "BC7_UNORM_SRGB", 28 => "R8G8B8A8_UNORM", 29 => "R8G8B8A8_UNORM_SRGB",
                87 => "B8G8R8A8_UNORM", 88 => "B8G8R8X8_UNORM", 91 => "B8G8R8A8_UNORM_SRGB", 93 => "B8G8R8X8_UNORM_SRGB", 61 => "R8_UNORM", _ => null };
            return new(width, height, (BinaryPrimitives.ReadUInt32LittleEndian(header[136..]) & 4) != 0, format,
                BinaryPrimitives.ReadUInt32LittleEndian(header[144..]));
        }
        var legacy = code switch { "DXT1" => "BC1_UNORM", "DXT3" => "BC2_UNORM", "DXT5" => "BC3_UNORM",
            "ATI1" or "BC4U" => "BC4_UNORM", "BC4S" => "BC4_SNORM", "ATI2" or "BC5U" => "BC5_UNORM", "BC5S" => "BC5_SNORM", _ => null };
        if (code == "\0\0\0\0" && BinaryPrimitives.ReadUInt32LittleEndian(header[88..]) == 32)
        {
            var r = BinaryPrimitives.ReadUInt32LittleEndian(header[92..]); var b = BinaryPrimitives.ReadUInt32LittleEndian(header[100..]);
            var a = BinaryPrimitives.ReadUInt32LittleEndian(header[104..]);
            if (r == 255 && b == 16711680 && a == 4278190080) legacy = "R8G8B8A8_UNORM";
            else if (r == 16711680 && b == 255 && a == 4278190080) legacy = "B8G8R8A8_UNORM";
            else if (r == 16711680 && b == 255 && a == 0) legacy = "B8G8R8X8_UNORM";
        }
        return new(width, height, (BinaryPrimitives.ReadUInt32LittleEndian(header[112..]) & 0x200) != 0, legacy, 0);
    }
}
