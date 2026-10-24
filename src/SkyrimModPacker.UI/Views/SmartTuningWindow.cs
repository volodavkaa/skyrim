using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views;

public sealed class SmartTuningWindow : Window
{
    public SmartTuningPreview? Preview { get; private set; }
    public SmartTuningWindow(Func<bool, bool, bool, SmartTuningPreview> calculate, Func<string, string> label, bool material, bool crafting, bool tempering, bool keepManual = true, string? keepManualLabel = null)
    {
        Title = label("Smart_TuningPreviewTitle"); Width = 780; Height = 740; MinWidth = 520; MinHeight = 480; FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(20), Background = Background }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = Title, FontSize = 24, TextWrapping = TextWrapping.Wrap });
        var keep = new CheckBox { Content = new TextBlock { Text = keepManualLabel ?? label("Smart_TuningKeepManual"), TextWrapping = TextWrapping.Wrap }, IsChecked = keepManual }; top.Children.Add(keep);
        var forge = new CheckBox { Content = label("Smart_Crafting"), IsChecked = crafting, Visibility = material ? Visibility.Visible : Visibility.Collapsed }; top.Children.Add(forge);
        var wheel = new CheckBox { Content = label("Smart_Tempering"), IsChecked = tempering, Visibility = material ? Visibility.Visible : Visibility.Collapsed }; top.Children.Add(wheel);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var cancel = new Button { Content = label("Smart_Cancel"), IsCancel = true }; var apply = new Button { Content = label("Smart_TuningApply"), Style = (Style)FindResource("SmartPrimary") };
        bottom.Children.Add(cancel); bottom.Children.Add(apply);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(details);
        void Refresh()
        { try { Preview = calculate(keep.IsChecked == true, forge.IsChecked == true, wheel.IsChecked == true); details.Text = Preview.Description; apply.IsEnabled = true; }
          catch (Exception ex) { Preview = null; details.Text = ex.Message; apply.IsEnabled = false; } }
        foreach (var checkbox in new[] { keep, forge, wheel }) { checkbox.Checked += (_, _) => Refresh(); checkbox.Unchecked += (_, _) => Refresh(); }
        apply.Click += (_, _) => { if (Preview != null) DialogResult = true; }; Loaded += (_, _) => Refresh();
    }
}
