using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views;

public sealed class WeaponFolderWindow : Window
{
    public sealed class ModelChoice(WeaponFolderSource source) : INotifyPropertyChanged
    {
        public WeaponFolderSource Source { get; } = source;
        public string Name => Source.RelativePath + " [" + Source.Format + "]";
        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public WeaponFolderPlan? Plan { get; private set; }
    public bool CreateNew { get; private set; }
    public WeaponKind Kind { get; private set; }
    public bool TextureOnly { get; private set; }
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, string> _mappings = new(StringComparer.OrdinalIgnoreCase);
    private bool _closed;

    public WeaponFolderWindow(string sourceFolder, string projectPath, Func<string, CancellationToken, Task<WeaponFolderScan>> scan,
        Func<WeaponFolderRequest, CancellationToken, Task<WeaponFolderPlan>> preview, IEnumerable<SmartModTypeRow> types,
        Func<string, string> label, bool createNew, WeaponKind initialKind, string? newProjectPluginName = null, string? sourceDisplayName = null)
    {
        Title = label(sourceDisplayName == null ? "Smart_FolderTitle" : "Smart_StartZip"); Width = 980; Height = 820; MinWidth = 600; MinHeight = 480; FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(20), Background = Background }; Content = root;
        var title = new TextBlock { Text = Title, FontSize = 24 }; DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var cancel = new Button { Content = label("Smart_Cancel"), IsCancel = true }; actions.Children.Add(cancel);
        var texturesOnly = new Button { Content = label("Smart_FolderTexturesOnly"), Visibility = Visibility.Collapsed }; actions.Children.Add(texturesOnly);
        var accept = new Button { Content = label("Smart_FolderConfirm"), IsEnabled = false, Style = (Style)FindResource("SmartPrimary") }; actions.Children.Add(accept);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        void Note(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
        Note(sourceDisplayName ?? sourceFolder); Note(label("Smart_FolderHint"));
        var progress = new TextBlock { Text = label("Smart_FolderScanning"), TextWrapping = TextWrapping.Wrap }; body.Children.Add(progress);
        var configuration = new StackPanel { IsEnabled = false }; body.Children.Add(configuration);
        var newItem = new CheckBox { Content = label("Smart_FolderNewItem"), IsChecked = createNew }; configuration.Children.Add(newItem);
        if (newProjectPluginName != null) newItem.IsEnabled = false;
        var kind = new ComboBox { ItemsSource = types.ToArray(), DisplayMemberPath = "Name", SelectedValuePath = "Kind", SelectedValue = initialKind }; configuration.Children.Add(kind);
        void Caption(string key) => configuration.Children.Add(new TextBlock { Text = label(key), TextWrapping = TextWrapping.Wrap });
        var modelTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><DockPanel><Image Source="{Binding Thumbnail}" Width="44" Height="44" Margin="0,0,10,0"/><TextBlock Text="{Binding Name}" TextWrapping="Wrap" VerticalAlignment="Center"/></DockPanel></DataTemplate>
            """);
        Caption("Models_Third"); var third = new ComboBox { ItemTemplate = modelTemplate }; configuration.Children.Add(third);
        var share = new CheckBox { Content = new TextBlock { Text = label("Smart_SharedModel"), TextWrapping = TextWrapping.Wrap } }; configuration.Children.Add(share);
        Caption("Models_First"); var first = new ComboBox { ItemTemplate = modelTemplate }; configuration.Children.Add(first);
        Caption("Smart_FolderFirstHint"); Caption("Smart_FolderTextureRoot"); var mapping = new TextBox(); configuration.Children.Add(mapping);
        var unused = new CheckBox { Content = label("Smart_FolderUnused") }; configuration.Children.Add(unused);
        var incomplete = new CheckBox { Content = new TextBlock { Text = label("Smart_FolderIncomplete"), TextWrapping = TextWrapping.Wrap } }; configuration.Children.Add(incomplete);
        var refresh = new Button { Content = label("Smart_TexturePreview") }; configuration.Children.Add(refresh);
        var assignments = new StackPanel(); configuration.Children.Add(assignments);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(details);
        WeaponFolderScan? inventory = null; var planId = Guid.NewGuid(); var loading = false;
        void Invalidate() { Plan = null; accept.IsEnabled = false; }
        void ModelChanged()
        { _mappings.Clear(); assignments.Children.Clear(); first.IsEnabled = share.IsChecked != true; Invalidate(); }
        async Task Thumbnail(ModelChoice? choice)
        {
            if (choice == null || choice.Thumbnail != null || _closed) return;
            try
            {
                var scene = await Task.Run(async () =>
                {
                    PackingPathGuard.EnsureNoLinks(choice.Source.SourcePath); using var input = File.OpenRead(choice.Source.SourcePath);
                    if (input.Length > SseNifReader.MaxFileBytes) throw new InvalidDataException("NIF preview size limit.");
                    var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, _cancellation.Token); return SseNifReader.Read(bytes, _cancellation.Token);
                }, _cancellation.Token);
                if (!_closed) choice.Thumbnail = ModelThumbnailService.Render(scene);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { if (!_closed) progress.Text = label("Smart_FolderThumbnailLimit") + " " + ex.Message; }
            catch (OperationCanceledException) { }
        }
        async Task Refresh()
        {
            if (inventory == null || third.SelectedItem is not ModelChoice model || loading) return;
            loading = true; configuration.IsEnabled = false; Invalidate(); progress.Text = label("Smart_FolderBuildingPlan");
            try
            {
                var request = new WeaponFolderRequest(planId, projectPath, inventory.Root, model.Source.SourcePath, (first.SelectedItem as ModelChoice)?.Source.SourcePath,
                    share.IsChecked == true, mapping.Text, unused.IsChecked == true, incomplete.IsChecked == true)
                { TextureMappings = _mappings.Select(p => new WeaponFolderMapping(p.Value, p.Key)).ToArray(), NewProjectPluginName = newProjectPluginName };
                var plan = await preview(request, _cancellation.Token); if (_closed) return;
                assignments.Children.Clear();
                foreach (var dependency in plan.Dependencies.Where(d => d.Status is "Missing" or "MappingRequired" or "Unknown" || _mappings.ContainsKey(d.Path)))
                {
                    assignments.Children.Add(new TextBlock { Text = dependency.Path + " — " + label("Smart_FolderDependency_" + dependency.Status), TextWrapping = TextWrapping.Wrap });
                    var choose = new ComboBox { ItemsSource = inventory.Files.Where(f => f.SourcePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToArray(), DisplayMemberPath = "RelativePath", SelectedValuePath = "SourcePath" };
                    if (_mappings.TryGetValue(dependency.Path, out var selected)) choose.SelectedValue = selected;
                    choose.SelectionChanged += (_, _) => { if (choose.SelectedValue is string file) _mappings[dependency.Path] = file; else _mappings.Remove(dependency.Path); Invalidate(); };
                    assignments.Children.Add(choose);
                }
                details.Text = string.Join(Environment.NewLine, new[] { label("Models_Third") + ": " + plan.ThirdPersonPath, label("Models_First") + ": " + (plan.FirstPersonPath ?? label("Smart_FolderKeepFirst")),
                    string.Format(label("Smart_FolderPlanSummary"), plan.Files.Count, plan.Files.Sum(f => f.Bytes) / 1048576d, plan.SkippedFiles) }
                    .Concat(plan.Files.Select(f => f.SourcePath + Environment.NewLine + " → " + f.RelativePath + (f.Conflict == null ? f.Identical ? " [" + label("Smart_TextureIdentical") + "]" : "" : " [" + label("Smart_TextureConflict") + "] " + f.Conflict)))
                    .Concat(plan.Dependencies.Select(d => label("Smart_FolderDependency_" + d.Status) + ": " + d.Path)).Concat(plan.Warnings));
                Plan = plan; accept.IsEnabled = plan.CanImport; progress.Text = plan.Incomplete ? label("Smart_FolderDependenciesPending") : label("Smart_FolderPlanReady");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) details.Text = ex.Message; }
            finally { loading = false; if (!_closed) configuration.IsEnabled = true; }
        }
        third.SelectionChanged += async (_, _) => { ModelChanged(); await Thumbnail(third.SelectedItem as ModelChoice); };
        first.SelectionChanged += async (_, _) => { ModelChanged(); await Thumbnail(first.SelectedItem as ModelChoice); };
        share.Checked += (_, _) => ModelChanged(); share.Unchecked += (_, _) => ModelChanged();
        mapping.TextChanged += (_, _) => Invalidate();
        foreach (var box in new[] { unused, incomplete }) { box.Checked += (_, _) => Invalidate(); box.Unchecked += (_, _) => Invalidate(); }
        newItem.Checked += (_, _) => kind.IsEnabled = true; newItem.Unchecked += (_, _) => kind.IsEnabled = false;
        refresh.Click += async (_, _) => await Refresh();
        accept.Click += (_, _) => { if (Plan?.CanImport == true && (newItem.IsChecked != true || kind.SelectedItem is SmartModTypeRow)) { CreateNew = newItem.IsChecked == true; Kind = (kind.SelectedItem as SmartModTypeRow)?.Kind ?? initialKind; DialogResult = true; } };
        texturesOnly.Click += (_, _) => { TextureOnly = true; DialogResult = true; };
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); _cancellation.Dispose(); };
        Loaded += async (_, _) =>
        {
            try
            {
                inventory = await scan(sourceFolder, _cancellation.Token); if (_closed) return;
                var models = inventory.Files.Where(f => f.SourcePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).Select(f => new ModelChoice(f)).ToArray();
                third.ItemsSource = models; first.ItemsSource = models;
                details.Text = string.Join(Environment.NewLine, inventory.Files.Select(f => f.RelativePath + " [" + f.Format + "]" + (f.Error == null ? "" : " — " + f.Error)));
                if (models.Length == 0) { progress.Text = label("Smart_FolderNoNif"); texturesOnly.Visibility = Visibility.Visible; return; }
                configuration.IsEnabled = true; kind.IsEnabled = newItem.IsChecked == true; share.IsChecked = models.Length == 1; third.SelectedItem = models.Length == 1 ? models[0] : null;
                progress.Text = label("Smart_FolderChooseModels");
                if (models.Length == 1) await Refresh();
                foreach (var choice in models.Take(8)) { if (_closed) break; await Thumbnail(choice); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) progress.Text = ex.Message; }
        };
    }
}
