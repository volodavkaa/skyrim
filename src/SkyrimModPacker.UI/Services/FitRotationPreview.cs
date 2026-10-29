using System.Windows.Media.Media3D;

namespace SkyrimModPacker.UI.Services;

public readonly record struct FitRotation(double X, double Y, double Z);

public static class FitRotationPreview
{
    public static Matrix3D Difference(FitRotation prepared, FitRotation requested)
    {
        static Matrix3D Rotation(FitRotation angles)
        {
            var matrix = Matrix3D.Identity;
            // NIF (x,y,z) is displayed as (x,z,-y).
            matrix.Rotate(new Quaternion(new Vector3D(1, 0, 0), angles.X));
            matrix.Rotate(new Quaternion(new Vector3D(0, 0, -1), angles.Y));
            matrix.Rotate(new Quaternion(new Vector3D(0, 1, 0), angles.Z));
            return matrix;
        }
        var result = Rotation(prepared);
        result.Invert();
        result.Append(Rotation(requested));
        return result;
    }
}
