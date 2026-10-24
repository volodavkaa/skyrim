using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.Controls;

/// <summary>Single interactive viewport; stale or unsupported models never leave the previous scene visible.</summary>
public sealed class ModelPreviewControl : UserControl, IDisposable
{
    private readonly IModelPreviewService _service;
    private readonly Viewport3D _viewport = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(10) };
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(10) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 213)), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), TextAlignment = TextAlignment.Center };
    private readonly Slider _zoom = new() { Minimum = 1.5, Maximum = 10, Value = 4, Width = 90, Margin = new Thickness(8) };
    private ModelPreviewRequest? _request;
    private UIElement[] _fullDetails = [];
    private Border? _surface;
    private Model3DGroup? _model;
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _first, _disposed;
    private double _yaw = 25, _pitch = -10;
    private Point? _drag;
    public bool HasScene => _model != null;
    public string Status => _status.Text;
    public ModelPreviewControl(IModelPreviewService service)
    {
        _service = service; MinHeight = 380;
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(14, 22, 35)) };
        var title = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(128, 216, 207)), FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(12, 14, 12, 6) };
        title.SetResourceReference(TextBlock.TextProperty, "Smart_PreviewTitle"); DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var toolbar = new WrapPanel { Margin = new Thickness(6) };
        void Button(string key, Action action)
        {
            var button = new Button { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(3) };
            button.SetResourceReference(ContentControl.ContentProperty, key); button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        Button("Smart_ThirdView", () => { _first = false; _ = ReloadAsync(); });
        Button("Smart_FirstView", () => { _first = true; _ = ReloadAsync(); });
        Button("Smart_Fit", () => { _zoom.Value = 4; _yaw = 25; _pitch = -10; Render(); });
        Button("Smart_Expand", () => { if (_request != null) new Views.ModelPreviewWindow(_service, _request) { Owner = Window.GetWindow(this) }.ShowDialog(); });
        toolbar.Children.Add(_zoom); _zoom.ValueChanged += (_, _) => Render();
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(_source, Dock.Top); root.Children.Add(_source);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(10) };
        note.SetResourceReference(TextBlock.TextProperty, "Smart_PreviewLimits");
        var limits = new Expander { Content = note, Margin = new Thickness(10, 0, 10, 10) }; limits.SetResourceReference(HeaderedContentControl.HeaderProperty, "Smart_PreviewDetails");
        DockPanel.SetDock(limits, Dock.Bottom); root.Children.Add(limits);
        var messages = new ScrollViewer { Content = _status, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(messages, Dock.Bottom); root.Children.Add(messages);
        _empty.SetResourceReference(TextBlock.TextProperty, "Smart_PreviewEmpty");
        var canvas = new Grid(); canvas.Children.Add(_viewport); canvas.Children.Add(_empty);
        var surface = new Border { Child = canvas, Background = root.Background, ClipToBounds = true, MinHeight = 180 };
        _fullDetails = [title, limits]; _surface = surface;
        surface.MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(surface); surface.CaptureMouse(); };
        surface.MouseLeftButtonUp += (_, _) => { _drag = null; surface.ReleaseMouseCapture(); };
        surface.LostMouseCapture += (_, _) => _drag = null;
        surface.MouseMove += (_, e) =>
        {
            if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(surface); _yaw += (point.X - _drag.Value.X) * 0.5;
            _pitch = Math.Clamp(_pitch + (point.Y - _drag.Value.Y) * 0.5, -89, 89); _drag = point; Render();
        };
        surface.MouseWheel += (_, e) => { _zoom.Value = Math.Clamp(_zoom.Value * (e.Delta > 0 ? 0.9 : 1.1), _zoom.Minimum, _zoom.Maximum); e.Handled = true; };
        root.Children.Add(surface); Content = root;
    }
    public void SetCompact(bool compact)
    {
        MinHeight = compact ? 260 : 380;
        foreach (var detail in _fullDetails) detail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (_surface != null) _surface.MinHeight = compact ? 80 : 180;
        _source.FontSize = compact ? 11 : 14;
        _source.Margin = compact ? new Thickness(8, 2, 8, 4) : new Thickness(10);
    }
    public Task LoadAsync(ModelPreviewRequest? request)
    {
        _request = request; _first = false; _yaw = 25; _pitch = -10; _zoom.Value = 4; return ReloadAsync();
    }
    private async Task ReloadAsync()
    {
        var generation = ++_generation; _cts?.Cancel(); _cts?.Dispose(); _cts = new(); var token = _cts.Token;
        _model = null; _viewport.Children.Clear();
        _empty.Visibility = Visibility.Visible; _empty.SetResourceReference(TextBlock.TextProperty, "Smart_PreviewEmpty");
        if (_request == null) { _source.Text = ""; _status.Text = Label("Preview_NoModel"); return; }
        var request = _request; var path = _first ? request.FirstPersonPath : request.ThirdPersonPath;
        _source.Text = Label(_first ? "Models_First" : "Models_Third") + " — " + path;
        if (string.IsNullOrWhiteSpace(path)) { _status.Text = Label("Preview_NoModel"); return; }
        _status.Text = Label("Preview_Loading");
        try
        {
            var scene = await _service.LoadAsync(request.ProjectDirectory, path, request.GameDataPath, token);
            if (_disposed || token.IsCancellationRequested || generation != _generation) return;
            _model = PreviewSceneRenderer.Create(scene, true, out var warnings);
            _status.Text = string.Join(Environment.NewLine, warnings.Where(w => !w.StartsWith("Static geometry and diffuse texture only.", StringComparison.Ordinal))); Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && generation == _generation) { _model = null; _viewport.Children.Clear(); _empty.SetResourceReference(TextBlock.TextProperty, "Preview_Failed"); _status.Text = Label("Preview_Failed") + " " + ex.Message; } }
    }
    private void Render()
    {
        if (_model == null || _disposed) return;
        _empty.Visibility = Visibility.Collapsed;
        var lights = new Model3DGroup(); lights.Children.Add(new AmbientLight(Color.FromRgb(55, 55, 55)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)));
        var rotation = new Transform3DGroup();
        rotation.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _yaw)));
        rotation.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _pitch)));
        _viewport.Children.Clear(); _viewport.Children.Add(new ModelVisual3D { Content = lights });
        _viewport.Children.Add(new ModelVisual3D { Content = _model, Transform = rotation });
        _viewport.Camera = new PerspectiveCamera(new Point3D(0, 0, _zoom.Value), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45) { NearPlaneDistance = 0.01, FarPlaneDistance = 100 };
    }
    private string Label(string key) => TryFindResource(key) as string ?? key;
    public void Dispose() { _disposed = true; _generation++; _cts?.Cancel(); _cts?.Dispose(); _model = null; _viewport.Children.Clear(); }
}
