using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SkyrimModPacker.UI.Controls;

/// <summary>Hue ring and a hue/white/black triangle for selecting an RGB color.</summary>
public sealed class TriangleColorPicker : FrameworkElement
{
    private const int Size = 220;
    private const double Center = 110, RingOuter = 104, RingInner = 88;
    private static readonly Point HueVertex = new(110, 35), WhiteVertex = new(45, 147), BlackVertex = new(175, 147);
    private double _hue, _hueWeight, _whiteWeight = 1;
    private bool _dragRing, _dragTriangle;
    private BitmapSource? _bitmap;
    public event Action<Color>? ColorChanged;

    public TriangleColorPicker()
    {
        Width = Size; Height = Size; Focusable = true;
        Cursor = Cursors.Cross;
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        ToolTip = "Коло — відтінок, трикутник — насиченість і яскравість";
    }

    public Color SelectedColor
    {
        get
        {
            var hue = HueColor(_hue);
            return Color.FromRgb(Channel(_whiteWeight + _hueWeight * hue.R / 255),
                Channel(_whiteWeight + _hueWeight * hue.G / 255), Channel(_whiteWeight + _hueWeight * hue.B / 255));
        }
        set
        {
            var r = value.R / 255.0; var g = value.G / 255.0; var b = value.B / 255.0;
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;
            if (delta > 0.00001)
            {
                _hue = max == r ? ((g - b) / delta + 6) % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
                _hue /= 6;
            }
            _hueWeight = delta; _whiteWeight = min;
            _bitmap = null; InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _bitmap ??= DrawPalette();
        dc.DrawImage(_bitmap, new Rect(0, 0, Size, Size));
        var angle = _hue * Math.PI * 2 - Math.PI / 2;
        var ringPoint = new Point(Center + 96 * Math.Cos(angle), Center + 96 * Math.Sin(angle));
        var blackWeight = 1 - _hueWeight - _whiteWeight;
        var point = new Point(_hueWeight * HueVertex.X + _whiteWeight * WhiteVertex.X + blackWeight * BlackVertex.X,
            _hueWeight * HueVertex.Y + _whiteWeight * WhiteVertex.Y + blackWeight * BlackVertex.Y);
        foreach (var marker in new[] { ringPoint, point })
        {
            dc.DrawEllipse(null, new Pen(Brushes.Black, 3), marker, 5, 5);
            dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), marker, 5, 5);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        var point = e.GetPosition(this); var dx = point.X - Center; var dy = point.Y - Center;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        _dragRing = distance >= RingInner - 5 && distance <= RingOuter + 5;
        _dragTriangle = !_dragRing && TriangleWeights(point, out _, out _, out _);
        if (!_dragRing && !_dragTriangle) return;
        CaptureMouse(); UpdateFromPoint(point); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (_dragRing || _dragTriangle) { UpdateFromPoint(e.GetPosition(this)); e.Handled = true; } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); if (_dragRing || _dragTriangle) { _dragRing = _dragTriangle = false; ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); _dragRing = _dragTriangle = false; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: _hue = (_hue + 0.99) % 1; _bitmap = null; break;
            case Key.Right: _hue = (_hue + 0.01) % 1; _bitmap = null; break;
            case Key.Up: _whiteWeight = Math.Min(1 - _hueWeight, _whiteWeight + 0.02); break;
            case Key.Down: _whiteWeight = Math.Max(0, _whiteWeight - 0.02); break;
            default: return;
        }
        InvalidateVisual(); ColorChanged?.Invoke(SelectedColor); e.Handled = true;
    }

    private void UpdateFromPoint(Point point)
    {
        if (_dragRing)
        {
            _hue = (Math.Atan2(point.Y - Center, point.X - Center) + Math.PI / 2) / (2 * Math.PI);
            _hue = (_hue + 1) % 1; _bitmap = null;
        }
        else
        {
            TriangleWeights(point, out var h, out var w, out var b);
            h = Math.Max(0, h); w = Math.Max(0, w); b = Math.Max(0, b);
            var sum = h + w + b;
            _hueWeight = h / sum; _whiteWeight = w / sum;
        }
        InvalidateVisual(); ColorChanged?.Invoke(SelectedColor);
    }

    private BitmapSource DrawPalette()
    {
        var pixels = new byte[Size * Size * 4]; var baseColor = HueColor(_hue);
        for (var y = 0; y < Size; y++) for (var x = 0; x < Size; x++)
        {
            var dx = x - Center; var dy = y - Center; var distance = Math.Sqrt(dx * dx + dy * dy);
            Color color;
            if (distance >= RingInner && distance <= RingOuter)
                color = HueColor(((Math.Atan2(dy, dx) + Math.PI / 2) / (2 * Math.PI) + 1) % 1);
            else if (TriangleWeights(new Point(x, y), out var h, out var w, out _))
                color = Color.FromRgb(Channel(w + h * baseColor.R / 255), Channel(w + h * baseColor.G / 255), Channel(w + h * baseColor.B / 255));
            else continue;
            var offset = (y * Size + x) * 4; pixels[offset] = color.B; pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
        }
        var image = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4); image.Freeze(); return image;
    }
    private static bool TriangleWeights(Point p, out double h, out double w, out double b)
    {
        h = (147 - p.Y) / 112;
        w = (175 - p.X - 65 * h) / 130;
        b = 1 - h - w;
        return h >= 0 && w >= 0 && b >= 0;
    }
    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static Color HueColor(double hue)
    {
        var h = hue * 6; var x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = h switch { < 1 => (1d, x, 0d), < 2 => (x, 1d, 0d), < 3 => (0d, 1d, x),
            < 4 => (0d, x, 1d), < 5 => (x, 0d, 1d), _ => (1d, 0d, x) };
        return Color.FromRgb(Channel(r), Channel(g), Channel(b));
    }
}
