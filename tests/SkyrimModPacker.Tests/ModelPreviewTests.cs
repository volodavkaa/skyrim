using System.Numerics;
using System.Text.Json;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class ModelPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModelPreviewTests", Guid.NewGuid().ToString("N"));
    public ModelPreviewTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void StaticNif_PreservesIndicesUvAndParentRotationTranslationScale()
    {
        var source = PreviewFixtures.Nif(); var original = source.ToArray(); var scene = SseNifReader.Read(source); var mesh = Assert.Single(scene.Meshes);
        Assert.Equal("Blade", mesh.Name); Assert.Equal(new Vector3(7, 4, 4), mesh.Positions[1]);
        Assert.Equal(new Vector2(1, 0), mesh.TextureCoordinates[1]); Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
        Assert.Equal("textures/test.dds", mesh.DiffuseTexture); Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("LE")] [InlineData("skin")] [InlineData("animated")] [InlineData("unsupported")]
    [InlineData("nan")] [InlineData("index")] [InlineData("cycle")] [InlineData("root")]
    public void UnsupportedOrCorruptNifIsRejected(string scenario) => Assert.ThrowsAny<Exception>(() => SseNifReader.Read(PreviewFixtures.Nif(scenario)));

    [Theory]
    [InlineData(0)] [InlineData(40)] [InlineData(200)]
    public void TruncatedNifIsRejected(int length) => Assert.ThrowsAny<Exception>(() => SseNifReader.Read(PreviewFixtures.Nif()[..length]));

    [Theory]
    [InlineData("DXT1")] [InlineData("DXT3")] [InlineData("DXT5")]
    public void DdsDecodesKnown565PaletteAndAlpha(string format)
    {
        var texture = DdsPreviewDecoder.Decode("test", PreviewFixtures.Dds(format));
        Assert.Equal(4, texture.Width); Assert.Equal(64, texture.BgraPixels.Length);
        Assert.Equal(new byte[] { 0, 0, 255 }, texture.BgraPixels[..3]); Assert.Equal(new byte[] { 255, 0, 0 }, texture.BgraPixels[4..7]);
        Assert.Equal((byte)85, texture.BgraPixels[8]); Assert.Equal((byte)170, texture.BgraPixels[10]);
        Assert.Equal(format == "DXT1" ? 255 : format == "DXT3" ? 0 : 218, texture.BgraPixels[3]);
    }

    [Fact]
    public void DdsTransparencyAndUnsupportedAndTruncatedPayloadAreHandled()
    {
        var transparent = DdsPreviewDecoder.Decode("test", PreviewFixtures.Dds("DXT1", 0, 0xFFFF, uint.MaxValue)); Assert.Equal(0, transparent.BgraPixels[3]);
        Assert.Throws<NotSupportedException>(() => DdsPreviewDecoder.Decode("test", PreviewFixtures.Dds("BC7 ")));
        Assert.Throws<InvalidDataException>(() => DdsPreviewDecoder.Decode("test", PreviewFixtures.Dds()[..130]));
        var huge = PreviewFixtures.Dds(); BitConverter.GetBytes(1000000u).CopyTo(huge, 16);
        Assert.Throws<InvalidDataException>(() => DdsPreviewDecoder.Decode("test", huge));
    }

    [Theory]
    [InlineData("DXT1", 71u)] [InlineData("DXT3", 74u)] [InlineData("DXT5", 77u)]
    public void Dx10BcFormatsMatchLegacyPixelBuffers(string format, uint dxgi)
    {
        var legacy = PreviewFixtures.Dds(format); var extended = new byte[legacy.Length + 20];
        Array.Copy(legacy, extended, 128); Array.Copy(legacy, 128, extended, 148, legacy.Length - 128);
        System.Text.Encoding.ASCII.GetBytes("DX10").CopyTo(extended, 84); BitConverter.GetBytes(dxgi).CopyTo(extended, 128);
        BitConverter.GetBytes(3u).CopyTo(extended, 132); BitConverter.GetBytes(1u).CopyTo(extended, 140);
        Assert.Equal(DdsPreviewDecoder.Decode("legacy", legacy).BgraPixels, DdsPreviewDecoder.Decode("dx10", extended).BgraPixels);
        BitConverter.GetBytes(2u).CopyTo(extended, 140);
        Assert.Throws<NotSupportedException>(() => DdsPreviewDecoder.Decode("array", extended));
    }

    [Fact]
    public void DdsExplicitAlphaAndBc3SixEntryAlphaBranchAreDecoded()
    {
        var bc2 = PreviewFixtures.Dds("DXT3", 0, 0xFFFF, uint.MaxValue); Array.Fill(bc2, (byte)0x88, 128, 8);
        var pixels = DdsPreviewDecoder.Decode("bc2", bc2).BgraPixels;
        Assert.Equal(new byte[] { 170, 170, 170, 136 }, pixels[..4]);
        var bc3 = PreviewFixtures.Dds("DXT5"); bc3[128] = 0; bc3[129] = 255; bc3[130] = 6 | (7 << 3);
        pixels = DdsPreviewDecoder.Decode("bc3", bc3).BgraPixels; Assert.Equal(0, pixels[3]); Assert.Equal(255, pixels[7]);
    }

    [Fact]
    public void DdsPartialEdgeBlocksAreClippedToDeclaredSize()
    {
        var data = PreviewFixtures.Dds(); BitConverter.GetBytes(3u).CopyTo(data, 16); BitConverter.GetBytes(2u).CopyTo(data, 12);
        var decoded = DdsPreviewDecoder.Decode("edge", data); Assert.Equal(24, decoded.BgraPixels.Length); Assert.Equal(3, decoded.Width); Assert.Equal(2, decoded.Height);
    }

    [Fact]
    public void NifExcessiveHeaderCountsFailBeforeAllocation()
    {
        var bytes = PreviewFixtures.Nif(); var offset = System.Text.Encoding.ASCII.GetByteCount("Gamebryo File Format, Version 20.2.0.7\n") + 9;
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, offset);
        Assert.Throws<InvalidDataException>(() => SseNifReader.Read(bytes));
    }

    [Fact]
    public async Task LooseProjectModelAndDiffuseLoadWithoutChangingSources()
    {
        Directory.CreateDirectory(Path.Combine(_root, "meshes")); Directory.CreateDirectory(Path.Combine(_root, "textures"));
        var nif = PreviewFixtures.Nif(); var dds = PreviewFixtures.Dds();
        await File.WriteAllBytesAsync(Path.Combine(_root, "meshes/test.nif"), nif); await File.WriteAllBytesAsync(Path.Combine(_root, "textures/test.dds"), dds);
        var result = await new ModelPreviewService().LoadAsync(_root, "test.nif", _root);
        Assert.Single(result.Meshes); Assert.Single(result.Textures);
        Assert.Equal(nif, File.ReadAllBytes(Path.Combine(_root, "meshes/test.nif"))); Assert.Equal(dds, File.ReadAllBytes(Path.Combine(_root, "textures/test.dds")));
    }

    [Theory]
    [InlineData("../escape.nif")] [InlineData("C:/escape.nif")] [InlineData("test.dds")]
    public async Task UnsafeModelPathsAreRejected(string path) => await Assert.ThrowsAnyAsync<Exception>(() => new ModelPreviewService().LoadAsync(_root, path, _root));

    [Fact]
    public async Task MissingRegisteredModelNeverFallsBackToDifferentDataCopy()
    {
        var project = Path.Combine(_root, "project"); var data = Path.Combine(_root, "data"); Directory.CreateDirectory(project); Directory.CreateDirectory(Path.Combine(data, "meshes"));
        await File.WriteAllTextAsync(Path.Combine(project, "mod-project.json"), JsonSerializer.Serialize(new { Resources = new[] { "meshes/test.nif" } }));
        await File.WriteAllBytesAsync(Path.Combine(data, "meshes/test.nif"), PreviewFixtures.Nif());
        await Assert.ThrowsAsync<FileNotFoundException>(() => new ModelPreviewService().LoadAsync(project, "test.nif", data));
    }

    [Fact]
    public async Task CorruptBsaIsReportedAsReadFailure_AndCancellationPropagates()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Skyrim-Meshes.bsa"), "broken");
        var error = await Assert.ThrowsAsync<IOException>(() => new ModelPreviewService().LoadAsync("", "test.nif", _root)); Assert.Contains("BSA read failed", error.Message);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelPreviewService().LoadAsync("", "test.nif", _root, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SseNifReader.Read(PreviewFixtures.Nif(), cts.Token));
    }
}
