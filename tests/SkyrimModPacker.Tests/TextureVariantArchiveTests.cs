using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.ModelPreparation;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;

namespace SkyrimModPacker.Tests;

public class TextureVariantArchiveTests
{
    [Fact]
    public async Task CreatesVerifiedSecondZipWithResizedDdsAndAccurateReport()
    {
        var texconv = Environment.GetEnvironmentVariable("TEXCONV_PATH") ?? Path.Combine(Path.GetTempPath(), "skyrim-texconv-may2026.exe");
        if (!File.Exists(texconv)) return;
        var root = Path.Combine(Path.GetTempPath(), "skyrim-variant-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var png = Path.Combine(root, "diffuse.png"); var input = new byte[512 * 512 * 4];
            for (var i = 0; i < input.Length; i += 4) { input[i] = 60; input[i + 1] = 90; input[i + 2] = 120; input[i + 3] = 96; }
            var image = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, input, 2048);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(png)) encoder.Save(stream);
            var ddsFolder = Path.Combine(root, "dds"); Directory.CreateDirectory(ddsFolder);
            await ModelPreparationTextures.ConvertAsync(texconv, png, ddsFolder, "BC7_UNORM_SRGB", false, CancellationToken.None, separateAlpha: true);
            var dds = File.ReadAllBytes(Path.Combine(ddsFolder, "diffuse.dds"));
            var bc3Folder = Path.Combine(root, "bc3"); Directory.CreateDirectory(bc3Folder);
            await ModelPreparationTextures.ConvertAsync(texconv, png, bc3Folder, "BC3_UNORM", false, CancellationToken.None, separateAlpha: true);
            var bc3 = File.ReadAllBytes(Path.Combine(bc3Folder, "diffuse.dds"));
            var cube = PreviewFixtures.Dds().Take(128).Concat(new byte[6 * 128 * 128 * 8]).ToArray();
            BitConverter.GetBytes(512u).CopyTo(cube, 12); BitConverter.GetBytes(512u).CopyTo(cube, 16);
            BitConverter.GetBytes(0xFE00u).CopyTo(cube, 112);
            var small = PreviewFixtures.Bc7(); var plugin = new byte[] { 1, 2, 3, 4, 5 }; var nif = PreviewFixtures.Nif();
            var payload = new Dictionary<string, byte[]> { ["Test.esp"] = plugin, ["meshes/test.nif"] = nif,
                ["textures/a.dds"] = dds, ["textures/bc3.dds"] = bc3, ["textures/cube.dds"] = cube, ["textures/small.dds"] = small };
            var report = JsonSerializer.Serialize(new { SchemaVersion = 1, Files = payload.Select(item => new
                { Path = item.Key, Length = item.Value.Length, Sha256 = Convert.ToHexString(SHA256.HashData(item.Value)) }).ToArray() });
            var original = Path.Combine(root, "mod.zip");
            using (var zip = ZipFile.Open(original, ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in payload)
                { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
                using var writer = new StreamWriter(zip.CreateEntry("mod-report.json").Open()); writer.Write(report);
            }
            var before = await File.ReadAllBytesAsync(original);
            var variant = await TextureVariantArchiveService.CreateAsync(original, 256, texconv, Path.Combine(root, "game"));
            Assert.EndsWith("mod-256.zip", variant, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await File.ReadAllBytesAsync(original));
            using var result = ZipFile.OpenRead(variant);
            Assert.Equal(payload.Keys.Append("mod-report.json").Order(), result.Entries.Select(entry => entry.FullName).Order());
            foreach (var name in new[] { "Test.esp", "meshes/test.nif", "textures/small.dds", "textures/cube.dds" })
            {
                using var src = result.GetEntry(name)!.Open(); using var memory = new MemoryStream(); src.CopyTo(memory);
                Assert.Equal(payload[name], memory.ToArray());
            }
            var converted = Path.Combine(root, "result.dds"); result.GetEntry("textures/a.dds")!.ExtractToFile(converted);
            var info = DdsResourceInspector.Inspect(converted);
            Assert.Equal(("BC7", 256, 256, 9), (info.Format, info.Width, info.Height, info.MipLevels));
            Assert.Equal(99u, BitConverter.ToUInt32(File.ReadAllBytes(converted), 128));
            var convertedBc3 = Path.Combine(root, "bc3-result.dds"); result.GetEntry("textures/bc3.dds")!.ExtractToFile(convertedBc3);
            var bc3Info = DdsResourceInspector.Inspect(convertedBc3);
            Assert.Equal(("DXT5", 256, 256, 9), (bc3Info.Format, bc3Info.Width, bc3Info.Height, bc3Info.MipLevels));
            var preview = DdsPreviewDecoder.Decode("result", File.ReadAllBytes(converted), CancellationToken.None);
            Assert.InRange(preview.BgraPixels[3], 88, 104);
            using var reportStream = result.GetEntry("mod-report.json")!.Open(); using var document = JsonDocument.Parse(reportStream);
            Assert.Equal(256, document.RootElement.GetProperty("TextureVariant").GetProperty("MaximumDimension").GetInt32());
            var row = document.RootElement.GetProperty("Files").EnumerateArray().Single(row => row.GetProperty("Path").GetString() == "textures/a.dds");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(converted))), row.GetProperty("Sha256").GetString());
            var second = await TextureVariantArchiveService.CreateAsync(original, 256, texconv, Path.Combine(root, "game"));
            Assert.NotEqual(variant, second);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidOversizedDdsDoesNotPublishVariant()
    {
        var texconv = Environment.GetEnvironmentVariable("TEXCONV_PATH") ?? Path.Combine(Path.GetTempPath(), "skyrim-texconv-may2026.exe");
        if (!File.Exists(texconv)) return;
        var root = Path.Combine(Path.GetTempPath(), "skyrim-variant-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bad = PreviewFixtures.Bc7(); BitConverter.GetBytes(512u).CopyTo(bad, 12); BitConverter.GetBytes(512u).CopyTo(bad, 16);
            var original = Path.Combine(root, "mod.zip");
            using (var zip = ZipFile.Open(original, ZipArchiveMode.Create))
            { using var stream = zip.CreateEntry("textures/bad.dds").Open(); stream.Write(bad); }
            await Assert.ThrowsAnyAsync<Exception>(() => TextureVariantArchiveService.CreateAsync(original, 256, texconv, Path.Combine(root, "game")));
            Assert.False(File.Exists(Path.Combine(root, "mod-256.zip")));
            Assert.True(File.Exists(original));
        }
        finally { Directory.Delete(root, true); }
    }
}
