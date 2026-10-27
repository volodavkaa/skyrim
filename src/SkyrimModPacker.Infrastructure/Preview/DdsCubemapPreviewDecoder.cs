using System.Buffers.Binary;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Preview;

/// <summary>Decodes the six top mip faces of a complete DDS cubemap for the editor preview.</summary>
public static class DdsCubemapPreviewDecoder
{
    public static IReadOnlyList<PreviewTexture> Decode(string path, byte[] data, CancellationToken token = default)
    {
        var bytes = data.AsSpan();
        if (bytes.Length < 128 || !bytes[..4].SequenceEqual("DDS "u8))
            throw new InvalidDataException("Invalid cubemap DDS header.");
        var width = checked((int)Read(data, 16));
        var height = checked((int)Read(data, 12));
        var mips = checked((int)Math.Max(1, Read(data, 28)));
        if (width != height || width is < 1 or > 16384 || mips > 15)
            throw new InvalidDataException("Cubemap preview requires square faces up to 16384 pixels.");
        var dx10 = bytes.Slice(84, 4).SequenceEqual("DX10"u8);
        var headerSize = dx10 ? 148 : 128;
        if (bytes.Length < headerSize) throw new InvalidDataException("Truncated cubemap header.");
        if (dx10 ? (Read(data, 136) & 4) == 0 || Read(data, 140) != 1 : (Read(data, 112) & 0xFE00) != 0xFE00)
            throw new InvalidDataException("DDS does not contain six cubemap faces.");
        var blockBytes = dx10 ? Read(data, 128) switch
        {
            71 or 72 => 8, 74 or 75 or 77 or 78 or 98 or 99 => 16,
            _ => throw new NotSupportedException("Cubemap preview supports BC1, BC2, BC3 and BC7.")
        } : System.Text.Encoding.ASCII.GetString(bytes.Slice(84, 4)) switch
        {
            "DXT1" => 8, "DXT3" or "DXT5" => 16,
            _ => throw new NotSupportedException("Cubemap preview supports DXT1, DXT3 and DXT5.")
        };
        var selectedMip = 0;
        while (selectedMip + 1 < mips && (width >> selectedMip) > 256) selectedMip++;
        var selectedSize = Math.Max(1, width >> selectedMip);
        if (selectedSize > 1024)
            throw new NotSupportedException("Cubemap preview needs a mip level of 1024 pixels or smaller.");
        var top = ((selectedSize + 3) / 4) * ((selectedSize + 3) / 4) * blockBytes;
        var stride = 0;
        var selectedOffset = 0;
        for (var mip = 0; mip < mips; mip++)
        {
            var size = Math.Max(1, width >> mip);
            if (mip == selectedMip) selectedOffset = stride;
            stride = checked(stride + ((size + 3) / 4) * ((size + 3) / 4) * blockBytes);
        }
        if ((long)headerSize + 6L * stride > data.Length)
            throw new InvalidDataException("Cubemap face data is truncated.");
        var faces = new List<PreviewTexture>(6);
        for (var face = 0; face < 6; face++)
        {
            token.ThrowIfCancellationRequested();
            var single = new byte[headerSize + top];
            bytes[..headerSize].CopyTo(single);
            BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(12), (uint)selectedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(16), (uint)selectedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(28), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(112), 0);
            if (dx10) BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(136), 0);
            bytes.Slice(headerSize + face * stride + selectedOffset, top).CopyTo(single.AsSpan(headerSize));
            faces.Add(DdsPreviewDecoder.Decode(path + "#" + face, single, token));
        }
        return faces;
    }

    private static uint Read(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
}
