using System.Numerics;
using System.Text;

namespace SkyrimModPacker.Tests;

internal static class PreviewFixtures
{
    public static byte[] ResourceDds(string marker = "fixture resource") => Dds().Concat(Encoding.UTF8.GetBytes(marker)).ToArray();
    public static byte[] Bc7(uint dxgi = 98)
    {
        var bytes = new byte[164]; Array.Copy(Dds(), bytes, 128); Encoding.ASCII.GetBytes("DX10").CopyTo(bytes, 84);
        BitConverter.GetBytes(dxgi).CopyTo(bytes, 128); BitConverter.GetBytes(3u).CopyTo(bytes, 132); BitConverter.GetBytes(1u).CopyTo(bytes, 140);
        bytes[148] = 64; return bytes; // Valid mode 6, constant black endpoints and zero indices.
    }
    public static byte[] Nif(string scenario = "valid", string diffuse = "textures/test.dds")
    {
        byte[] Block(Action<BinaryWriter> write) { using var memory = new MemoryStream(); using var w = new BinaryWriter(memory); write(w); return memory.ToArray(); }
        void Net(BinaryWriter w, int name) { w.Write(name); w.Write(0u); w.Write(scenario == "animated" ? 0 : -1); }
        void Av(BinaryWriter w, int name, bool root)
        {
            Net(w, name); w.Write(14u);
            foreach (var f in root ? new[] { 10f, 0, 0 } : new[] { 2f, 3, 4 }) w.Write(f);
            foreach (var f in root ? new[] { 0f, -1, 0, 1, 0, 0, 0, 0, 1 } : new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1 }) w.Write(f);
            w.Write(root ? 1f : 2f); w.Write(-1);
        }
        var blocks = new[] {
            Block(w => { Av(w, 0, true); w.Write(1u); w.Write(scenario == "cycle" ? 0 : 1); w.Write(0u); }),
            Block(w => {
                Av(w, 1, false); foreach(var f in new[] {0f,0,0,1}) w.Write(f);
                w.Write(scenario == "skin" ? 0 : -1); w.Write(2); w.Write(-1);
                w.Write(5UL | (4UL << 8) | (3UL << 44)); w.Write((ushort)1); w.Write((ushort)3); w.Write(66u);
                foreach (var v in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }) {
                    w.Write(scenario == "nan" ? float.NaN : v.X); w.Write(v.Y); w.Write(v.Z); w.Write(0u);
                    w.Write(BitConverter.HalfToUInt16Bits((Half)v.X)); w.Write(BitConverter.HalfToUInt16Bits((Half)v.Y));
                }
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)(scenario == "index" ? 3 : 2)); w.Write(0u);
            }),
            Block(w => { w.Write(0u); Net(w, -1); w.Write(0u); w.Write(0u); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(1f); w.Write(3); }),
            Block(w => { w.Write(1u); Text(w, diffuse); })
        };
        var types = new[] { "BSFadeNode", scenario == "unsupported" ? "BSDynamicTriShape" : "BSTriShape", "BSLightingShaderProperty", "BSShaderTextureSet" };
        return Block(w => {
            w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n")); w.Write(0x14020007u); w.Write((byte)1); w.Write(12u); w.Write(4u);
            w.Write(scenario == "LE" ? 83u : 100u); w.Write(new byte[3]); w.Write((ushort)4);
            foreach (var type in types) Text(w, type);
            for (ushort i = 0; i < 4; i++) w.Write(i);
            foreach (var block in blocks) w.Write((uint)block.Length);
            w.Write(2u); w.Write(5u); Text(w, "Scene"); Text(w, "Blade"); w.Write(0u);
            foreach (var block in blocks) w.Write(block);
            w.Write(1u); w.Write(scenario == "root" ? 9 : 0);
        });
    }
    private static void Text(BinaryWriter w, string text) { var bytes = Encoding.UTF8.GetBytes(text); w.Write((uint)bytes.Length); w.Write(bytes); }

    public static byte[] Dds(string format = "DXT1", ushort first = 0xF800, ushort second = 0x001F, uint codes = 0xE4E4E4E4)
    {
        var bytes = new byte[format == "DXT1" ? 136 : 144];
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
        "DDS "u8.CopyTo(bytes); U32(4, 124); U32(12, 4); U32(16, 4); U32(76, 32); U32(80, 4); Encoding.ASCII.GetBytes(format).CopyTo(bytes, 84);
        var offset = format == "DXT1" ? 128 : 136;
        BitConverter.GetBytes(first).CopyTo(bytes, offset); BitConverter.GetBytes(second).CopyTo(bytes, offset + 2); U32(offset + 4, codes);
        if (format == "DXT3") Array.Fill(bytes, (byte)0xF0, 128, 8);
        if (format == "DXT5") { bytes[128] = 255; bytes[129] = 0; bytes[130] = 2; }
        return bytes;
    }
}
