using System.Numerics;

namespace SkyrimModPacker.Infrastructure.Preview;

public sealed partial class SseNifReader
{
    // Skyrim LE stream 83 stores separate NiGeometryData, unlike SSE's interleaved BSTriShape.
    private void ReadLeGeometry(BinaryReader shape, string type, string name, Matrix4x4 transform)
    {
        var data = Link(shape, false);
        if (Link(shape) != -1) throw new NotSupportedException("Skinned LE geometry is not supported by this static preview.");
        var materials = Count(shape, 128);
        for (var i = 0; i < materials; i++) { var s = shape.ReadInt32(); if (s < -1 || s >= _strings.Length) throw new InvalidDataException("Invalid material name."); }
        for (var i = 0; i < materials; i++) shape.ReadInt32();
        shape.ReadInt32(); Boolean(shape);
        var shader = Link(shape); Link(shape); End(shape);
        var expected = type == "NiTriShape" ? "NiTriShapeData" : "NiTriStripsData";
        if (_blocks[data].Type != expected) throw new InvalidDataException("LE geometry data type mismatch.");
        using var r = Reader(_blocks[data].Bytes);
        r.ReadInt32(); var vertices = r.ReadUInt16(); r.ReadByte(); r.ReadByte();
        _vertices += vertices;
        if (vertices == 0 || _vertices > 60000 || _meshes.Count >= 128 || !Boolean(r)) throw new InvalidDataException("Invalid LE vertex array or preview budget exceeded.");
        var points = new Vector3[vertices];
        for (var i = 0; i < vertices; i++)
        {
            _token.ThrowIfCancellationRequested(); points[i] = Vector3.Transform(Vector(r), transform);
            if (!Finite(points[i]) || points[i].LengthSquared() > 1e14f) throw new InvalidDataException("Invalid LE transformed vertex.");
        }
        var flags = r.ReadUInt16(); r.ReadUInt32();
        if ((flags & ~0x1001) != 0) throw new NotSupportedException("Unsupported LE geometry flags.");
        if (Boolean(r))
        {
            for (var i = 0; i < vertices; i++) Vector(r);
            if ((flags & 4096) != 0) for (var i = 0; i < vertices * 2; i++) Vector(r);
        }
        for (var i = 0; i < 4; i++) Number(r);
        if (Boolean(r)) for (var i = 0; i < vertices * 4; i++) Number(r);
        var uv = new Vector2[vertices];
        if ((flags & 1) != 0) for (var i = 0; i < vertices; i++) uv[i] = new(Number(r), Number(r));
        r.ReadUInt16();
        if (Link(r) != -1) throw new NotSupportedException("Additional LE vertex data is unsupported.");
        var triangles = r.ReadUInt16(); var indices = new List<int>();
        if (triangles == 0 || _triangles + triangles > 120000) throw new InvalidDataException("Invalid LE triangle count.");
        int Index() { var n = r.ReadUInt16(); if (n >= vertices) throw new InvalidDataException("LE triangle index outside vertex array."); return n; }
        if (type == "NiTriShape")
        {
            if (r.ReadUInt32() != triangles * 3u || !Boolean(r)) throw new InvalidDataException("Invalid LE triangle array.");
            for (var i = 0; i < triangles * 3; i++) indices.Add(Index());
            var groups = r.ReadUInt16();
            for (var i = 0; i < groups; i++) { var n = r.ReadUInt16(); for (var j = 0; j < n; j++) Index(); }
        }
        else
        {
            var strips = r.ReadUInt16(); var lengths = new ushort[strips];
            for (var i = 0; i < strips; i++) lengths[i] = r.ReadUInt16();
            if (!Boolean(r)) throw new InvalidDataException("Missing LE strip indices.");
            foreach (var length in lengths)
            {
                var strip = new int[length]; for (var j = 0; j < length; j++) strip[j] = Index();
                for (var j = 2; j < length; j++)
                {
                    var a = strip[j - 2]; var b = strip[j - 1]; var c = strip[j];
                    if (a == b || b == c || a == c) continue;
                    if (indices.Count >= 360000) throw new InvalidDataException("LE strip triangle budget exceeded.");
                    indices.Add((j & 1) == 0 ? a : b); indices.Add((j & 1) == 0 ? b : a); indices.Add(c);
                }
            }
            if (indices.Count != triangles * 3) throw new InvalidDataException("LE strip triangle count mismatch.");
        }
        End(r); _triangles += indices.Count / 3;
        _meshes.Add(new(name, points, uv, indices.ToArray(), Texture(shader)));
    }
    private static bool Boolean(BinaryReader r) => r.ReadByte() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid NIF boolean.") };
}
