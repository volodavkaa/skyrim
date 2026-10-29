using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.Infrastructure.ModelPreparation;

namespace SkyrimModPacker.UI.Controls;

public sealed record PreviewViewState(double Yaw, double Pitch, double Zoom);
public enum FitGizmoMode { None, Move, Rotate, Scale }

/// <summary>Single interactive viewport; stale or unsupported models never leave the previous scene visible.</summary>
public sealed class ModelPreviewControl : UserControl, IDisposable
{
    private readonly IModelPreviewService _service;
    private readonly Viewport3D _viewport = new();
    private readonly Canvas _gizmoCanvas = new() { ClipToBounds = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(10) };
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(10) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 213)), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), TextAlignment = TextAlignment.Center };
    private readonly Slider _zoom = new() { Minimum = 1.5, Maximum = 10, Value = 4, Width = 90, Margin = new Thickness(8) };
    private Button? _firstButton;
    private ModelPreviewRequest? _request;
    private UIElement[] _fullDetails = [];
    private Border? _surface;
    private Model3DGroup? _model;
    private ModelPreview? _scene;
    private PreviewSceneRenderer.ReflectionPreview? _reflection;
    private double _vertexColor = 1;
    private double _environmentStrength = 1, _specularStrength;
    private Color? _specularColor;
    private Matrix3D _fitRotation = Matrix3D.Identity;
    private Rect3D? _frameBounds;
    private FitGizmoMode _gizmoMode;
    private GizmoDrag? _gizmoDrag;
    private CharacterPose? _character;
    private ModelPreparationAdjustments _preparedFit = new(), _currentFit = new();
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _first, _disposed;
    private double _yaw = 25, _pitch = -10;
    private Point? _drag;
    public bool HasScene => _model != null;
    public string Status => _status.Text;
    public double OriginalExtent { get; private set; }
    private double? _normalizationExtent;
    private double _light = 1;
    private bool _settingView;
    public PreviewViewState ViewState => new(_yaw, _pitch, _zoom.Value);
    public event Action? SceneChanged;
    public event Action<PreviewViewState>? ViewChanged;
    public event Action<ModelPreparationAdjustments>? FitAdjustmentRequested;
    public void SetView(PreviewViewState state)
    {
        _settingView = true;
        try { _yaw = state.Yaw; _pitch = Math.Clamp(state.Pitch, -89, 89); _zoom.Value = Math.Clamp(state.Zoom, _zoom.Minimum, _zoom.Maximum); Render(); }
        finally { _settingView = false; }
    }
    public void SetNormalizationExtent(double? extent) { _normalizationExtent = extent is > 0 && double.IsFinite(extent.Value) ? extent : null; Render(); }
    public void SetEnvironment(double light, Color background)
    { _light = Math.Clamp(light, 0.05, 1); if (_surface != null) _surface.Background = new SolidColorBrush(background); Render(); }
    public void SetReflectionPreview(PreviewSceneRenderer.ReflectionPreview? reflection)
    {
        _reflection = reflection;
        if (_scene == null || _disposed) return;
        try
        {
            var composed = ComposeScene(_scene);
            _frameBounds ??= SceneBounds(composed);
            _model = PreviewSceneRenderer.Create(composed, true, out var warnings,
                out var extent, reflection, _vertexColor, _environmentStrength, _specularColor, _specularStrength, _frameBounds);
            OriginalExtent = extent;
            _status.Text = string.Join(Environment.NewLine, warnings.Where(w =>
                !w.StartsWith("Static geometry and diffuse texture only.", StringComparison.Ordinal)));
            Render();
        }
        catch (Exception ex) { _status.Text = "Помилка відображення відбиття: " + ex.Message; }
    }
    public void SetVertexColorPreview(double? value)
    {
        _vertexColor = value is double color ? Math.Clamp(color, 0, 1) : 1;
        SetReflectionPreview(_reflection);
    }
    public void SetShaderPreview(double environmentStrength, Color? specularColor, double specularStrength)
    {
        _environmentStrength = Math.Clamp(environmentStrength, 0, 4);
        _specularColor = specularColor;
        _specularStrength = Math.Clamp(specularStrength, 0, 4);
        SetReflectionPreview(_reflection);
    }
    public void SetFitRotation(FitRotation prepared, FitRotation requested)
    {
        _fitRotation = FitRotationPreview.Difference(prepared, requested);
        if (_character != null) SetReflectionPreview(_reflection); else Render();
    }
    public void SetFitAdjustment(ModelPreparationAdjustments prepared, ModelPreparationAdjustments current)
    {
        _preparedFit = prepared; _currentFit = current;
        SetFitRotation(new(prepared.RotationXDegrees, prepared.RotationYDegrees, prepared.RotationZDegrees),
            new(current.RotationXDegrees, current.RotationYDegrees, current.RotationZDegrees));
    }
    public void SetCharacterPose(CharacterPose? character)
    {
        _character = character;
        _frameBounds = null;
        if (_firstButton != null) _firstButton.IsEnabled = character == null;
        if (character != null && _first) { _first = false; _ = ReloadAsync(); return; }
        if (_scene != null) SetReflectionPreview(_reflection);
    }
    public void SetFitGizmoMode(FitGizmoMode mode)
    {
        _gizmoMode = mode; _gizmoDrag = null; Render();
    }
    private ModelPreview ComposeScene(ModelPreview weapon)
    {
        if (_character == null) return weapon;
        var prepared = _preparedFit; var current = _currentFit;
        var baselineOffset = new System.Numerics.Vector3((float)prepared.OffsetX, (float)prepared.OffsetY, (float)prepared.OffsetZ);
        var currentOffset = new System.Numerics.Vector3((float)current.OffsetX, (float)current.OffsetY, (float)current.OffsetZ);
        var scale = (float)(current.ScaleMultiplier / prepared.ScaleMultiplier);
        var parts = weapon.Meshes.Where(mesh => _character.IsSheathed || !mesh.Name.Equals("Scb", StringComparison.OrdinalIgnoreCase)).Select(mesh => mesh with { Positions = mesh.Positions.Select(p =>
        {
            var local = (p - baselineOffset) * scale;
            var display = _fitRotation.Transform(new Vector3D(local.X, local.Z, -local.Y));
            local = new System.Numerics.Vector3((float)display.X, (float)-display.Z, (float)display.Y) + currentOffset;
            return System.Numerics.Vector3.Transform(local, _character.WeaponTransform);
        }).ToArray() }).ToArray();
        return weapon with { Meshes = _character.Meshes.Concat(parts).ToArray(),
            Textures = weapon.Textures.Concat(_character.Textures).ToArray(),
            Warnings = weapon.Warnings.Concat(_character.Warnings).ToArray() };
    }
    public ModelPreviewControl(IModelPreviewService service)
    {
        _service = service; MinHeight = 380;
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(14, 22, 35)) };
        var title = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(128, 216, 207)), FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(12, 14, 12, 6) };
        title.SetResourceReference(TextBlock.TextProperty, "Smart_PreviewTitle"); DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var toolbar = new WrapPanel { Margin = new Thickness(6) };
        Button Button(string key, Action action)
        {
            var button = new Button { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(3) };
            button.SetResourceReference(ContentControl.ContentProperty, key); button.Click += (_, _) => action(); toolbar.Children.Add(button); return button;
        }
        Button("Smart_ThirdView", () => { _first = false; _ = ReloadAsync(); });
        _firstButton = Button("Smart_FirstView", () => { _first = true; _ = ReloadAsync(); });
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
        var canvas = new Grid(); canvas.Children.Add(_viewport); canvas.Children.Add(_empty); canvas.Children.Add(_gizmoCanvas);
        var surface = new Border { Child = canvas, Background = root.Background, ClipToBounds = true, MinHeight = 180 };
        _fullDetails = [title, limits]; _surface = surface;
        surface.MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(surface); surface.CaptureMouse(); };
        surface.MouseLeftButtonUp += (_, _) => { _drag = null; _gizmoDrag = null; surface.ReleaseMouseCapture(); };
        surface.LostMouseCapture += (_, _) => { _drag = null; _gizmoDrag = null; };
        surface.MouseMove += (_, e) =>
        {
            if (_gizmoDrag != null && e.LeftButton == MouseButtonState.Pressed)
            {
                ApplyGizmoDrag(e.GetPosition(_gizmoCanvas)); e.Handled = true; return;
            }
            if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(surface); _yaw += (point.X - _drag.Value.X) * 0.5;
            _pitch = Math.Clamp(_pitch + (point.Y - _drag.Value.Y) * 0.5, -89, 89); _drag = point; Render();
        };
        surface.MouseWheel += (_, e) => { _zoom.Value = Math.Clamp(_zoom.Value * (e.Delta > 0 ? 0.9 : 1.1), _zoom.Minimum, _zoom.Maximum); e.Handled = true; };
        _gizmoCanvas.SizeChanged += (_, _) => Render();
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
    public Task LoadAsync(ModelPreviewRequest? request, bool preserveView = false)
    {
        _request = request; _first = false; if (!preserveView) SetView(new(25, -10, 4)); return ReloadAsync();
    }
    private async Task ReloadAsync()
    {
        var generation = ++_generation; _cts?.Cancel(); _cts?.Dispose(); _cts = new(); var token = _cts.Token;
        _model = null; _scene = null; _frameBounds = null; OriginalExtent = 0; _viewport.Children.Clear(); _gizmoCanvas.Children.Clear(); SceneChanged?.Invoke();
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
            _scene = scene;
            var composed = ComposeScene(scene); _frameBounds = SceneBounds(composed);
            _model = PreviewSceneRenderer.Create(composed, true, out var warnings, out var extent, _reflection, _vertexColor,
                _environmentStrength, _specularColor, _specularStrength, _frameBounds); OriginalExtent = extent;
            _status.Text = string.Join(Environment.NewLine, warnings.Where(w => !w.StartsWith("Static geometry and diffuse texture only.", StringComparison.Ordinal))); Render();
            SceneChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && generation == _generation) { _model = null; _viewport.Children.Clear(); _empty.SetResourceReference(TextBlock.TextProperty, "Preview_Failed"); _status.Text = Label("Preview_Failed") + " " + ex.Message; } }
    }
    private void Render()
    {
        if (_model == null || _disposed) return;
        _empty.Visibility = Visibility.Collapsed;
        var lights = new Model3DGroup(); lights.Children.Add(new AmbientLight(Color.FromRgb(55, 55, 55)));
        var brightness = (byte)(_light * 255); lights.Children.Add(new DirectionalLight(Color.FromRgb(brightness, brightness, brightness), new Vector3D(-1, -1, -2)));
        var rotation = new Transform3DGroup();
        if (_character == null)
        {
            var factor = _currentFit.ScaleMultiplier / _preparedFit.ScaleMultiplier;
            rotation.Children.Add(new ScaleTransform3D(factor, factor, factor));
            rotation.Children.Add(new MatrixTransform3D(_fitRotation));
            var unit = OriginalExtent > 0 ? 2 / OriginalExtent : 0;
            rotation.Children.Add(new TranslateTransform3D((_currentFit.OffsetX - _preparedFit.OffsetX) * unit,
                (_currentFit.OffsetZ - _preparedFit.OffsetZ) * unit, -(_currentFit.OffsetY - _preparedFit.OffsetY) * unit));
        }
        if (_normalizationExtent is > 0) { var scale = OriginalExtent / _normalizationExtent.Value; rotation.Children.Add(new ScaleTransform3D(scale, scale, scale)); }
        rotation.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _yaw)));
        rotation.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _pitch)));
        _viewport.Children.Clear(); _viewport.Children.Add(new ModelVisual3D { Content = lights });
        _viewport.Children.Add(new ModelVisual3D { Content = _model, Transform = rotation });
        _viewport.Camera = new PerspectiveCamera(new Point3D(0, 0, _zoom.Value), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45) { NearPlaneDistance = 0.01, FarPlaneDistance = 100 };
        DrawGizmo(rotation);
        if (!_settingView) ViewChanged?.Invoke(ViewState);
    }
    private sealed record GizmoDrag(FitGizmoMode Mode, int Axis, Point Start, Vector Direction,
        double PixelsPerUnit, ModelPreparationAdjustments Initial);

    private static Rect3D SceneBounds(ModelPreview scene)
    {
        var bounds = Rect3D.Empty;
        foreach (var position in scene.Meshes.SelectMany(mesh => mesh.Positions))
            bounds.Union(new Point3D(position.X, position.Z, -position.Y));
        return bounds;
    }

    private void DrawGizmo(Transform3DGroup viewTransform)
    {
        _gizmoCanvas.Children.Clear();
        if (_gizmoMode == FitGizmoMode.None || _scene == null || _model == null ||
            _gizmoCanvas.ActualWidth < 50 || _gizmoCanvas.ActualHeight < 50) return;
        var character = _character;
        var offset = new System.Numerics.Vector3((float)_currentFit.OffsetX, (float)_currentFit.OffsetY, (float)_currentFit.OffsetZ);
        Point3D origin;
        if (character == null)
        {
            origin = viewTransform.Transform(new Point3D(0, 0, 0));
        }
        else
        {
            var point = System.Numerics.Vector3.Transform(offset, character.WeaponTransform);
            origin = viewTransform.Transform(_model.Transform.Transform(new Point3D(point.X, point.Z, -point.Y)));
        }
        Point? Project(Point3D p)
        {
            var depth = _zoom.Value - p.Z;
            if (depth <= 0.02) return null;
            var focal = _gizmoCanvas.ActualWidth / (2 * Math.Tan(Math.PI / 8));
            return new Point(_gizmoCanvas.ActualWidth / 2 + p.X * focal / depth,
                _gizmoCanvas.ActualHeight / 2 - p.Y * focal / depth);
        }
        var center = Project(origin);
        if (center == null) return;
        var colors = new[] { Colors.IndianRed, Colors.LimeGreen, Colors.DodgerBlue };
        var axes = new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ };
        for (var i = 0; i < axes.Length; i++)
        {
            var local = axes[i];
            if (character != null) local = System.Numerics.Vector3.TransformNormal(local, character.WeaponTransform);
            var axis = new Vector3D(local.X, local.Z, -local.Y);
            if (character != null) axis = _model.Transform.Transform(axis);
            else axis *= OriginalExtent > 0 ? 2 / OriginalExtent : 0;
            var view = viewTransform.Transform(axis);
            // Offsets are in the parent coordinate system and should not rotate with the model.
            if (character == null)
            {
                var camera = new Transform3DGroup();
                camera.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _yaw)));
                camera.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _pitch)));
                view = camera.Transform(axis);
            }
            var unitEnd = Project(origin + view);
            if (unitEnd == null) continue;
            var perUnit = unitEnd.Value - center.Value;
            var pixelsPerUnit = perUnit.Length;
            if (pixelsPerUnit < 0.015) continue;
            var direction = perUnit / pixelsPerUnit;
            var end = center.Value + direction * 74;
            var line = new Line { X1 = center.Value.X, Y1 = center.Value.Y, X2 = end.X, Y2 = end.Y,
                Stroke = new SolidColorBrush(colors[i]), StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round };
            var hit = new Line { X1 = line.X1, Y1 = line.Y1, X2 = line.X2, Y2 = line.Y2,
                Stroke = Brushes.Transparent, StrokeThickness = 18, Cursor = Cursors.Hand,
                ToolTip = $"{(_gizmoMode == FitGizmoMode.Rotate ? "Поворот" : "Зсув")} {"XYZ"[i]}: тягніть уздовж стрілки" };
            var normal = new Vector(-direction.Y, direction.X);
            var tip = new Polygon { Fill = new SolidColorBrush(colors[i]), IsHitTestVisible = false,
                Points = new PointCollection { end, end - direction * 14 + normal * 6, end - direction * 14 - normal * 6 } };
            var label = new TextBlock { Text = "XYZ"[i].ToString(), Foreground = Brushes.White,
                FontWeight = FontWeights.Bold, Background = new SolidColorBrush(Color.FromArgb(180, 15, 20, 28)), IsHitTestVisible = false };
            Canvas.SetLeft(label, end.X + normal.X * 9 - 5); Canvas.SetTop(label, end.Y + normal.Y * 9 - 8);
            _gizmoCanvas.Children.Add(line); _gizmoCanvas.Children.Add(tip); _gizmoCanvas.Children.Add(label);
            if (_gizmoMode is FitGizmoMode.Move or FitGizmoMode.Rotate)
            {
                var axisNumber = i; var axisDirection = direction; var unit = pixelsPerUnit;
                hit.MouseLeftButtonDown += (_, e) => StartGizmoDrag(axisNumber, axisDirection, unit, e);
                _gizmoCanvas.Children.Add(hit);
            }
        }
        if (_gizmoMode == FitGizmoMode.Scale)
        {
            var handle = new Ellipse { Width = 22, Height = 22, Fill = Brushes.Gold, Stroke = Brushes.Black,
                StrokeThickness = 2, Cursor = Cursors.SizeAll, ToolTip = "Перетягніть для масштабу" };
            Canvas.SetLeft(handle, center.Value.X - 11); Canvas.SetTop(handle, center.Value.Y - 11);
            handle.MouseLeftButtonDown += (_, e) => StartGizmoDrag(0, new Vector(0, -1), 1, e);
            _gizmoCanvas.Children.Add(handle);
        }
    }

    private void StartGizmoDrag(int axis, Vector direction, double pixelsPerUnit, MouseButtonEventArgs e)
    {
        if (_surface == null || FitAdjustmentRequested == null) return;
        _gizmoDrag = new GizmoDrag(_gizmoMode, axis, e.GetPosition(_gizmoCanvas), direction, pixelsPerUnit, _currentFit);
        _drag = null; _surface.CaptureMouse(); e.Handled = true;
    }

    private void ApplyGizmoDrag(Point point)
    {
        if (_gizmoDrag is not { } drag) return;
        var delta = point - drag.Start;
        var initial = drag.Initial;
        ModelPreparationAdjustments next;
        if (drag.Mode == FitGizmoMode.Scale)
        {
            var scale = Math.Clamp(initial.ScaleMultiplier * Math.Exp(-delta.Y / 120), 0.25, 4);
            next = initial with { ScaleMultiplier = scale };
        }
        else if (drag.Mode == FitGizmoMode.Rotate)
        {
            var change = Vector.Multiply(delta, drag.Direction) * 0.6;
            next = drag.Axis switch
            {
                0 => initial with { RotationXDegrees = Math.Clamp(initial.RotationXDegrees + change, -180, 180) },
                1 => initial with { RotationYDegrees = Math.Clamp(initial.RotationYDegrees + change, -180, 180) },
                _ => initial with { RotationZDegrees = Math.Clamp(initial.RotationZDegrees + change, -180, 180) }
            };
        }
        else
        {
            var change = Vector.Multiply(delta, drag.Direction) / drag.PixelsPerUnit;
            next = drag.Axis switch
            {
                0 => initial with { OffsetX = Math.Clamp(initial.OffsetX + change, -100, 100) },
                1 => initial with { OffsetY = Math.Clamp(initial.OffsetY + change, -100, 100) },
                _ => initial with { OffsetZ = Math.Clamp(initial.OffsetZ + change, -100, 100) }
            };
        }
        FitAdjustmentRequested?.Invoke(next);
    }

    private string Label(string key) => TryFindResource(key) as string ?? key;
    public void Dispose() { _disposed = true; _generation++; _cts?.Cancel(); _cts?.Dispose(); _model = null; _scene = null; _viewport.Children.Clear(); }
}
