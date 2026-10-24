using System.Buffers.Binary;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.Preview;

public sealed record DdsResourceInfo(string Format, int Width, int Height, int MipLevels);

/// <summary>SE/AE resource validation independent of the deliberately limited CPU preview decoder.</summary>
public static class DdsResourceInspector
{
    public static DdsResourceInfo Inspect(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            PackingPathGuard.EnsureNoLinks(path);
            using var input = File.OpenRead(path); Span<byte> header = stackalloc byte[148];
            input.ReadExactly(header[..128]);
            if (!header[..4].SequenceEqual("DDS "u8) || Read(header, 4) != 124 || Read(header, 76) != 32) throw new InvalidDataException("Некоректний заголовок DDS / Invalid DDS header.");
            var width = Read(header, 16); var height = Read(header, 12); var mips = Math.Max(1, Read(header, 28));
            if (width is 0 or > 16384 || height is 0 or > 16384 || mips > 1 + (int)Math.Log2(Math.Max(width, height))) throw new InvalidDataException("Некоректні розміри або кількість mip-рівнів / Invalid dimensions or mip count.");
            if (Read(header, 24) > 1 || (Read(header, 112) & 0x200000) != 0) throw new NotSupportedException("Об’ємні DDS не підтримуються / Volume DDS is unsupported.");
            var code = System.Text.Encoding.ASCII.GetString(header.Slice(84, 4)); var bytes = 0; var pixelBytes = 0; var format = code; var faces = 1; var bc7 = false;
            if (code == "DX10")
            {
                input.ReadExactly(header[128..]); var dxgi = Read(header, 128);
                if (Read(header, 132) != 3 || Read(header, 140) != 1) throw new NotSupportedException("Потрібна 2D DDS без масиву / Only 2D DDS without arrays is supported.");
                faces = (Read(header, 136) & 4) != 0 ? 6 : 1;
                (format, bytes, pixelBytes) = dxgi switch
                {
                    71 or 72 => ("BC1", 8, 0), 74 or 75 => ("BC2", 16, 0), 77 or 78 => ("BC3", 16, 0),
                    80 or 81 => ("BC4", 8, 0), 83 or 84 => ("BC5", 16, 0), 98 or 99 => ("BC7", 16, 0),
                    28 or 29 or 87 or 88 or 91 or 93 => ("RGBA8", 0, 4), 61 => ("R8", 0, 1),
                    _ => throw new NotSupportedException("Непідтримуваний DDS DXGI формат / Unsupported DDS DXGI format: " + dxgi)
                };
                bc7 = dxgi is 98 or 99;
            }
            else if ((Read(header, 80) & 4) != 0)
                bytes = code switch { "DXT1" or "ATI1" or "BC4U" or "BC4S" => 8, "DXT3" or "DXT5" or "ATI2" or "BC5U" or "BC5S" => 16,
                    _ => throw new NotSupportedException("Непідтримуваний DDS FourCC / Unsupported DDS FourCC: " + code) };
            else
            {
                var bits = Read(header, 88); var flags = Read(header, 80);
                if ((flags & 0x40) != 0 && bits is 24 or 32) pixelBytes = (int)bits / 8;
                else if ((flags & (0x20000 | 2)) != 0 && bits == 8) pixelBytes = 1;
                else throw new NotSupportedException("Непідтримувана розкладка DDS / Unsupported DDS pixel layout.");
                format = "RGB/Luminance " + bits;
            }
            if (code != "DX10" && (Read(header, 112) & 0x200) != 0)
            {
                if ((Read(header, 112) & 0xFC00) != 0xFC00) throw new InvalidDataException("Неповна DDS cubemap / Incomplete DDS cubemap.");
                faces = 6;
            }
            long required = 0; var w = width; var h = height;
            for (var mip = 0; mip < mips; mip++)
            { required += bytes > 0 ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * bytes : (long)w * h * pixelBytes; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
            required *= faces;
            if (input.Length - input.Position < required) throw new InvalidDataException("Обрізані дані DDS або mip-рівні / Truncated DDS data or mip levels.");
            if (bc7)
            {
                var buffer = new byte[65536]; long remaining = required;
                while (remaining > 0)
                { token.ThrowIfCancellationRequested(); var size = (int)Math.Min(buffer.Length, remaining); input.ReadExactly(buffer.AsSpan(0, size)); for (var i = 0; i < size; i += 16) if (buffer[i] == 0) throw new InvalidDataException("Пошкоджений блок BC7: не вказано режим / Invalid BC7 block mode."); remaining -= size; }
            }
            return new(format, (int)width, (int)height, (int)mips);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        { throw new InvalidDataException(Path.GetFileName(path) + ": " + ex.Message, ex); }
    }
    private static uint Read(ReadOnlySpan<byte> header, int at) => BinaryPrimitives.ReadUInt32LittleEndian(header[at..]);
}
