using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyrimModPacker.Infrastructure.ModelPreparation;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ModelPreparationTextureConversionTests
{
    [Fact]
    public void EstimatesCompleteBc7MipChainAndNeverUpscales()
    {
        var estimate = TextureSizePlanner.Plan(4096, 2048, 2048);
        Assert.Equal(2048, estimate.OutputWidth);
        Assert.Equal(1024, estimate.OutputHeight);
        Assert.Equal(12, estimate.MipLevels);
        Assert.Equal(2796240, estimate.Bc7Bytes);
        var small = TextureSizePlanner.Plan(512, 256, 2048);
        Assert.Equal((512, 256), (small.OutputWidth, small.OutputHeight));
        Assert.Equal(80, TextureSizePlanner.Plan(8, 4).Bc7Bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureSizePlanner.Plan(8, 8, 2));
    }

    [Fact]
    public void ReadsDimensionsFromPngJpegBmpTgaAndDdsInZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "skyrim-size-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "textures.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddImage(archive, "color.png", 255, 16, 8);
                var pixels = new byte[16 * 8 * 4];
                var bitmap = BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
                foreach (var (name, encoder) in new (string, BitmapEncoder)[] { ("color.jpg", new JpegBitmapEncoder()), ("color.bmp", new BmpBitmapEncoder()) })
                {
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var buffer = new MemoryStream(); encoder.Save(buffer); buffer.Position = 0;
                    using var output = archive.CreateEntry(name).Open(); buffer.CopyTo(output);
                }
                var tga = new byte[18 + 16 * 8 * 4]; tga[2] = 2; tga[12] = 16; tga[14] = 8; tga[16] = 32;
                using (var output = archive.CreateEntry("color.tga").Open()) output.Write(tga);
                using (var output = archive.CreateEntry("color.dds").Open()) output.Write(PreviewFixtures.Bc7());
            }
            foreach (var ext in new[] { "png", "jpg", "bmp", "tga" })
            {
                var size = TextureSizePlanner.Inspect(path, "color." + ext, 8);
                Assert.Equal((16, 8, 8, 4), (size.SourceWidth, size.SourceHeight, size.OutputWidth, size.OutputHeight));
            }
            var dds = TextureSizePlanner.Inspect(path, "color.dds");
            Assert.True(dds.SourceWidth > 0 && dds.SourceHeight > 0);
            Assert.Throws<InvalidDataException>(() => TextureSizePlanner.Inspect(path, "missing.png"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ResizeMaintainsAspectRatioAndBc7MipChainForZipAndDisk()
    {
        var texconv = Environment.GetEnvironmentVariable("TEXCONV_PATH") ??
            Path.Combine(Path.GetTempPath(), "skyrim-texconv-may2026.exe");
        if (!File.Exists(texconv)) return;
        var root = Path.Combine(Path.GetTempPath(), "skyrim-resize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "input.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddImage(archive, "color.png", 64, 32, 16);
                AddImage(archive, "normal.png", 255, 16, 8);
                AddImage(archive, "metal.png", 192, 4, 4);
            }
            var normal = Path.Combine(root, "normal.png");
            using (var archive = ZipFile.OpenRead(zip)) archive.GetEntry("normal.png")!.ExtractToFile(normal);
            var before = File.ReadAllBytes(normal);
            var plan = TextureSizePlanner.Inspect(zip, "color.png", 8);
            Assert.Equal((32, 16, 8, 4, 4, 80L),
                (plan.SourceWidth, plan.SourceHeight, plan.OutputWidth, plan.OutputHeight, plan.MipLevels, plan.Bc7Bytes));
            var normalPlan = TextureSizePlanner.Inspect(zip, normal, 8);
            Assert.Equal((16, 8, 8, 4), (normalPlan.SourceWidth, normalPlan.SourceHeight, normalPlan.OutputWidth, normalPlan.OutputHeight));
            var stage = Path.Combine(root, "stage");
            var result = await ModelPreparationTextures.PrepareAsync(zip, stage, "blade",
                new("color.png", normal, texconv, MetallicEntry: "metal.png", MaxTextureSize: 8), CancellationToken.None, root);
            foreach (var (path, expectedWidth, expectedHeight, expectedMips) in new[]
                     { (result.Diffuse, 8, 4, 4), (result.Normal, 8, 4, 4), (result.EnvMask!, 4, 4, 3) })
            {
                var output = Path.Combine(stage, path);
                var info = DdsResourceInspector.Inspect(output);
                Assert.Equal((expectedWidth, expectedHeight, expectedMips), (info.Width, info.Height, info.MipLevels));
                var outputPlan = TextureSizePlanner.Plan(expectedWidth, expectedHeight);
                Assert.Equal(outputPlan.Bc7Bytes + 148, new FileInfo(output).Length);
            }
            Assert.Equal(before, File.ReadAllBytes(normal));
            var diffuse = DdsPreviewDecoder.Decode("diffuse", File.ReadAllBytes(Path.Combine(stage, result.Diffuse)), CancellationToken.None);
            Assert.InRange(diffuse.BgraPixels[3], 55, 73);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ResizeHandlesNonSquareDimensionsNotDivisibleByFour()
    {
        var texconv = Environment.GetEnvironmentVariable("TEXCONV_PATH") ??
            Path.Combine(Path.GetTempPath(), "skyrim-texconv-may2026.exe");
        if (!File.Exists(texconv)) return;
        var root = Path.Combine(Path.GetTempPath(), "skyrim-resize-odd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "images.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddImage(archive, "color.png", 128, 32, 20);
                AddImage(archive, "normal.png", 255, 32, 20);
            }
            var stage = Path.Combine(root, "stage");
            var result = await ModelPreparationTextures.PrepareAsync(zip, stage, "blade",
                new("color.png", "normal.png", texconv, MaxTextureSize: 8), CancellationToken.None, root);
            var info = DdsResourceInspector.Inspect(Path.Combine(stage, result.Diffuse));
            Assert.Equal((8, 5, 4), (info.Width, info.Height, info.MipLevels));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TexconvProducesBc7MipmapsAndPreservesDiffuseTransparency()
    {
        // Set TEXCONV_PATH to run this integration test on machines without the local tool.
        var texconv = Environment.GetEnvironmentVariable("TEXCONV_PATH") ??
            Path.Combine(Path.GetTempPath(), "skyrim-texconv-may2026.exe");
        if (!File.Exists(texconv)) return;

        var root = Path.Combine(Path.GetTempPath(), "skyrim-texture-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "source.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddImage(archive, "diffuse.png", 64);
                AddImage(archive, "normal.png", 255);
                AddImage(archive, "metallic.png", 192);
            }
            var staging = Path.Combine(root, "stage");
            var result = await ModelPreparationTextures.PrepareAsync(zip, staging, "blade",
                new("diffuse.png", "normal.png", texconv, MetallicEntry: "metallic.png"),
                CancellationToken.None, root);

            foreach (var (relative, dxgi) in new[] { (result.Diffuse, 99u), (result.Normal, 98u), (result.EnvMask!, 98u) })
            {
                var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                var info = DdsResourceInspector.Inspect(path);
                Assert.Equal("BC7", info.Format);
                Assert.Equal(4, info.MipLevels);
                Assert.Equal(dxgi, BitConverter.ToUInt32(File.ReadAllBytes(path), 128));
            }
            var diffusePath = Path.Combine(staging, result.Diffuse.Replace('/', Path.DirectorySeparatorChar));
            var diffuse = DdsPreviewDecoder.Decode(diffusePath, File.ReadAllBytes(diffusePath), CancellationToken.None);
            Assert.InRange(diffuse.BgraPixels[3], 55, 73);
            Assert.False(Directory.Exists(Path.Combine(staging, ".source-textures")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MixedZipAndExternalTexturePathsConvertWithoutChangingSources()
    {
        var texconv = Environment.GetEnvironmentVariable("TEXCONV_PATH") ??
            Path.Combine(Path.GetTempPath(), "skyrim-texconv-may2026.exe");
        if (!File.Exists(texconv)) return;
        var root = Path.Combine(Path.GetTempPath(), "skyrim-external-texture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "source.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddImage(archive, "images/diffuse.png", 64);
                AddImage(archive, "normal.png", 255);
                AddImage(archive, "metal.png", 192);
            }
            var normal = Path.Combine(root, "custom normal.png");
            var metallic = Path.Combine(root, "custom metal.png");
            using (var archive = ZipFile.OpenRead(zip))
            {
                archive.GetEntry("normal.png")!.ExtractToFile(normal);
                archive.GetEntry("metal.png")!.ExtractToFile(metallic);
            }
            var originalNormal = File.ReadAllBytes(normal);
            var originalMetal = File.ReadAllBytes(metallic);
            var stage = Path.Combine(root, "stage");
            var result = await ModelPreparationTextures.PrepareAsync(zip, stage, "blade",
                new("images/diffuse.png", normal, texconv, MetallicEntry: metallic), CancellationToken.None, root);
            Assert.Equal("BC7", DdsResourceInspector.Inspect(Path.Combine(stage, result.Normal)).Format);
            Assert.Equal("BC7", DdsResourceInspector.Inspect(Path.Combine(stage, result.EnvMask!)).Format);
            Assert.Equal(originalNormal, File.ReadAllBytes(normal));
            Assert.Equal(originalMetal, File.ReadAllBytes(metallic));
            Assert.False(Directory.Exists(Path.Combine(stage, ".source-textures")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AddImage(ZipArchive archive, string name, byte alpha, int width = 8, int height = 8)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 60; pixels[i + 1] = 120; pixels[i + 2] = 180; pixels[i + 3] = alpha;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        using var stream = archive.CreateEntry(name).Open();
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }
}
