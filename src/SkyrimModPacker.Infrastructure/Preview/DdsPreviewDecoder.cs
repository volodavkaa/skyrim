using System.Buffers.Binary;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Preview;

/// <summary>Bounded top-mip BC1/BC2/BC3 decoder; other formats are explicitly unsupported.</summary>
public static class DdsPreviewDecoder
{
    public static PreviewTexture Decode(string path, byte[] data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var b = data.AsSpan();
        if (b.Length < 128 || !b[..4].SequenceEqual("DDS "u8) || U32(b, 4) != 124 || U32(b, 76) != 32) throw new InvalidDataException("Invalid DDS header.");
        var width = U32(b, 16); var height = U32(b, 12);
        if (width == 0 || height == 0 || width > 4096 || height > 4096 || (long)width * height > 4 * 1024 * 1024)
            throw new InvalidDataException("DDS preview supports at most 4 million pixels and 4096 per dimension.");
        if (U32(b, 112) != 0 || U32(b, 24) > 1) throw new NotSupportedException("Cubemap/volume DDS preview is not supported.");
        var offset = 128; var fourcc = b.Slice(84, 4);
        var format = fourcc.SequenceEqual("DXT1"u8) ? 1 : fourcc.SequenceEqual("DXT3"u8) ? 2 : fourcc.SequenceEqual("DXT5"u8) ? 3 : 0;
        if (fourcc.SequenceEqual("DX10"u8))
        {
            if (b.Length < 148) throw new InvalidDataException("Truncated DDS DX10 header.");
            if (U32(b, 132) != 3 || U32(b, 136) != 0 || U32(b, 140) != 1) throw new NotSupportedException("DDS arrays and cubemaps are not supported.");
            format = U32(b, 128) switch { 71 => 1, 74 => 2, 77 => 3, _ => 0 }; offset = 148;
        }
        if (format == 0) throw new NotSupportedException("DDS preview supports BC1/DXT1, BC2/DXT3 and BC3/DXT5 only; BC7 is not supported.");
        var blockSize = format == 1 ? 8 : 16; var columns = ((int)width + 3) / 4; var rows = ((int)height + 3) / 4;
        if (b.Length - offset < (long)columns * rows * blockSize) throw new InvalidDataException("DDS top mip is truncated.");
        var output = new byte[checked((int)(width * height * 4))];
        Span<byte> colors = stackalloc byte[16]; Span<byte> alphas = stackalloc byte[8];
        for (var y = 0; y < rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < columns; x++)
            {
                var block = b.Slice(offset + (y * columns + x) * blockSize, blockSize); var color = block[(format == 1 ? 0 : 8)..];
                var first = BinaryPrimitives.ReadUInt16LittleEndian(color); var second = BinaryPrimitives.ReadUInt16LittleEndian(color[2..]);
                Color(first, colors); Color(second, colors[4..]);
                for (var c = 0; c < 3; c++)
                {
                    colors[8 + c] = (byte)(first > second || format != 1 ? (2 * colors[c] + colors[4 + c]) / 3 : (colors[c] + colors[4 + c]) / 2);
                    colors[12 + c] = (byte)(first > second || format != 1 ? (colors[c] + 2 * colors[4 + c]) / 3 : 0);
                }
                colors[3] = colors[7] = colors[11] = 255; colors[15] = first > second || format != 1 ? (byte)255 : (byte)0;
                var codes = BinaryPrimitives.ReadUInt32LittleEndian(color[4..]); ulong alphaCodes = 0;
                if (format == 3)
                {
                    alphas[0] = block[0]; alphas[1] = block[1];
                    for (var i = 2; i < 8; i++) alphas[i] = block[0] > block[1] ? (byte)(((8 - i) * block[0] + (i - 1) * block[1]) / 7)
                        : i < 6 ? (byte)(((6 - i) * block[0] + (i - 1) * block[1]) / 5) : i == 6 ? (byte)0 : (byte)255;
                    for (var i = 0; i < 6; i++) alphaCodes |= (ulong)block[2 + i] << (i * 8);
                }
                for (var i = 0; i < 16; i++)
                {
                    var px = x * 4 + i % 4; var py = y * 4 + i / 4; if (px >= width || py >= height) continue;
                    var chosen = (int)((codes >> (i * 2)) & 3) * 4; var target = checked((py * (int)width + px) * 4);
                    colors.Slice(chosen, 4).CopyTo(output.AsSpan(target, 4));
                    if (format == 2) output[target + 3] = (byte)(((block[i / 2] >> ((i % 2) * 4)) & 15) * 17);
                    if (format == 3) output[target + 3] = alphas[(int)((alphaCodes >> (3 * i)) & 7)];
                }
            }
        }
        return new(path, (int)width, (int)height, output);
    }
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    private static void Color(ushort value, Span<byte> result)
    {
        var blue = value & 31; var green = (value >> 5) & 63; var red = (value >> 11) & 31;
        result[0] = (byte)((blue << 3) | (blue >> 2)); result[1] = (byte)((green << 2) | (green >> 4)); result[2] = (byte)((red << 3) | (red >> 2));
    }
}
