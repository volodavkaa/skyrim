using System.Text;
using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.Preview;

public enum NifFormatFamily { Unknown, SkyrimLe, SkyrimSe }
public sealed record NifHeaderInfo(NifFormatFamily Family, uint? StreamVersion);

/// <summary>Read-only format identification; neither validation for the game nor conversion.</summary>
public static class NifCompatibilityInspector
{
    public static NifHeaderInfo Read(string path)
    {
        PackingPathGuard.EnsureNoLinks(path);
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream, Encoding.UTF8);
        var header = new StringBuilder();
        while (header.Length < 128) { var value = reader.ReadByte(); if (value == 10) break; header.Append((char)value); }
        if (header.ToString().TrimEnd('\r') != "Gamebryo File Format, Version 20.2.0.7" || reader.ReadUInt32() != 0x14020007 ||
            reader.ReadByte() != 1 || reader.ReadUInt32() != 12) return new(NifFormatFamily.Unknown, null);
        reader.ReadUInt32(); var version = reader.ReadUInt32();
        return new(version switch { 83 => NifFormatFamily.SkyrimLe, 100 => NifFormatFamily.SkyrimSe, _ => NifFormatFamily.Unknown }, version);
    }
}
