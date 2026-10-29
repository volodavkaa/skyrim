using System.Buffers.Binary;
using System.IO.Compression;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

public sealed record TextureSizeEstimate(int SourceWidth, int SourceHeight, int OutputWidth,
    int OutputHeight, int MipLevels, long Bc7Bytes);

/// <summary>Reads image dimensions without decoding pixels and estimates a complete BC7 mip chain.</summary>
public static class TextureSizePlanner
{
    public static TextureSizeEstimate Inspect(string archivePath, string source, int maxSize = 0)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Texture path is empty.");
        if (Path.IsPathFullyQualified(source))
        {
            var path = PackingPathGuard.GetAbsolutePath(source); PackingPathGuard.EnsureNoLinks(path);
            using var file = File.OpenRead(path);
            return InspectStream(file, file.Length, Path.GetExtension(path), maxSize);
        }
        using var zip = ZipFile.OpenRead(archivePath);
        var matches = zip.Entries.Where(item => !string.IsNullOrEmpty(item.Name) &&
            item.FullName.Equals(source, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Texture in ZIP is missing or ambiguous: " + source);
        using var stream = matches[0].Open();
        return InspectStream(stream, matches[0].Length, Path.GetExtension(matches[0].Name), maxSize);
    }

    public static TextureSizeEstimate Plan(int width, int height, int maxSize = 0)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384) throw new InvalidDataException("Texture dimensions must be between 1 and 16384.");
        if (maxSize != 0 && (maxSize is < 4 or > 16384)) throw new ArgumentOutOfRangeException(nameof(maxSize));
        var outWidth = width; var outHeight = height;
        if (maxSize > 0 && Math.Max(width, height) > maxSize)
        {
            var ratio = (double)maxSize / Math.Max(width, height);
            outWidth = Math.Max(1, (int)Math.Round(width * ratio, MidpointRounding.AwayFromZero));
            outHeight = Math.Max(1, (int)Math.Round(height * ratio, MidpointRounding.AwayFromZero));
        }
        long bytes = 0; var levels = 0; var w = outWidth; var h = outHeight;
        do
        {
            bytes = checked(bytes + (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16);
            levels++; if (w == 1 && h == 1) break;
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        } while (true);
        return new(width, height, outWidth, outHeight, levels, bytes);
    }

    private static TextureSizeEstimate InspectStream(Stream stream, long length, string extension, int maxSize)
    {
        if (length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Image size is outside the 64 MiB limit.");
        // A bounded copy permits seeking through JPEG segments and prevents archive decompression bombs.
        using var data = new MemoryStream();
        var buffer = new byte[81920]; long copied = 0; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += count;
            if (copied > 64 * 1024 * 1024) throw new InvalidDataException("Image exceeds the 64 MiB limit.");
            data.Write(buffer, 0, count);
        }
        if (copied != length) throw new InvalidDataException("Image length changed while reading.");
        var bytes = data.GetBuffer().AsSpan(0, checked((int)data.Length));
        int width, height;
        switch (extension.ToLowerInvariant())
        {
            case ".png" when bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8):
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])); break;
            case ".dds" when bytes.Length >= 128 && bytes[..4].SequenceEqual("DDS "u8) && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 124:
                width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
                height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..])); break;
            case ".bmp" when bytes.Length >= 26 && bytes[..2].SequenceEqual("BM"u8):
                var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
                if (header == 12) { width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]); height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]); }
                else if (header >= 40) { width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]); var h = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]); height = h == int.MinValue ? 0 : Math.Abs(h); }
                else throw new InvalidDataException("Unsupported BMP header.");
                break;
            case ".tga" when bytes.Length >= 18 && bytes[2] is 1 or 2 or 3 or 9 or 10 or 11:
                width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]); height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]); break;
            case ".jpg" or ".jpeg": (width, height) = ReadJpeg(bytes); break;
            default: throw new InvalidDataException("Cannot read image dimensions for " + extension);
        }
        return Plan(width, height, maxSize);
    }

    private static (int Width, int Height) ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) throw new InvalidDataException("Invalid JPEG header.");
        var i = 2;
        while (i < bytes.Length)
        {
            if (bytes[i++] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            while (i < bytes.Length && bytes[i] == 0xff) i++;
            if (i >= bytes.Length) break;
            var marker = bytes[i++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (i + 2 > bytes.Length) break;
            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[i..]);
            if (size < 2 || i + size > bytes.Length) throw new InvalidDataException("Invalid JPEG segment.");
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (size < 8) throw new InvalidDataException("Invalid JPEG frame.");
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 3)..]));
            }
            i += size;
        }
        throw new InvalidDataException("JPEG dimensions not found.");
    }
}
