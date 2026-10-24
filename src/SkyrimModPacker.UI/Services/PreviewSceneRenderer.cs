using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.UI.Services;

public static class PreviewSceneRenderer
{
    public static Model3DGroup Create(ModelPreview scene, bool textured, out IReadOnlyList<string> warnings)
    {
        var messages = scene.Warnings.ToList(); var textures = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
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
            Brush brush = mesh.DiffuseTexture != null && textures.TryGetValue(mesh.DiffuseTexture, out var image) ? image : new SolidColorBrush(Color.FromRgb(165, 181, 198));
            var material = new DiffuseMaterial(brush); material.Freeze();
            var model = new GeometryModel3D(geometry, material) { BackMaterial = material }; model.Freeze(); group.Children.Add(model);
        }
        var bounds = group.Bounds; var extent = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        if (bounds.IsEmpty || !double.IsFinite(extent) || extent < 1e-8) throw new InvalidDataException("The model has no measurable geometry.");
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-bounds.X - bounds.SizeX / 2, -bounds.Y - bounds.SizeY / 2, -bounds.Z - bounds.SizeZ / 2));
        transform.Children.Add(new ScaleTransform3D(2 / extent, 2 / extent, 2 / extent)); transform.Freeze(); group.Transform = transform;
        group.Freeze(); warnings = messages; return group;
    }
}
