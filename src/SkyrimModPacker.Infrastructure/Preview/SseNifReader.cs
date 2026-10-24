using System.Numerics;
using System.Text;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Preview;

/// <summary>Read-only, bounded static Skyrim SE/LE geometry subsets; never converts or writes NIF files.</summary>
public sealed partial class SseNifReader
{
    public const int MaxFileBytes = 64 * 1024 * 1024;
    private sealed record Block(string Type, byte[] Bytes);
    private Block[] _blocks = [];
    private string[] _strings = [];
    private readonly List<PreviewMesh> _meshes = [];
    private readonly HashSet<int> _active = [];
    private CancellationToken _token;
    private int _vertices;
    private int _triangles;
    private int _visits;
    private uint _stream;
    private readonly HashSet<string> _texturePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _textureDependenciesComplete = true;
    private readonly HashSet<string> _dependencyWarnings = [];

    public static ModelPreview Read(byte[] data, CancellationToken token = default) => new SseNifReader().Parse(data, token);
    private ModelPreview Parse(byte[] data, CancellationToken token)
    {
        _token = token; token.ThrowIfCancellationRequested();
        if (data.Length > MaxFileBytes) throw new InvalidDataException("NIF exceeds the 64 MiB preview limit.");
        using var r = Reader(data);
        var line = new StringBuilder();
        while (line.Length < 128) { var c = r.ReadByte(); if (c == 10) break; line.Append((char)c); }
        if (line.ToString().TrimEnd('\r') != "Gamebryo File Format, Version 20.2.0.7" || r.ReadUInt32() != 0x14020007)
            throw new NotSupportedException("Preview supports Skyrim LE/SE NIF 20.2.0.7 only.");
        if (r.ReadByte() != 1 || r.ReadUInt32() != 12) throw new NotSupportedException("Unsupported NIF endianness or user version.");
        var count = Count(r, 4096);
        _stream = r.ReadUInt32();
        if (_stream is not (83 or 100)) throw new NotSupportedException("Preview supports Bethesda streams 83 (LE) and 100 (SE) only.");
        for (var i = 0; i < 3; i++) Bytes(r, r.ReadByte());
        var typeCount = r.ReadUInt16(); if (typeCount > 4096) throw new InvalidDataException("Too many block types.");
        var types = Enumerable.Range(0, typeCount).Select(_ => Text(r)).ToArray();
        var ids = Enumerable.Range(0, count).Select(_ => r.ReadUInt16()).ToArray();
        var sizes = Enumerable.Range(0, count).Select(_ => Count(r, MaxFileBytes)).ToArray();
        var strings = Count(r, 65536); var maxString = Count(r, 4096);
        _strings = Enumerable.Range(0, strings).Select(_ => Text(r, maxString)).ToArray();
        var groups = Count(r, 4096); Bytes(r, checked(groups * 4));
        _blocks = new Block[count];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (ids[i] >= types.Length) throw new InvalidDataException("Invalid NIF block type index.");
            _blocks[i] = new(types[ids[i]], Bytes(r, sizes[i]));
        }
        var roots = Count(r, 128); if (roots == 0) throw new InvalidDataException("The NIF has no scene roots.");
        var rootIds = Enumerable.Range(0, roots).Select(_ => Link(r, false)).ToArray();
        if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected data after the NIF footer.");
        // Texture dependencies include hidden/scabbard geometry, not only rendered meshes.
        InspectTextureBlocks();
        foreach (var id in rootIds) Visit(id, Matrix4x4.Identity, 0);
        if (_meshes.Count == 0) throw new NotSupportedException("No visible supported static geometry was found.");
        return new(_meshes, [], new[] { "Static geometry and diffuse texture only. Skyrim shaders, collision, animation and plugin texture overrides are not reproduced." }.Concat(_dependencyWarnings).ToArray())
        { TexturePaths = _texturePaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), TextureDependenciesComplete = _textureDependenciesComplete };
    }

    private void Visit(int id, Matrix4x4 parent, int depth)
    {
        _token.ThrowIfCancellationRequested();
        if (++_visits > 8192) throw new InvalidDataException("NIF scene traversal budget exceeded.");
        if (depth > 64 || !_active.Add(id)) throw new InvalidDataException("Cyclic or excessively deep NIF scene graph.");
        try
        {
            var block = _blocks[id];
            var geometry = _stream == 83 ? block.Type is "NiTriShape" or "NiTriStrips" : block.Type == "BSTriShape";
            if (!geometry && block.Type is not ("NiNode" or "BSFadeNode")) throw new NotSupportedException("Unsupported scene block: " + block.Type);
            using var r = Reader(block.Bytes);
            var name = ObjectNet(r);
            var flags = r.ReadUInt32(); var translation = Vector(r);
            var a = Enumerable.Range(0, 9).Select(_ => Number(r)).ToArray(); var scale = Number(r);
            if (scale <= 0 || scale > 100000) throw new NotSupportedException("Unsupported NIF scale.");
            var rotation = new Matrix4x4(a[0], a[3], a[6], 0, a[1], a[4], a[7], 0, a[2], a[5], a[8], 0, 0, 0, 0, 1);
            var transform = Matrix4x4.CreateScale(scale) * rotation * Matrix4x4.CreateTranslation(translation) * parent;
            Link(r); // Collision is intentionally not displayed.
            if ((flags & 1) != 0) return; // Dependencies were inspected independently above.
            if (!geometry)
            {
                var children = Count(r, _blocks.Length); var links = Enumerable.Range(0, children).Select(_ => Link(r)).ToArray();
                var effects = Count(r, _blocks.Length); for (var i = 0; i < effects; i++) Link(r);
                End(r);
                foreach (var child in links.Where(i => i >= 0)) Visit(child, transform, depth + 1);
                return;
            }
            if (_stream == 83) { ReadLeGeometry(r, block.Type, name, transform); return; }
            for (var i = 0; i < 4; i++) Number(r); // Bounds are not trusted for framing.
            if (Link(r) != -1) throw new NotSupportedException("Skinned NIF geometry is not supported by this static preview.");
            var shader = Link(r); Link(r); // Alpha property: approximate diffuse preview only.
            var desc = r.ReadUInt64(); var attributes = (uint)((desc >> 44) & 0xFFF); var stride = (int)(desc & 15) * 4;
            if ((attributes & 1) == 0 || (attributes & ~0x43Bu) != 0 || (desc & 0xF0) != 0)
                throw new NotSupportedException("Unsupported NIF vertex attributes.");
            var triangles = r.ReadUInt16(); var vertices = r.ReadUInt16(); var size = r.ReadUInt32();
            _vertices += vertices;
            _triangles += triangles;
            if (_vertices > 60000 || _triangles > 120000 || _meshes.Count >= 128 || vertices == 0 || triangles == 0 || stride < 16 || size != (long)stride * vertices + (long)triangles * 6)
                throw new InvalidDataException("Invalid geometry size or preview budget exceeded.");
            var uvOffset = (int)((desc >> 8) & 15) * 4;
            var expectedStride = 16 + ((attributes & 2) != 0 ? 4 : 0) + ((attributes & 8) != 0 ? 4 : 0) + ((attributes & 24) == 24 ? 4 : 0) + ((attributes & 32) != 0 ? 4 : 0);
            if (stride != expectedStride || ((attributes & 16) != 0 && (attributes & 8) == 0)) throw new NotSupportedException("Unsupported vertex stride.");
            if ((attributes & 2) != 0 && (uvOffset < 16 || uvOffset + 4 > stride)) throw new InvalidDataException("Invalid UV offset.");
            var points = new Vector3[vertices]; var uv = new Vector2[vertices];
            for (var i = 0; i < vertices; i++)
            {
                _token.ThrowIfCancellationRequested();
                var start = r.BaseStream.Position;
                points[i] = Vector3.Transform(Vector(r), transform);
                if (!Finite(points[i]) || points[i].LengthSquared() > 1e14f) throw new InvalidDataException("Invalid transformed vertex.");
                if ((attributes & 2) != 0)
                {
                    r.BaseStream.Position = start + uvOffset;
                    uv[i] = new((float)BitConverter.UInt16BitsToHalf(r.ReadUInt16()), (float)BitConverter.UInt16BitsToHalf(r.ReadUInt16()));
                    if (!float.IsFinite(uv[i].X) || !float.IsFinite(uv[i].Y)) throw new InvalidDataException("Invalid texture coordinates.");
                }
                if (start + stride > r.BaseStream.Length) throw new EndOfStreamException();
                r.BaseStream.Position = start + stride;
            }
            var indices = new int[triangles * 3];
            for (var i = 0; i < indices.Length; i++) { indices[i] = r.ReadUInt16(); if (indices[i] >= vertices) throw new InvalidDataException("Triangle index outside the vertex array."); }
            if (r.ReadUInt32() != 0) throw new NotSupportedException("Particle geometry is not supported.");
            End(r);
            _meshes.Add(new(name, points, uv, indices, Texture(shader)));
        }
        finally { _active.Remove(id); }
    }

    private string? Texture(int shader)
    {
        if (shader < 0) { _textureDependenciesComplete = false; return null; }
        if (_blocks[shader].Type == "BSEffectShaderProperty")
        {
            using var effect = Reader(_blocks[shader].Bytes);
            ObjectNet(effect); effect.ReadUInt32(); effect.ReadUInt32();
            for (var i = 0; i < 4; i++) Number(effect);
            var source = Text(effect); effect.ReadUInt32();
            for (var i = 0; i < 10; i++) Number(effect);
            var greyscale = Text(effect); End(effect);
            foreach (var path in new[] { source, greyscale }.Where(p => !string.IsNullOrWhiteSpace(p))) _texturePaths.Add(path);
            return string.IsNullOrWhiteSpace(source) ? null : source;
        }
        if (_blocks[shader].Type != "BSLightingShaderProperty") { _textureDependenciesComplete = false; return null; }
        using var r = Reader(_blocks[shader].Bytes);
        r.ReadUInt32(); ObjectNet(r); r.ReadUInt32(); r.ReadUInt32();
        for (var i = 0; i < 4; i++) Number(r);
        var set = Link(r); if (set < 0) { _textureDependenciesComplete = false; return null; }
        if (_blocks[set].Type != "BSShaderTextureSet") throw new InvalidDataException("Invalid shader texture-set link.");
        using var t = Reader(_blocks[set].Bytes); var count = Count(t, 16);
        var values = Enumerable.Range(0, count).Select(_ => Text(t)).ToArray(); End(t);
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v))) _texturePaths.Add(value);
        return values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;
    }
    private void InspectTextureBlocks()
    {
        for (var index = 0; index < _blocks.Length; index++)
        {
            _token.ThrowIfCancellationRequested(); var block = _blocks[index];
            try
            {
                if (block.Type == "BSShaderTextureSet")
                {
                    using var reader = Reader(block.Bytes); var count = Count(reader, 16);
                    for (var slot = 0; slot < count; slot++) { var path = Text(reader); if (!string.IsNullOrWhiteSpace(path)) _texturePaths.Add(path); }
                    End(reader);
                }
                else if (block.Type is "BSLightingShaderProperty" or "BSEffectShaderProperty") _ = Texture(index);
                else if (block.Type.Contains("Textur", StringComparison.Ordinal) || block.Type.Contains("Shader", StringComparison.Ordinal))
                    throw new NotSupportedException("Unknown texture/shader block.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _textureDependenciesComplete = false; _dependencyWarnings.Add(block.Type + ": " + ex.Message); }
        }
    }

    private string ObjectNet(BinaryReader r)
    {
        var name = r.ReadInt32(); if (name < -1 || name >= _strings.Length) throw new InvalidDataException("Invalid string index.");
        var extras = Count(r, _blocks.Length); for (var i = 0; i < extras; i++) Link(r);
        if (Link(r) != -1) throw new NotSupportedException("Animated NIF objects are not supported.");
        return name == -1 ? "" : _strings[name];
    }
    private int Link(BinaryReader r, bool nullable = true) { var id = r.ReadInt32(); if (id < (nullable ? -1 : 0) || id >= _blocks.Length) throw new InvalidDataException("Invalid block link."); return id; }
    private static BinaryReader Reader(byte[] b) => new(new MemoryStream(b, false), Encoding.UTF8);
    private static int Count(BinaryReader r, int max) { var n = r.ReadUInt32(); if (n > max) throw new InvalidDataException("NIF count exceeds preview limits."); return (int)n; }
    private static byte[] Bytes(BinaryReader r, int count) { if (count > r.BaseStream.Length - r.BaseStream.Position) throw new EndOfStreamException(); return r.ReadBytes(count); }
    private static string Text(BinaryReader r, int max = 4096) => Encoding.UTF8.GetString(Bytes(r, Count(r, max))).TrimEnd('\0');
    private static float Number(BinaryReader r) { var n = r.ReadSingle(); if (!float.IsFinite(n) || Math.Abs(n) > 1e7) throw new InvalidDataException("Invalid NIF floating-point value."); return n; }
    private static Vector3 Vector(BinaryReader r) => new(Number(r), Number(r), Number(r));
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static void End(BinaryReader r) { if (r.BaseStream.Position != r.BaseStream.Length) throw new NotSupportedException("Unsupported additional block data."); }
}
