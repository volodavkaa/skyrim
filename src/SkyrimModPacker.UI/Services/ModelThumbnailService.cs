using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.UI.Services;

public sealed record ModelThumbnail(ImageSource? Image, string? Error);
public interface IModelThumbnailService
{
    Task<ModelThumbnail> CreateAsync(string project, string model, string gameData, CancellationToken token = default);
}

/// <summary>Bounded static image cache keyed by the actual geometry and decoded diffuse pixels.</summary>
public sealed class ModelThumbnailService(IModelPreviewService preview) : IModelThumbnailService
{
    private readonly Dictionary<string, ImageSource> _cache = [];
    private readonly Queue<string> _order = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<ModelThumbnail> CreateAsync(string project, string model, string gameData, CancellationToken token = default)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        await _gate.WaitAsync(token);
        try
        {
            var scene = await preview.LoadAsync(project, model, gameData, token);
            var key = Fingerprint(scene);
            if (_cache.TryGetValue(key, out var image)) return new(image, null);
            image = await dispatcher.InvokeAsync(() => Render(scene), DispatcherPriority.Background, token);
            _cache[key] = image; _order.Enqueue(key);
            while (_order.Count > 32) _cache.Remove(_order.Dequeue());
            return new(image, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, ex.Message); }
        finally { _gate.Release(); }
    }
    private static string Fingerprint(ModelPreview scene)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var mesh in scene.Meshes)
        {
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Positions.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.TextureCoordinates.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(mesh.DiffuseTexture ?? ""));
        }
        foreach (var texture in scene.Textures)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(texture.Path));
            hash.AppendData(BitConverter.GetBytes(texture.Width)); hash.AppendData(BitConverter.GetBytes(texture.Height)); hash.AppendData(texture.BgraPixels);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static BitmapSource Render(ModelPreview scene)
    {
        var viewport = new Viewport3D
        {
            Width = 128, Height = 128,
            Camera = new PerspectiveCamera(new Point3D(0, 1.4, 3.5), new Vector3D(0, -1.4, -3.5), new Vector3D(0, 1, 0), 45) { NearPlaneDistance = 0.01, FarPlaneDistance = 100 }
        };
        var lights = new Model3DGroup(); lights.Children.Add(new AmbientLight(Color.FromRgb(70, 70, 70)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)));
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        viewport.Children.Add(new ModelVisual3D
        {
            Content = PreviewSceneRenderer.Create(scene, true, out _),
            Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 25))
        });
        var surface = new Border { Width = 128, Height = 128, Background = new SolidColorBrush(Color.FromRgb(32, 35, 42)), Child = viewport };
        surface.Measure(new Size(128, 128)); surface.Arrange(new Rect(0, 0, 128, 128)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface); bitmap.Freeze(); return bitmap;
    }
}
