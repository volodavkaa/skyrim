using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.Views;

/// <summary>Read-only static SSE viewer. Failed loads clear the previous model.</summary>
public sealed class ModelPreviewWindow : Window
{
    private readonly IModelPreviewService _service;
    private readonly ModelPreviewRequest _request;
    private readonly Viewport3D _viewport = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 8), FontWeight = FontWeights.SemiBold };
    private readonly CheckBox _textures = new() { IsChecked = true, Margin = new Thickness(8), Foreground = Brushes.White };
    private readonly Slider _yaw = new() { Minimum = -180, Maximum = 180, Width = 110, Value = 25 };
    private readonly Slider _zoom = new() { Minimum = 1.5, Maximum = 10, Width = 100, Value = 4 };
    private readonly Slider _light = new() { Minimum = 0.05, Maximum = 1, Width = 100, Value = 0.85 };
    private CancellationTokenSource? _cts;
    private ModelPreview? _scene;
    private Model3DGroup? _model;
    private IReadOnlyList<string> _renderWarnings = [];
    private bool _renderTextures;
    private int _generation;
    private bool _closed;
    private Point? _drag;
    private double _pitch = -10;

    public bool HasScene => _viewport.Children.Count > 0;
    public string Status => _status.Text;
    public Viewport3D Viewport => _viewport;

    public ModelPreviewWindow(IModelPreviewService service, ModelPreviewRequest request)
    {
        _service = service; _request = request;
        Width = 960; Height = 740; MinWidth = 540; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(TitleProperty, "Preview_Title");
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 42)); Foreground = Brushes.White; FontSize = 14;
        var root = new DockPanel { Background = Background }; var controls = new WrapPanel { Margin = new Thickness(8) };
        Button Button(string key, Action action)
        {
            var button = new Button { Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6), Background = new SolidColorBrush(Color.FromRgb(58, 63, 73)), Foreground = Brushes.White };
            button.SetResourceReference(ContentControl.ContentProperty, key); button.Click += (_, _) => action(); controls.Children.Add(button); return button;
        }
        Button("Models_Third", () => _ = LoadViewAsync(false)); Button("Models_First", () => _ = LoadViewAsync(true));
        Button("Preview_Cancel", () => { _generation++; _cts?.Cancel(); Clear(); _status.Text = Label("Preview_Cancelled", "Preview cancelled."); });
        Button("Preview_Reset", () => { _pitch = -10; _yaw.Value = 25; _zoom.Value = 4; Render(); });
        _textures.SetResourceReference(ContentControl.ContentProperty, "Preview_Textures"); controls.Children.Add(_textures);
        void Slider(string key, Slider slider)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.TextProperty, key); group.Children.Add(text); group.Children.Add(slider); controls.Children.Add(group);
            slider.ValueChanged += (_, _) => Render();
        }
        Slider("Preview_Rotate", _yaw); Slider("Preview_Zoom", _zoom); Slider("Preview_Light", _light);
        _textures.Checked += (_, _) => Render(); _textures.Unchecked += (_, _) => Render();
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) }; note.SetResourceReference(TextBlock.TextProperty, "Preview_Limits");
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        DockPanel.SetDock(_source, Dock.Top); root.Children.Add(_source);
        var messages = new ScrollViewer { Content = _status, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(messages, Dock.Bottom); root.Children.Add(messages);
        var surface = new Border { Background = Background, Child = _viewport, ClipToBounds = true };
        surface.MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(surface); surface.CaptureMouse(); };
        surface.MouseLeftButtonUp += (_, _) => { _drag = null; surface.ReleaseMouseCapture(); };
        surface.LostMouseCapture += (_, _) => _drag = null;
        surface.MouseMove += (_, e) =>
        {
            if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(surface); _pitch = Math.Clamp(_pitch + (point.Y - _drag.Value.Y) * 0.5, -89, 89);
            _yaw.Value = Math.Clamp(_yaw.Value + (point.X - _drag.Value.X) * 0.5, -180, 180); _drag = point; Render();
        };
        surface.MouseWheel += (_, e) => { _zoom.Value = Math.Clamp(_zoom.Value * (e.Delta > 0 ? 0.9 : 1.1), _zoom.Minimum, _zoom.Maximum); e.Handled = true; };
        root.Children.Add(surface); Content = root;
        Loaded += async (_, _) => await LoadViewAsync(false);
        Closed += (_, _) => { _closed = true; _generation++; _cts?.Cancel(); _cts?.Dispose(); Clear(); };
    }

    public async Task LoadViewAsync(bool firstPerson)
    {
        var generation = ++_generation; _cts?.Cancel(); _cts?.Dispose(); _cts = new(); var token = _cts.Token;
        Clear(); var path = firstPerson ? _request.FirstPersonPath : _request.ThirdPersonPath;
        _source.Text = Label(firstPerson ? "Models_First" : "Models_Third", firstPerson ? "First-person model" : "Third-person model") + " — " + path;
        if (string.IsNullOrWhiteSpace(path)) { _status.Text = Label("Preview_NoModel", "No model is assigned to this view."); return; }
        _status.Text = Label("Preview_Loading", "Loading…") + Environment.NewLine + path;
        try
        {
            var scene = await _service.LoadAsync(_request.ProjectDirectory, path, _request.GameDataPath, token);
            if (_closed || generation != _generation || token.IsCancellationRequested) return;
            _scene = scene; Render();
        }
        catch (OperationCanceledException) { if (generation == _generation && !_closed) { Clear(); _status.Text = Label("Preview_Cancelled", "Preview cancelled."); } }
        catch (Exception ex) { if (generation == _generation && !_closed) { Clear(); _status.Text = Label("Preview_Failed", "Preview unavailable:") + " " + ex.Message; } }
    }

    private void Clear() { _scene = null; _model = null; _renderWarnings = []; _viewport.Children.Clear(); }
    private void Render()
    {
        if (_scene == null || _closed) return;
        try
        {
            if (_model == null || _renderTextures != (_textures.IsChecked == true))
            {
                _model = PreviewSceneRenderer.Create(_scene, _textures.IsChecked == true, out _renderWarnings);
                _renderTextures = _textures.IsChecked == true;
            }
            var group = new Model3DGroup(); var rotation = new Transform3DGroup();
            rotation.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _yaw.Value)));
            rotation.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _pitch)));
            var visual = new ModelVisual3D { Content = _model, Transform = rotation };
            group.Children.Add(new AmbientLight(Color.FromRgb(45, 45, 45)));
            var intensity = (byte)(_light.Value * 255); group.Children.Add(new DirectionalLight(Color.FromRgb(intensity, intensity, intensity), new Vector3D(-1, -1, -2)));
            _viewport.Children.Clear(); _viewport.Children.Add(new ModelVisual3D { Content = group }); _viewport.Children.Add(visual);
            _viewport.Camera = new PerspectiveCamera(new Point3D(0, 0, _zoom.Value), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45) { NearPlaneDistance = 0.01, FarPlaneDistance = 100 };
            _status.Text = string.Join(Environment.NewLine, _renderWarnings.Where(w => !w.StartsWith("Static geometry and diffuse texture only.", StringComparison.Ordinal)));
        }
        catch (Exception ex) { Clear(); _status.Text = Label("Preview_Failed", "Preview unavailable:") + " " + ex.Message; }
    }
    private string Label(string key, string fallback) => TryFindResource(key) as string ?? fallback;
}
