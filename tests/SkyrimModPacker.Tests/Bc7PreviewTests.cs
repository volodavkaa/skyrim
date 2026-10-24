using System.Security.Cryptography;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class Bc7PreviewTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void AllBc7ModesDecodeConstantBlack(int mode)
    {
        var bytes = PreviewFixtures.Bc7(); Array.Clear(bytes, 148, 16); bytes[148] = (byte)(1 << mode);
        var texture = DdsPreviewDecoder.Decode("mode.dds", bytes);
        for (var i = 0; i < 16; i++) Assert.Equal(new byte[] { 0, 0, 0, (byte)(mode < 4 ? 255 : 0) }, texture.BgraPixels[(i * 4)..(i * 4 + 4)]);
    }
    private static byte[] SolidMode6()
    {
        var data = PreviewFixtures.Bc7(); var bit = 148 * 8;
        void Bits(uint value, int count) { for (var i = 0; i < count; i++, bit++) if ((value & (1u << i)) != 0) data[bit / 8] |= (byte)(1 << (bit % 8)); }
        Array.Clear(data, 148, 16); Bits(64, 7);
        foreach (var value in new uint[] { 127, 127, 0, 0, 0, 0, 127, 127 }) Bits(value, 7);
        Bits(1, 1); Bits(1, 1); Bits(0, 3); for (var i = 1; i < 16; i++) Bits(0, 4);
        return data; // RGBA = (255, 1, 1, 255), same endpoint at both ends.
    }
    [Theory]
    [InlineData(98u)] [InlineData(99u)]
    public void Bc7PreviewPreservesKnownColourAlphaAndSrgbEncodedBytes(uint format)
    {
        var bytes = SolidMode6(); BitConverter.GetBytes(format).CopyTo(bytes, 128); var original = bytes.ToArray();
        var texture = DdsPreviewDecoder.Decode("solid.dds", bytes);
        Assert.Equal(4, texture.Width); Assert.Equal(4, texture.Height); Assert.Equal(64, texture.BgraPixels.Length);
        for (var i = 0; i < 16; i++) Assert.Equal(new byte[] { 1, 1, 255, 255 }, texture.BgraPixels[(i * 4)..(i * 4 + 4)]);
        Assert.Equal(original, bytes);
    }
    [Fact]
    public void Bc7PreviewClipsPartialEdgeBlocksAndRejectsBadDataAndCancellation()
    {
        var bytes = SolidMode6(); BitConverter.GetBytes(3u).CopyTo(bytes, 16); BitConverter.GetBytes(2u).CopyTo(bytes, 12);
        Assert.Equal(24, DdsPreviewDecoder.Decode("edge.dds", bytes).BgraPixels.Length);
        Assert.Throws<InvalidDataException>(() => DdsPreviewDecoder.Decode("short.dds", bytes[..^1]));
        bytes[148] = 0; Assert.Throws<InvalidDataException>(() => DdsPreviewDecoder.Decode("bad.dds", bytes));
        using var token = new CancellationTokenSource(); token.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DdsPreviewDecoder.Decode("cancel.dds", bytes, token.Token));
    }
    [Fact]
    public void Bc7FourKPreviewStaysWithinFourMillionDecodedPixels()
    {
        var bytes = new byte[148 + 4096 * 4096]; Array.Copy(PreviewFixtures.Bc7(), bytes, 148);
        BitConverter.GetBytes(4096u).CopyTo(bytes, 12); BitConverter.GetBytes(4096u).CopyTo(bytes, 16);
        for (var i = 148; i < bytes.Length; i += 16) bytes[i] = 64;
        var texture = DdsPreviewDecoder.Decode("4k.dds", bytes); Assert.Equal(2048, texture.Width); Assert.Equal(2048, texture.Height);
        Assert.Equal(2048 * 2048 * 4, texture.BgraPixels.Length); Assert.Equal(4096u, BitConverter.ToUInt32(bytes, 12));
    }
    [Fact]
    public void AvailableRealBc7TexturesDecodeWithoutModification()
    {
        var root = Environment.GetEnvironmentVariable("SKYRIM_LE_PROJECT"); if (string.IsNullOrWhiteSpace(root)) return;
        var files = Directory.GetFiles(Path.Combine(root, "textures"), "*.dds", SearchOption.AllDirectories).Where(f => DdsResourceInspector.Inspect(f).Format == "BC7").ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file); var hash = SHA256.HashData(bytes); var texture = DdsPreviewDecoder.Decode(file, bytes);
            Assert.Equal(texture.Width * texture.Height * 4, texture.BgraPixels.Length);
            Assert.True(texture.BgraPixels.Where((_, i) => i % 4 != 3).Distinct().Take(20).Count() > 10);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(file)));
        }
    }
}
