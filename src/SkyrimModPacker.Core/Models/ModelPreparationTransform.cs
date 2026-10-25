using System.Numerics;

namespace SkyrimModPacker.Core.Models;

/// <summary>Read-only geometry measurements; does not imply a NIF can be exported.</summary>
public readonly record struct ModelGeometryBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Dimensions => Maximum - Minimum;
    public float LongestExtent => MathF.Max(Dimensions.X, MathF.Max(Dimensions.Y, Dimensions.Z));

    public static ModelGeometryBounds Measure(IEnumerable<Vector3> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var count = 0;
        foreach (var point in positions)
        {
            if (!Finite(point)) throw new ArgumentOutOfRangeException(nameof(positions), "Mesh coordinates must be finite.");
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
            count++;
        }
        if (count == 0 || Vector3.DistanceSquared(minimum, maximum) <= 0)
            throw new ArgumentException("Geometry must contain a nonzero extent.", nameof(positions));
        return new(minimum, maximum);
    }

    internal static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>A suggested uniform transform using explicit hilt-to-tip directions and grip points.</summary>
public readonly record struct ModelPreparationTransform(float Scale, Quaternion Rotation, Vector3 Translation)
{
    public Vector3 Apply(Vector3 point) => Vector3.Transform(point * Scale, Rotation) + Translation;

    public static ModelPreparationTransform Suggest(
        ModelGeometryBounds source, ModelGeometryBounds template,
        Vector3 sourceGrip, Vector3 templateGrip,
        Vector3 sourceHiltToTip, Vector3 templateHiltToTip)
    {
        if (!ModelGeometryBounds.Finite(source.Minimum) || !ModelGeometryBounds.Finite(source.Maximum) ||
            !ModelGeometryBounds.Finite(template.Minimum) || !ModelGeometryBounds.Finite(template.Maximum) ||
            !ModelGeometryBounds.Finite(sourceGrip) || !ModelGeometryBounds.Finite(templateGrip) ||
            !ModelGeometryBounds.Finite(sourceHiltToTip) || !ModelGeometryBounds.Finite(templateHiltToTip))
            throw new ArgumentOutOfRangeException(nameof(source), "Geometry, grip and directions must be finite.");
        if (source.LongestExtent <= 0 || template.LongestExtent <= 0 ||
            sourceHiltToTip.LengthSquared() <= 1e-12f || templateHiltToTip.LengthSquared() <= 1e-12f)
            throw new ArgumentException("Geometry extents and hilt-to-tip directions must be nonzero.");

        var scale = template.LongestExtent / source.LongestExtent;
        if (!float.IsFinite(scale) || scale <= 0 || scale > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(source), "Suggested scale is outside the supported range.");
        var from = Vector3.Normalize(sourceHiltToTip);
        var to = Vector3.Normalize(templateHiltToTip);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        Quaternion rotation;
        if (dot > 1f - 1e-6f) rotation = Quaternion.Identity;
        else if (dot < -1f + 1e-6f)
        {
            var axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
            rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        else
        {
            var axis = Vector3.Normalize(Vector3.Cross(from, to));
            rotation = Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
        }
        var translation = templateGrip - Vector3.Transform(sourceGrip * scale, rotation);
        if (!ModelGeometryBounds.Finite(translation))
            throw new ArgumentOutOfRangeException(nameof(templateGrip), "Calculated translation is not finite.");
        return new(scale, Quaternion.Normalize(rotation), translation);
    }
}
