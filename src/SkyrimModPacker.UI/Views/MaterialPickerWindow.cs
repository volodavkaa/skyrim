using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.Views;

/// <summary>Searches a session index; no master-file IO takes place during typing.</summary>
public sealed class MaterialPickerWindow : Window
{
    private sealed record Category(string Key, string Name);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ListBox _results = new();
    public RecordOption? Selection { get; private set; }
    public MaterialPickerWindow(MaterialSearchIndex index, Func<string, string> label, RecordOption? current)
    {
        Title = label("Smart_MaterialPicker"); Width = 680; Height = 660; MinWidth = 500; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)); Foreground = Brushes.White; FontSize = 14;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(20), Background = Background }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = label("Smart_MaterialPicker"), FontSize = 24, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = label("Smart_MaterialSearchHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var search = new TextBox(); top.Children.Add(search);
        var category = new ComboBox { DisplayMemberPath = "Name", ItemsSource = new[] { "", "Metal", "Leather", "Wood", "Dragon", "Gems", "Special" }
            .Select(key => new Category(key, label("Smart_MaterialCategory_" + (key.Length == 0 ? "All" : key)))).ToArray(), SelectedIndex = 0 };
        top.Children.Add(category);
        var other = new CheckBox { Content = label("Smart_ShowOtherItems") }; top.Children.Add(other);
        var count = new TextBlock { Margin = new Thickness(0, 10, 0, 10), TextWrapping = TextWrapping.Wrap }; top.Children.Add(count);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        if (current != null)
        {
            var keep = new Button { Content = label("Smart_KeepCurrent") + ": " + current.DisplayName, HorizontalContentAlignment = HorizontalAlignment.Left };
            keep.Click += (_, _) => { Selection = current; DialogResult = true; }; bottom.Children.Add(keep);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = label("Smart_SelectMaterial"), IsDefault = true, IsEnabled = false, Style = (Style)FindResource("SmartPrimary") };
        var cancel = new Button { Content = label("Smart_Cancel"), IsCancel = true };
        actions.Children.Add(cancel); actions.Children.Add(accept); bottom.Children.Add(actions);
        accept.Click += (_, _) => { if (_results.SelectedItem is RecordOption option) { Selection = option; DialogResult = true; } };
        _results.SelectionChanged += (_, _) => accept.IsEnabled = _results.SelectedItem is RecordOption;
        _results.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel Margin="2,5"><TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" TextWrapping="Wrap"/><TextBlock Text="{Binding EditorId}" FontSize="11" Foreground="#B4C3D5"/><TextBlock Text="{Binding Reference.PluginName}" FontSize="11" Foreground="#B4C3D5"/></StackPanel></DataTemplate>
            """);
        VirtualizingPanel.SetIsVirtualizing(_results, true); VirtualizingPanel.SetVirtualizationMode(_results, VirtualizationMode.Recycling);
        root.Children.Add(_results);
        void Refresh()
        {
            var result = index.Search(search.Text, (category.SelectedItem as Category)?.Key ?? "", other.IsChecked == true);
            _results.ItemsSource = result.Items;
            count.Text = string.Format(label("Smart_MaterialResults"), result.Total, result.Items.Count);
        }
        _timer.Tick += (_, _) => { _timer.Stop(); Refresh(); };
        search.TextChanged += (_, _) => { _timer.Stop(); _timer.Start(); };
        category.SelectionChanged += (_, _) => Refresh(); other.Checked += (_, _) => Refresh(); other.Unchecked += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); search.Focus(); }; Closed += (_, _) => _timer.Stop();
    }
}
