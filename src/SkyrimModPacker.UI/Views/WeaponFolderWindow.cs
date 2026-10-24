using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
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
    public sealed class ImportItemRow(Func<WeaponKind, string> typeName) : INotifyPropertyChanged
    {
        public Guid Id { get; } = Guid.NewGuid();
        private string _name = "";
        private WeaponKind _kind;
        private ModelChoice? _thirdModel, _firstModel;
        private bool _shareModel = true;
        public string Name { get => _name; set => Set(ref _name, value); }
        public WeaponKind Kind { get => _kind; set => Set(ref _kind, value); }
        public ModelChoice? ThirdModel { get => _thirdModel; set => Set(ref _thirdModel, value); }
        public ModelChoice? FirstModel { get => _firstModel; set => Set(ref _firstModel, value); }
        public bool ShareModel { get => _shareModel; set => Set(ref _shareModel, value); }
        public string Summary => Name + " · " + typeName(Kind) + Environment.NewLine + (ThirdModel?.Source.RelativePath ?? "—");
        public WeaponFolderItemRequest Capture() => new(Id, Name.Trim(), Kind, ThirdModel?.Source.SourcePath ?? "", FirstModel?.Source.SourcePath, ShareModel);
        private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
        { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(property)); PropertyChanged?.Invoke(this, new(nameof(Summary))); }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public WeaponFolderPlan? Plan { get; private set; }
    public bool CreateNew { get; private set; }
    public WeaponKind Kind { get; private set; }
    public bool TextureOnly { get; private set; }
    public ObservableCollection<ImportItemRow> ItemRows { get; } = [];
    public IReadOnlyList<ModelChoice> Models { get; private set; } = [];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, string> _mappings = new(StringComparer.OrdinalIgnoreCase);
    private bool _closed;

    public WeaponFolderWindow(string sourceFolder, string projectPath, Func<string, CancellationToken, Task<WeaponFolderScan>> scan,
        Func<WeaponFolderRequest, CancellationToken, Task<WeaponFolderPlan>> preview, IEnumerable<SmartModTypeRow> types,
        Func<string, string> label, bool createNew, WeaponKind initialKind, string? newProjectPluginName = null, string? sourceDisplayName = null, string? targetPluginName = null)
    {
        var token = _cancellation.Token; var typeChoices = types.ToArray(); var starting = newProjectPluginName != null;
        string TypeName(WeaponKind value) => typeChoices.FirstOrDefault(t => t.Kind == value)?.Name ?? value.ToString();
        Title = label(sourceDisplayName == null ? "Smart_FolderTitle" : "Smart_StartZip"); Width = 1040; Height = 900; MinWidth = 600; MinHeight = 540; FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(20), Background = Background }; Content = root;
        var title = new TextBlock { Text = Title, FontSize = 24, TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        actions.Children.Add(new Button { Content = label("Smart_Cancel"), IsCancel = true });
        var texturesOnly = new Button { Content = label("Smart_FolderTexturesOnly"), Visibility = Visibility.Collapsed }; actions.Children.Add(texturesOnly);
        var accept = new Button { Name = "ImportConfirm", Content = label("Smart_FolderConfirm"), IsEnabled = false, Style = (Style)FindResource("SmartPrimary") }; actions.Children.Add(accept);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        void Note(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
        Note(body, sourceDisplayName ?? sourceFolder); Note(body, label("Smart_FolderQueueHint"));
        var progress = new TextBlock { Name = "ImportProgress", Text = label("Smart_FolderScanning"), TextWrapping = TextWrapping.Wrap }; body.Children.Add(progress);
        var configuration = new StackPanel { IsEnabled = false }; body.Children.Add(configuration);
        var nameBox = new TextBox { Name = "ImportProjectName", Text = starting ? Path.GetFileNameWithoutExtension(newProjectPluginName) ?? "" : "" };
        var initialDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var parentBox = new TextBox { Name = "ImportParentFolder", Text = Directory.GetParent(initialDirectory)?.FullName ?? "" };
        var destinationInfo = new TextBlock { Name = "ImportDestinationInfo", TextWrapping = TextWrapping.Wrap };
        var destinationPanel = new StackPanel { Visibility = starting ? Visibility.Visible : Visibility.Collapsed };
        configuration.Children.Add(new Border { Child = destinationPanel, BorderBrush = new SolidColorBrush(Color.FromRgb(52, 70, 94)), BorderThickness = new Thickness(1), Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Visibility = destinationPanel.Visibility });
        Note(destinationPanel, label("Smart_FolderProjectName")); destinationPanel.Children.Add(nameBox);
        Note(destinationPanel, label("Smart_Workspace")); var parentLine = new DockPanel(); destinationPanel.Children.Add(parentLine);
        var browse = new Button { Content = "…" }; DockPanel.SetDock(browse, Dock.Right); parentLine.Children.Add(browse); parentLine.Children.Add(parentBox);
        var suggest = new Button { Name = "ImportSuggestName", Content = label("Smart_FolderAvailableName") }; destinationPanel.Children.Add(suggest); destinationPanel.Children.Add(destinationInfo);
        var newItem = new CheckBox { Name = "ImportNewItems", Content = label("Smart_FolderNewItem"), IsChecked = starting || createNew, IsEnabled = !starting }; configuration.Children.Add(newItem);
        var queuePanel = new StackPanel(); configuration.Children.Add(queuePanel); Note(queuePanel, label("Smart_FolderQueueTitle"));
        var queue = new ListBox { Name = "ImportItemQueue", ItemsSource = ItemRows, MinHeight = 80, MaxHeight = 190 };
        queue.ItemTemplate = (DataTemplate)XamlReader.Parse("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding Summary}" TextWrapping="Wrap"/></DataTemplate>""");
        queuePanel.Children.Add(queue);
        var queueActions = new WrapPanel(); queuePanel.Children.Add(queueActions);
        var add = new Button { Name = "ImportAddItem", Content = label("Smart_FolderAddItem") }; var copy = new Button { Name = "ImportCopyItem", Content = label("Smart_FolderCopyItem") }; var remove = new Button { Name = "ImportRemoveItem", Content = label("Smart_FolderRemoveItem") };
        queueActions.Children.Add(add); queueActions.Children.Add(copy); queueActions.Children.Add(remove);
        var editor = new StackPanel(); configuration.Children.Add(editor);
        var itemNameLabel = new TextBlock { Text = label("Weapons_Name") }; editor.Children.Add(itemNameLabel);
        var itemName = new TextBox { Name = "ImportItemName" }; itemName.SetBinding(TextBox.TextProperty, new Binding(nameof(ImportItemRow.Name)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); editor.Children.Add(itemName);
        Note(editor, label("Smart_FolderItemKind")); var kind = new ComboBox { Name = "ImportItemKind", ItemsSource = typeChoices, DisplayMemberPath = "Name", SelectedValuePath = "Kind" };
        kind.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(ImportItemRow.Kind)) { Mode = BindingMode.TwoWay }); editor.Children.Add(kind);
        var modelTemplate = (DataTemplate)XamlReader.Parse("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><DockPanel><Image Source="{Binding Thumbnail}" Width="44" Height="44" Margin="0,0,10,0"/><TextBlock Text="{Binding Name}" TextWrapping="Wrap" VerticalAlignment="Center"/></DockPanel></DataTemplate>""");
        Note(editor, label("Models_Third")); var third = new ComboBox { Name = "ImportThirdModel", ItemTemplate = modelTemplate }; third.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(ImportItemRow.ThirdModel)) { Mode = BindingMode.TwoWay }); editor.Children.Add(third);
        var share = new CheckBox { Name = "ImportSharedModel", Content = new TextBlock { Text = label("Smart_SharedModel"), TextWrapping = TextWrapping.Wrap } }; share.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(ImportItemRow.ShareModel)) { Mode = BindingMode.TwoWay }); editor.Children.Add(share);
        Note(editor, label("Models_First")); var first = new ComboBox { Name = "ImportFirstModel", ItemTemplate = modelTemplate }; first.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(ImportItemRow.FirstModel)) { Mode = BindingMode.TwoWay }); editor.Children.Add(first);
        var firstHint = new TextBlock { Text = label("Smart_FolderQueueFirstHint"), TextWrapping = TextWrapping.Wrap }; editor.Children.Add(firstHint);
        Note(configuration, label("Smart_FolderTextureRoot")); var mapping = new TextBox(); configuration.Children.Add(mapping);
        var unused = new CheckBox { Content = label("Smart_FolderUnused") }; configuration.Children.Add(unused);
        var incomplete = new CheckBox { Content = new TextBlock { Text = label("Smart_FolderIncomplete"), TextWrapping = TextWrapping.Wrap } }; configuration.Children.Add(incomplete);
        var refresh = new Button { Name = "ImportPreview", Content = label("Smart_FolderPreviewAll") }; configuration.Children.Add(refresh);
        var assignments = new StackPanel(); configuration.Children.Add(assignments);
        var details = new TextBox { Name = "ImportDetails", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(details);
        WeaponFolderScan? inventory = null; var planId = Guid.NewGuid(); var loading = false;
        void Invalidate() { Plan = null; accept.IsEnabled = false; }
        void DestinationChanged()
        {
            Invalidate(); if (!starting) return;
            try
            {
                var path = WeaponImportDestination.ProjectPath(parentBox.Text, nameBox.Text);
                var directory = Path.GetDirectoryName(path)!;
                destinationInfo.Text = (Directory.Exists(directory) || File.Exists(directory) ? label("Smart_FolderDestinationExists") : label("Smart_FolderDestinationReady")) + Environment.NewLine + path;
            }
            catch (Exception ex) { destinationInfo.Text = ex.Message; }
        }
        void ModeChanged()
        { queuePanel.Visibility = itemName.Visibility = itemNameLabel.Visibility = newItem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; kind.IsEnabled = newItem.IsChecked == true; firstHint.Text = label(newItem.IsChecked == true ? "Smart_FolderQueueFirstHint" : "Smart_FolderFirstHint"); Invalidate(); }
        async Task Thumbnail(ModelChoice? choice)
        {
            if (choice == null || choice.Thumbnail != null || _closed) return;
            try
            {
                var scene = await Task.Run(async () => { PackingPathGuard.EnsureNoLinks(choice.Source.SourcePath); byte[] bytes; using (var input = new FileStream(choice.Source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { if (input.Length > SseNifReader.MaxFileBytes) throw new InvalidDataException("NIF preview size limit."); bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token); } return SseNifReader.Read(bytes, token); }, token);
                if (!_closed) choice.Thumbnail = ModelThumbnailService.Render(scene);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) details.Text += Environment.NewLine + label("Smart_FolderThumbnailLimit") + " " + ex.Message; }
        }
        ImportItemRow AddRow(ImportItemRow? original = null)
        {
            if (ItemRows.Count >= 100) throw new IOException(label("Smart_FolderQueueLimit"));
            var row = new ImportItemRow(TypeName) { Name = original == null ? label("Smart_FolderItemDefault") + " " + (ItemRows.Count + 1) : original.Name + " " + (ItemRows.Count + 1),
                Kind = original?.Kind ?? initialKind, ThirdModel = original?.ThirdModel ?? (Models.Count == 1 ? Models[0] : null), FirstModel = original?.FirstModel, ShareModel = original?.ShareModel ?? true };
            row.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(ImportItemRow.Summary)) return;
                Invalidate(); first.IsEnabled = (queue.SelectedItem as ImportItemRow)?.ShareModel != true;
                if (args.PropertyName is nameof(ImportItemRow.ThirdModel) or nameof(ImportItemRow.FirstModel) or nameof(ImportItemRow.ShareModel)) { _mappings.Clear(); assignments.Children.Clear(); }
                if (args.PropertyName == nameof(ImportItemRow.ThirdModel)) await Thumbnail(row.ThirdModel);
                if (args.PropertyName == nameof(ImportItemRow.FirstModel)) await Thumbnail(row.FirstModel);
            };
            ItemRows.Add(row); queue.SelectedItem = row; Invalidate(); return row;
        }
        async Task Refresh()
        {
            if (inventory == null || loading) return;
            loading = true; configuration.IsEnabled = false; Invalidate(); progress.Text = label("Smart_FolderBuildingPlan");
            try
            {
                var rows = newItem.IsChecked == true ? ItemRows.ToArray() : new[] { queue.SelectedItem as ImportItemRow ?? ItemRows.FirstOrDefault() };
                if (rows.Length == 0 || rows.Any(r => r == null || r.ThirdModel == null || !typeChoices.Any(t => t.Kind == r.Kind) || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 100 || r.Name.Any(char.IsControl) ||
                    newItem.IsChecked == true && !r.ShareModel && r.FirstModel == null)) throw new IOException(label("Smart_FolderQueueInvalid"));
                var selected = rows[0]!; var destination = starting ? WeaponImportDestination.ProjectPath(parentBox.Text, nameBox.Text) : projectPath;
                var request = new WeaponFolderRequest(planId, destination, inventory.Root, selected.ThirdModel!.Source.SourcePath, selected.FirstModel?.Source.SourcePath,
                    selected.ShareModel, mapping.Text, unused.IsChecked == true, incomplete.IsChecked == true)
                { TextureMappings = _mappings.Select(p => new WeaponFolderMapping(p.Value, p.Key)).ToArray(), NewProjectPluginName = starting ? (nameBox.Text ?? "").Trim() + ".esp" : null,
                    Items = newItem.IsChecked == true ? rows.Select(r => r!.Capture()).ToArray() : [] };
                var plan = await preview(request, token); if (_closed) return;
                assignments.Children.Clear();
                foreach (var dependency in plan.Dependencies.Where(d => d.Status is "Missing" or "MappingRequired" or "Unknown" || _mappings.ContainsKey(d.Path)))
                {
                    Note(assignments, dependency.Path + " — " + label("Smart_FolderDependency_" + dependency.Status));
                    var choose = new ComboBox { ItemsSource = inventory.Files.Where(f => f.SourcePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToArray(), DisplayMemberPath = "RelativePath", SelectedValuePath = "SourcePath" };
                    if (_mappings.TryGetValue(dependency.Path, out var value)) choose.SelectedValue = value;
                    choose.SelectionChanged += (_, _) => { if (choose.SelectedValue is string file) _mappings[dependency.Path] = file; else _mappings.Remove(dependency.Path); Invalidate(); }; assignments.Children.Add(choose);
                }
                details.Text = string.Join(Environment.NewLine, new[] { string.Format(label("Smart_FolderQueueSummary"), plan.Items.Count == 0 ? 1 : plan.Items.Count), label("Smart_FolderPlugin") + ": " + (starting ? request.NewProjectPluginName : targetPluginName ?? label("Smart_FolderCurrentPlugin")),
                    string.Format(label("Smart_FolderPlanSummary"), plan.Files.Count, plan.Files.Sum(f => f.Bytes) / 1048576d, plan.SkippedFiles) }
                    .Concat(plan.Items.Select(i => i.Name + " · " + TypeName(i.Kind) + Environment.NewLine + label("Models_Third") + ": " + i.ThirdPersonPath + Environment.NewLine + label("Models_First") + ": " + i.FirstPersonPath))
                    .Concat(plan.Files.Select(f => f.RelativePath + (f.Conflict == null ? f.Identical ? " [" + label("Smart_TextureIdentical") + "]" : "" : " [" + label("Smart_TextureConflict") + "] " + f.Conflict)))
                    .Concat(plan.Dependencies.Select(d => label("Smart_FolderDependency_" + d.Status) + ": " + d.Path)).Concat(plan.Warnings));
                Plan = plan; accept.IsEnabled = plan.CanImport; progress.Text = plan.Incomplete ? label("Smart_FolderDependenciesPending") : label("Smart_FolderPlanReady");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) { progress.Text = label("Smart_FolderPlanFailed"); details.Text = ex.Message; } }
            finally { loading = false; if (!_closed) configuration.IsEnabled = true; }
        }
        queue.SelectionChanged += (_, _) => { editor.DataContext = queue.SelectedItem; first.IsEnabled = (queue.SelectedItem as ImportItemRow)?.ShareModel != true; };
        add.Click += (_, _) => { try { AddRow(); } catch (Exception ex) { details.Text = ex.Message; } };
        copy.Click += (_, _) => { try { if (queue.SelectedItem is ImportItemRow row) AddRow(row); } catch (Exception ex) { details.Text = ex.Message; } };
        remove.Click += (_, _) => { if (queue.SelectedItem is ImportItemRow row) ItemRows.Remove(row); queue.SelectedItem = ItemRows.FirstOrDefault(); Invalidate(); };
        mapping.TextChanged += (_, _) => Invalidate(); nameBox.TextChanged += (_, _) => DestinationChanged(); parentBox.TextChanged += (_, _) => DestinationChanged();
        browse.Click += (_, _) => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) parentBox.Text = dialog.FolderName; };
        suggest.Click += (_, _) => { try { nameBox.Text = WeaponImportDestination.AvailableName(parentBox.Text, nameBox.Text); DestinationChanged(); } catch (Exception ex) { destinationInfo.Text = ex.Message; } };
        foreach (var box in new[] { unused, incomplete }) { box.Checked += (_, _) => Invalidate(); box.Unchecked += (_, _) => Invalidate(); }
        newItem.Checked += (_, _) => ModeChanged(); newItem.Unchecked += (_, _) => ModeChanged();
        refresh.Click += async (_, _) => await Refresh();
        accept.Click += (_, _) => { if (Plan?.CanImport == true) { CreateNew = newItem.IsChecked == true; Kind = ItemRows.FirstOrDefault()?.Kind ?? initialKind; DialogResult = true; } };
        texturesOnly.Click += (_, _) => { TextureOnly = true; DialogResult = true; };
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); _cancellation.Dispose(); };
        if (starting && (Directory.Exists(initialDirectory) || File.Exists(initialDirectory)))
        {
            try
            {
                var preferred = sourceDisplayName == null ? nameBox.Text : Path.GetFileNameWithoutExtension(sourceDisplayName);
                nameBox.Text = WeaponImportDestination.AvailableName(parentBox.Text, preferred);
                Note(destinationPanel, label("Smart_FolderDestinationSuggested"));
            }
            catch (Exception ex) { destinationInfo.Text = ex.Message; }
        }
        DestinationChanged(); ModeChanged();
        Loaded += async (_, _) =>
        {
            try
            {
                inventory = await scan(sourceFolder, token); if (_closed) return;
                Models = inventory.Files.Where(f => f.SourcePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).Select(f => new ModelChoice(f)).ToArray();
                third.ItemsSource = Models; first.ItemsSource = Models;
                details.Text = string.Join(Environment.NewLine, inventory.Files.Select(f => f.RelativePath + " [" + f.Format + "]" + (f.Error == null ? "" : " — " + f.Error)));
                if (Models.Count == 0) { progress.Text = label("Smart_FolderNoNif"); texturesOnly.Visibility = Visibility.Visible; return; }
                configuration.IsEnabled = true; if (ItemRows.Count == 0) AddRow(); ModeChanged(); progress.Text = label("Smart_FolderChooseModels");
                if (Models.Count == 1) await Refresh();
                foreach (var choice in Models.Take(8)) { if (_closed) break; await Thumbnail(choice); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) progress.Text = label("Smart_FolderPlanFailed") + " " + ex.Message; }
        };
    }
}
