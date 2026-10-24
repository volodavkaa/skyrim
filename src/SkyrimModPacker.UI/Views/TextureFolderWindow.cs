using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.Views;

public sealed class TextureFolderWindow : Window
{
    public TextureFolderPlan? Plan { get; private set; }
    public TextureFolderWindow(string source, Func<string, Task<TextureFolderPlan>> preview, Func<Task<ModelPreview?>> model, Func<string, string> label)
    {
        Title = label("Smart_TextureFolderTitle"); Width = 850; Height = 680; MinWidth = 600; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)); Foreground = Brushes.White; FontSize = 14;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(20), Background = Background }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = Title, FontSize = 24 });
        top.Children.Add(new TextBlock { Text = source, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        top.Children.Add(new TextBlock { Text = label("Smart_TextureMapping"), TextWrapping = TextWrapping.Wrap });
        var mapping = new TextBox { Text = NaturalMapping(source) }; top.Children.Add(mapping);
        var refresh = new Button { Content = label("Smart_TexturePreview") }; top.Children.Add(refresh);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap }; top.Children.Add(summary);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var accept = new Button { Content = label("Smart_TextureImport"), IsEnabled = false, Style = (Style)FindResource("SmartPrimary") };
        var cancel = new Button { Content = label("Smart_Cancel"), IsCancel = true }; bottom.Children.Add(cancel); bottom.Children.Add(accept);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(details);
        mapping.TextChanged += (_, _) => { Plan = null; accept.IsEnabled = false; };
        accept.Click += (_, _) => { if (Plan?.CanImport == true) DialogResult = true; };
        async Task Refresh()
        {
            refresh.IsEnabled = false; mapping.IsEnabled = false; accept.IsEnabled = false; Plan = null;
            try
            {
                var plan = await preview(mapping.Text);
                summary.Text = string.Format(label("Smart_TextureSummary"), plan.Items.Count, plan.Items.Sum(i => i.Bytes) / 1048576d, plan.SkippedFiles, plan.Items.Count(i => i.Identical), plan.Items.Count(i => i.Conflict != null));
                var lines = plan.Items.Select(i => i.SourcePath + Environment.NewLine + " → " + i.RelativePath +
                    (i.Conflict != null ? " [" + label("Smart_TextureConflict") + "] " + i.Conflict : i.Identical ? " [" + label("Smart_TextureIdentical") + "]" : "")).ToList();
                lines.Add(Environment.NewLine + label("Smart_TextureDependencies"));
                try
                {
                    var scene = await model();
                    if (scene == null || !scene.TextureDependenciesComplete) lines.Add(label("Smart_TextureUnknown"));
                    foreach (var path in scene?.TexturePaths ?? [])
                    {
                        var relative = path.Replace('\\', '/'); if (!relative.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) relative = "textures/" + relative;
                        var available = scene!.TextureDependencies.FirstOrDefault(d => d.Path == path)?.Available;
                        lines.Add((plan.Items.Any(i => i.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)) ? "✓ " :
                            available == true ? "✓ " + label("Smart_TextureExisting") + " " : available == false ? "! " + label("Smart_TextureMissing") + " " : "? ") + relative);
                    }
                    lines.Add(label("Smart_TextureDependencyHint"));
                }
                catch (Exception ex) { lines.Add(label("Smart_TextureUnknown") + " " + ex.Message); }
                details.Text = string.Join(Environment.NewLine, lines); Plan = plan; accept.IsEnabled = plan.CanImport;
            }
            catch (Exception ex) { details.Text = ex.Message; }
            finally { refresh.IsEnabled = true; mapping.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await Refresh(); Loaded += async (_, _) => await Refresh();
    }
    private static string NaturalMapping(string source)
    {
        for (var current = Path.TrimEndingDirectorySeparator(source); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Path.GetFileName(current).Equals("textures", StringComparison.OrdinalIgnoreCase))
            { var relative = Path.GetRelativePath(current, source); return relative == "." ? "" : relative.Replace('\\', '/'); }
        return "";
    }
}
