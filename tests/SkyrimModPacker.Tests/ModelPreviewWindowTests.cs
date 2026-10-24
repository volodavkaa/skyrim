using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NSubstitute;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.Views;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public sealed class ModelPreviewWindowTests
{
    private static ModelPreview Scene() => SseNifReader.Read(PreviewFixtures.Nif()) with { Textures = [DdsPreviewDecoder.Decode("textures/test.dds", PreviewFixtures.Dds())] };
    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } finally { dispatcher.InvokeShutdown(); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RendererHasRealGeometryAndMaterial_AndControlsChangeCameraAndLights() => await Sta(async () =>
    {
        var service = Substitute.For<IModelPreviewService>(); service.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Scene());
        var window = new ModelPreviewWindow(service, new("", "third.nif", "first.nif", ""));
        await window.LoadViewAsync(false); Assert.True(window.HasScene);
        var camera = (System.Windows.Media.Media3D.PerspectiveCamera)window.Viewport.Camera; Assert.Equal(4, camera.Position.Z);
        var controls = ((DockPanel)window.Content).Children.OfType<WrapPanel>().Single();
        var sliders = controls.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Slider>()).ToArray(); sliders[1].Value = 6;
        Assert.Equal(6, ((System.Windows.Media.Media3D.PerspectiveCamera)window.Viewport.Camera).Position.Z);
        sliders[0].Value = 90; sliders[2].Value = 0.2;
        await window.LoadViewAsync(true); await service.Received(1).LoadAsync("", "first.nif", "", Arg.Any<CancellationToken>());
        Assert.Contains(((DockPanel)window.Content).Children.OfType<TextBlock>(), t => t.Text.Contains("first.nif"));
        var source = PreviewSceneRenderer.Create(Scene(), true, out var warnings); Assert.True(source.IsFrozen); Assert.NotEmpty(source.Children);
        Assert.DoesNotContain(warnings, w => w.Contains("neutral material"));
        window.Close();
    });

    [Fact]
    public async Task FailedOrMissingFirstViewClearsThirdView_AndDoesNotUseThirdAsFallback() => await Sta(async () =>
    {
        var service = Substitute.For<IModelPreviewService>();
        service.LoadAsync("", "third.nif", "", Arg.Any<CancellationToken>()).Returns(Scene());
        service.LoadAsync("", "broken.nif", "", Arg.Any<CancellationToken>()).Returns(Task.FromException<ModelPreview>(new NotSupportedException("Unsupported geometry")));
        var window = new ModelPreviewWindow(service, new("", "third.nif", "broken.nif", ""));
        await window.LoadViewAsync(false); Assert.True(window.HasScene);
        await window.LoadViewAsync(true); Assert.False(window.HasScene); Assert.Contains("Unsupported geometry", window.Status); window.Close();
        var missing = new ModelPreviewWindow(service, new("", "third.nif", "", ""));
        await missing.LoadViewAsync(false); Assert.True(missing.HasScene); await missing.LoadViewAsync(true); Assert.False(missing.HasScene); missing.Close();
    });

    [Fact]
    public async Task OldCompletionAndClosedWindowCannotRestoreStaleScene() => await Sta(async () =>
    {
        var service = Substitute.For<IModelPreviewService>(); var first = new TaskCompletionSource<ModelPreview>(); var third = new TaskCompletionSource<ModelPreview>();
        service.LoadAsync("", "third.nif", "", Arg.Any<CancellationToken>()).Returns(third.Task);
        service.LoadAsync("", "first.nif", "", Arg.Any<CancellationToken>()).Returns(first.Task);
        var window = new ModelPreviewWindow(service, new("", "third.nif", "first.nif", ""));
        var oldLoad = window.LoadViewAsync(false); var currentLoad = window.LoadViewAsync(true);
        first.SetResult(Scene()); await currentLoad; Assert.True(window.HasScene);
        third.SetResult(Scene()); await oldLoad; Assert.True(window.HasScene);
        window.Close(); Assert.False(window.HasScene);
        var pending = new TaskCompletionSource<ModelPreview>();
        service.LoadAsync("", "third.nif", "", Arg.Any<CancellationToken>()).Returns(pending.Task);
        var closing = new ModelPreviewWindow(service, new("", "third.nif", "first.nif", ""));
        var late = closing.LoadViewAsync(false); closing.Close(); pending.SetResult(Scene()); await late;
        Assert.False(closing.HasScene);
    });

    [Fact]
    public async Task RendererRendersTexturedPixels_AndRejectsInvalidGeometry() => await Sta(() =>
    {
        var scene = Scene();
        var model = PreviewSceneRenderer.Create(scene, true, out _);
        var group = new System.Windows.Media.Media3D.Model3DGroup(); group.Children.Add(model); group.Children.Add(new System.Windows.Media.Media3D.AmbientLight(Colors.White));
        var viewport = new Viewport3D { Width = 256, Height = 256, Camera = new System.Windows.Media.Media3D.PerspectiveCamera(new(0, 3, 4), new(0, -3, -4), new(0, 1, 0), 45) };
        viewport.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = group });
        var surface = new Border { Width = 256, Height = 256, Background = Brushes.Black, Child = viewport };
        surface.Measure(new Size(256, 256)); surface.Arrange(new Rect(0, 0, 256, 256)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface); var pixels = new byte[256 * 256 * 4]; bitmap.CopyPixels(pixels, 1024, 0);
        Assert.True(Enumerable.Range(0, 256 * 256).Count(i => pixels[i * 4] > 40 || pixels[i * 4 + 2] > 40) > 100);
        var invalid = scene with { Meshes = [scene.Meshes[0] with { Indices = [0, 1, 99999] }] };
        Assert.Throws<InvalidDataException>(() => PreviewSceneRenderer.Create(invalid, false, out _));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task PreviewControlsRenderBothLanguagesAtMinimumAndDesktopSizes() => await Sta(async () =>
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SkyrimModPacker.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var language in new[] { "uk", "en" })
        foreach (var width in new[] { 540, 960 })
        {
            var service = Substitute.For<IModelPreviewService>(); service.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Scene());
            var window = new ModelPreviewWindow(service, new("", "third.nif", "first.nif", ""));
            using (var stream = File.OpenRead(Path.Combine(root!.FullName, "src", "SkyrimModPacker.UI", "Resources", $"Strings.{language}.xaml")))
                window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream));
            await window.LoadViewAsync(false);
            var content = (FrameworkElement)window.Content; content.Measure(new Size(width, 700)); content.Arrange(new Rect(0, 0, width, 700)); content.UpdateLayout();
            Assert.True(window.Viewport.ActualWidth > 200); Assert.True(window.Viewport.ActualHeight > 100);
            var panel = ((DockPanel)window.Content).Children.OfType<WrapPanel>().Single();
            Assert.All(panel.Children.OfType<Button>(), b => Assert.True(b.ActualWidth + b.Margin.Left + b.Margin.Right >= b.DesiredSize.Width - 1));
            Assert.Equal((string)window.Resources.MergedDictionaries[0]["Preview_Title"], window.Title);
            var directory = Environment.GetEnvironmentVariable("SKYRIM_UI_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap(width, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"model-preview-{language}-{width}.png")); encoder.Save(output);
            }
            window.Close();
        }
    });
}
