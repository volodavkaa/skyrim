using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.UI.Services;

public static class PreviewSceneRenderer
{
    public sealed record ReflectionPreview(PreviewTexture Mask, IReadOnlyList<PreviewTexture> Faces);

    public static Model3DGroup Create(ModelPreview scene, bool textured, out IReadOnlyList<string> warnings)
        => Create(scene, textured, out warnings, out _);
    public static Model3DGroup Create(ModelPreview scene, bool textured, out IReadOnlyList<string> warnings, out double originalExtent)
        => Create(scene, textured, out warnings, out originalExtent, null);

    public static Model3DGroup Create(ModelPreview scene, bool textured, out IReadOnlyList<string> warnings,
        out double originalExtent, ReflectionPreview? reflection, double vertexColor = 1,
        double environmentStrength = 1, Color? specularColor = null, double specularStrength = 0,
        Rect3D? framingBounds = null)
    {
        var messages = scene.Warnings.ToList(); var textures = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        var tintedTextures = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        vertexColor = Math.Clamp(vertexColor, 0, 1);
        long pixels = 0;
        if (textured) foreach (var texture in scene.Textures)
        {
            try
            {
                pixels += (long)texture.Width * texture.Height;
                if (pixels > 16 * 1024 * 1024 || texture.Width <= 0 || texture.Height <= 0 || texture.Width > 4096 || texture.Height > 4096 || texture.BgraPixels.Length != (long)texture.Width * texture.Height * 4)
                    throw new InvalidDataException("Invalid decoded texture dimensions or preview budget exceeded.");
                var image = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null, texture.BgraPixels, texture.Width * 4);
                image.Freeze();
                var brush = new ImageBrush(image) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.Absolute };
                brush.Freeze(); textures[texture.Path] = brush;
                if (vertexColor < 1)
                {
                    var tinted = (byte[])texture.BgraPixels.Clone();
                    for (var i = 0; i < tinted.Length; i += 4)
                        for (var channel = 0; channel < 3; channel++)
                            tinted[i + channel] = (byte)Math.Round(tinted[i + channel] * vertexColor);
                    var tintedImage = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null, tinted, texture.Width * 4);
                    tintedImage.Freeze();
                    var tintedBrush = new ImageBrush(tintedImage) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.Absolute };
                    tintedBrush.Freeze(); tintedTextures[texture.Path] = tintedBrush;
                }
            }
            catch (Exception ex) { messages.Add($"Texture '{texture.Path}': {ex.Message} Showing neutral material."); }
        }
        var group = new Model3DGroup();
        if (scene.Meshes.Count > 128 || scene.Meshes.Sum(m => (long)m.Indices.Length) > 360000 || scene.Meshes.Sum(m => (long)m.Positions.Length) > 60000)
            throw new InvalidDataException("Preview geometry budget exceeded.");
        foreach (var mesh in scene.Meshes)
        {
            if (mesh.Positions.Length < 3 || mesh.Positions.Length > 60000 || mesh.TextureCoordinates.Length != mesh.Positions.Length || mesh.Indices.Length == 0 || mesh.Indices.Length % 3 != 0 ||
                mesh.Indices.Any(i => i < 0 || i >= mesh.Positions.Length) || mesh.Positions.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)))
                throw new InvalidDataException("Invalid preview geometry.");
            var geometry = new MeshGeometry3D
            {
                Positions = new Point3DCollection(mesh.Positions.Select(v => new Point3D(v.X, v.Z, -v.Y))),
                TextureCoordinates = new PointCollection(mesh.TextureCoordinates.Select(v => new Point(v.X, v.Y))),
                TriangleIndices = new Int32Collection(mesh.Indices)
            };
            geometry.Freeze();
            var character = mesh.Name.StartsWith("Character: ", StringComparison.Ordinal);
            var tint = !character && mesh.Name is not ("BloodEffects" or "BloodLighting");
            var palette = tint && vertexColor < 1 ? tintedTextures : textures;
            var shade = tint ? vertexColor : 1;
            Brush brush = mesh.DiffuseTexture != null && palette.TryGetValue(mesh.DiffuseTexture, out var image) ? image : new SolidColorBrush(Color.FromRgb((byte)(165 * shade), (byte)(181 * shade), (byte)(198 * shade)));
            Material material = new DiffuseMaterial(brush);
            if (tint && specularColor is Color spec && specularStrength > 0)
            {
                var specBrush = new SolidColorBrush(spec) { Opacity = Math.Clamp(specularStrength / 4, 0, 1) };
                var groupMaterial = new MaterialGroup();
                groupMaterial.Children.Add(material);
                groupMaterial.Children.Add(new SpecularMaterial(specBrush, 40));
                material = groupMaterial;
            }
            material.Freeze();
            var model = new GeometryModel3D(geometry, material) { BackMaterial = material }; model.Freeze(); group.Children.Add(model);
            if (!character && reflection != null && mesh.Name is not ("BloodEffects" or "BloodLighting" or "Scb"))
                AddReflection(group, mesh, reflection, environmentStrength);
        }
        var bounds = framingBounds ?? group.Bounds; var extent = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        originalExtent = extent;
        if (bounds.IsEmpty || !double.IsFinite(extent) || extent < 1e-8) throw new InvalidDataException("The model has no measurable geometry.");
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-bounds.X - bounds.SizeX / 2, -bounds.Y - bounds.SizeY / 2, -bounds.Z - bounds.SizeZ / 2));
        transform.Children.Add(new ScaleTransform3D(2 / extent, 2 / extent, 2 / extent)); transform.Freeze(); group.Transform = transform;
        group.Freeze(); warnings = messages; return group;
    }

    private static void AddReflection(Model3DGroup group, PreviewMesh mesh, ReflectionPreview reflection, double environmentStrength)
    {
        if (reflection.Faces.Count != 6 || mesh.Indices.Length > 30000) return;
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Indices[i]; var b = mesh.Indices[i + 1]; var c = mesh.Indices[i + 2];
            var p0 = Display(mesh.Positions[a]); var p1 = Display(mesh.Positions[b]); var p2 = Display(mesh.Positions[c]);
            var normal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
            if (normal.LengthSquared < 1e-12) continue;
            normal.Normalize();
            var uv = (mesh.TextureCoordinates[a] + mesh.TextureCoordinates[b] + mesh.TextureCoordinates[c]) / 3;
            var mask = Sample(reflection.Mask, uv.X, uv.Y);
            var strength = (mask.R + mask.G + mask.B) / (3.0 * 255.0) * Math.Clamp(environmentStrength, 0, 4);
            if (strength < 0.025) continue;
            var incident = new Vector3D(0, 0, -1);
            var direction = incident - 2 * Vector3D.DotProduct(incident, normal) * normal;
            var color = SampleCube(reflection.Faces, direction);
            var alpha = (byte)Math.Clamp(strength * 180, 0, 220);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)); brush.Freeze();
            var material = new EmissiveMaterial(brush); material.Freeze();
            var offset = normal * 0.002;
            var geometry = new MeshGeometry3D
            {
                Positions = new Point3DCollection([p0 + offset, p1 + offset, p2 + offset]),
                TriangleIndices = new Int32Collection([0, 1, 2])
            };
            geometry.Freeze();
            var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
            model.Freeze(); group.Children.Add(model);
        }
    }

    private static Point3D Display(System.Numerics.Vector3 value) => new(value.X, value.Z, -value.Y);

    private static Color SampleCube(IReadOnlyList<PreviewTexture> faces, Vector3D vector)
    {
        var x = vector.X; var y = vector.Y; var z = vector.Z;
        var ax = Math.Abs(x); var ay = Math.Abs(y); var az = Math.Abs(z);
        int face; double u, v, major;
        if (ax >= ay && ax >= az)
        { major = ax; face = x >= 0 ? 0 : 1; u = x >= 0 ? -z : z; v = -y; }
        else if (ay >= az)
        { major = ay; face = y >= 0 ? 2 : 3; u = x; v = y >= 0 ? z : -z; }
        else
        { major = az; face = z >= 0 ? 4 : 5; u = z >= 0 ? x : -x; v = -y; }
        return Sample(faces[face], 0.5 + u / (2 * major), 0.5 + v / (2 * major));
    }

    private static Color Sample(PreviewTexture texture, double u, double v)
    {
        var x = Math.Clamp((int)(Math.Abs(u % 1) * texture.Width), 0, texture.Width - 1);
        var y = Math.Clamp((int)(Math.Abs(v % 1) * texture.Height), 0, texture.Height - 1);
        var offset = (y * texture.Width + x) * 4;
        return Color.FromRgb(texture.BgraPixels[offset + 2], texture.BgraPixels[offset + 1], texture.BgraPixels[offset]);
    }
}
