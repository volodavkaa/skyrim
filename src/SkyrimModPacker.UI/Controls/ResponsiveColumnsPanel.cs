using System.Windows;
using System.Windows.Controls;

namespace SkyrimModPacker.UI.Controls;

/// <summary>Reflows form sections into columns while measuring each row at its natural height.</summary>
public sealed class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(360d, FrameworkPropertyMetadataOptions.AffectsMeasure), value => (double)value > 0 && double.IsFinite((double)value));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure), value => (int)value > 0);
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsMeasure), value => (double)value >= 0 && double.IsFinite((double)value));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private int Columns(double width, int count) => Math.Max(1, Math.Min(Math.Min(MaxColumns, Math.Max(1, count)),
        double.IsFinite(width) ? Math.Max(1, (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap))) : 1));

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        var columns = Columns(availableSize.Width, children.Length);
        var columnWidth = double.IsFinite(availableSize.Width) ? Math.Max(0, (availableSize.Width - Gap * (columns - 1)) / columns) : MinColumnWidth;
        var height = 0d;
        for (var start = 0; start < children.Length; start += columns)
        {
            var rowHeight = 0d;
            for (var index = start; index < Math.Min(start + columns, children.Length); index++)
            {
                children[index].Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, children[index].DesiredSize.Height);
            }
            height += rowHeight + (start == 0 ? 0 : Gap);
        }
        return new Size(children.Length == 0 ? 0 : columnWidth * columns + Gap * (columns - 1), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        var columns = Columns(finalSize.Width, children.Length);
        var columnWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var top = 0d;
        for (var start = 0; start < children.Length; start += columns)
        {
            var rowHeight = 0d;
            for (var index = start; index < Math.Min(start + columns, children.Length); index++)
            {
                var child = children[index];
                child.Arrange(new Rect((index - start) * (columnWidth + Gap), top, columnWidth, child.DesiredSize.Height));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            top += rowHeight + Gap;
        }
        return finalSize;
    }
}
