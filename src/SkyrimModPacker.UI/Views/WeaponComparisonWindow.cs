using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views;

/// <summary>Read-only comparison until a concrete group selection is reviewed and applied.</summary>
public sealed class WeaponComparisonWindow : Window
{
    public ModelPreviewControl MinePreview { get; }
    public ModelPreviewControl SamplePreview { get; }
    public WeaponComparisonSnapshot? Sample { get; private set; }
    private readonly SmartModViewModel _vm;
    private readonly SmartModItemRow _item;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loading;
    private readonly DispatcherTimer _leftTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _closed;
    private int _generation;
    private SmartTuningPreview? _pending;

    public WeaponComparisonWindow(SmartModViewModel vm, IModelPreviewService service)
    {
        _vm = vm; _item = vm.SelectedItem ?? throw new InvalidOperationException(vm.Label("Smart_SelectTemplate"));
        string L(string key) => vm.Label(key);
        Title = L("Smart_CompareTitle"); Width = 1240; Height = 920; MinWidth = 600; MinHeight = 540; FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });
        MinePreview = new(service); SamplePreview = new(service); MinePreview.SetCompact(true); SamplePreview.SetCompact(true);
        var root = new DockPanel { Margin = new Thickness(18), Background = Background }; Content = root;
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Button Action(Panel panel, string key, string name = "") { var b = new Button { Content = L(key), Name = name }; panel.Children.Add(b); return b; }
        var undo = Action(footer, "Smart_TuningUndo", "CompareUndo"); var review = Action(footer, "Smart_CompareReview", "CompareReview");
        var apply = Action(footer, "Smart_CompareApply", "CompareApply"); apply.IsEnabled = false;
        Action(footer, "Smart_Close").Click += (_, _) => Close();
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        TextBlock Note(Panel panel, string text) { var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7) }; panel.Children.Add(t); return t; }
        Note(body, Title).FontSize = 24; Note(body, L("Smart_CompareHelp"));
        var selector = new Expander { Header = L("Smart_CompareSearch"), IsExpanded = true }; var selectionPanel = new StackPanel(); selector.Content = selectionPanel; body.Children.Add(selector);
        var search = new TextBox { Name = "CompareSearch" }; selectionPanel.Children.Add(search);
        var filters = new ResponsiveColumnsPanel { MinColumnWidth = 260, Gap = 10 }; selectionPanel.Children.Add(filters);
        var allSources = L("Smart_CompareAllSources"); var sources = new ComboBox { Name = "CompareSources", ItemsSource = vm.ComparisonChoices.Select(c => c.Source).Distinct().Prepend(allSources).ToArray(), SelectedIndex = 0 }; filters.Children.Add(sources);
        var typeChoices = new[] { new SmartModTypeRow(_item.Kind, L("Smart_CompareSameType")), new SmartModTypeRow(_item.Kind, L("Smart_CompareAllTypes")) }.Concat(vm.Types).ToArray();
        var type = new ComboBox { Name = "CompareTypes", ItemsSource = typeChoices, DisplayMemberPath = "Name", SelectedIndex = 0 }; filters.Children.Add(type);
        var results = new ListBox { Name = "CompareResults", MinHeight = 45, MaxHeight = 130, DisplayMemberPath = nameof(SmartComparisonChoice.DisplayName) }; selectionPanel.Children.Add(results);
        VirtualizingPanel.SetIsVirtualizing(results, true); VirtualizingPanel.SetVirtualizationMode(results, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(results, true);
        var resultCount = Note(selectionPanel, ""); var status = Note(body, ""); status.Foreground = Brushes.LightGoldenrodYellow;
        var sections = new TabControl { Name = "CompareTabs" }; body.Children.Add(sections);
        TabItem Tab(string key, object content) { var tab = new TabItem { Header = L(key), Content = content }; sections.Items.Add(tab); return tab; }
        var appearance = new StackPanel(); Tab("Smart_CompareAppearance", appearance);
        var viewTools = new WrapPanel(); appearance.Children.Add(viewTools);
        var sync = new CheckBox { Content = L("Smart_CompareSync"), IsChecked = true, Margin = new Thickness(0, 8, 18, 8) }; viewTools.Children.Add(sync);
        var scale = new CheckBox { Name = "CompareSameScale", Content = L("Smart_CompareScale"), Margin = new Thickness(0, 8, 18, 8) }; viewTools.Children.Add(scale);
        var light = new Slider { Minimum = .05, Maximum = 1, Value = .85, Width = 100, Margin = new Thickness(8) }; Note(viewTools, L("Preview_Light")); viewTools.Children.Add(light);
        var background = new ComboBox { ItemsSource = new[] { L("Smart_CompareDark"), L("Smart_CompareGray") }, SelectedIndex = 0, MinWidth = 130 }; viewTools.Children.Add(background);
        Note(appearance, L("Smart_CompareScaleHint"));
        var viewColumns = new ResponsiveColumnsPanel { MinColumnWidth = 400, Gap = 12 }; appearance.Children.Add(viewColumns);
        var minePanel = new StackPanel(); var samplePanel = new StackPanel(); viewColumns.Children.Add(minePanel); viewColumns.Children.Add(samplePanel);
        var mineTitle = Note(minePanel, ""); mineTitle.FontWeight = FontWeights.SemiBold; MinePreview.Height = 400; minePanel.Children.Add(MinePreview);
        var sampleTitle = Note(samplePanel, L("Smart_CompareChoose")); sampleTitle.FontWeight = FontWeights.SemiBold; SamplePreview.Height = 400; samplePanel.Children.Add(SamplePreview);
        var statistics = new StackPanel(); Tab("Smart_CompareStatistics", statistics); Note(statistics, L("Smart_CompareBaseValues"));
        var different = new CheckBox { Name = "CompareDifferences", Content = L("Smart_CompareDifferences") }; statistics.Children.Add(different);
        DataGrid Table(Panel panel)
        {
            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 130, MaxHeight = 420, Background = Background, Foreground = Foreground, RowBackground = Background,
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(23, 36, 52)), BorderBrush = new SolidColorBrush(Color.FromRgb(52, 70, 94)), GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
            var header = new Style(typeof(DataGridColumnHeader)); header.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(36, 66, 77)))); header.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7))); grid.ColumnHeaderStyle = header;
            var cellStyle = new Style(typeof(DataGridCell)); cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(40, 84, 84)))); cellStyle.Triggers.Add(selected); grid.CellStyle = cellStyle;
            foreach (var pair in new[] { ("Smart_CompareParameter", "Parameter"), ("Smart_CompareMine", "Mine"), ("Smart_CompareSample", "Sample"), ("Smart_CompareDifference", "Difference") })
            {
                var cell = new Style(typeof(TextBlock)); cell.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); cell.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5)));
                grid.Columns.Add(new DataGridTextColumn { Header = L(pair.Item1), Binding = new Binding(pair.Item2), Width = 110, ElementStyle = cell });
            }
            // Explicit widths keep columns usable inside a scrolling tab, where WPF may measure star columns at their minimum.
            grid.SizeChanged += (_, _) => { var available = Math.Max(320, grid.ActualWidth - 26); for (var i = 0; i < grid.Columns.Count; i++) grid.Columns[i].Width = available / 4; };
            panel.Children.Add(grid); return grid;
        }
        var stats = Table(statistics); stats.Name = "CompareStatistics";
        var leftRecipes = new ComboBox[2]; var rightRecipes = new ComboBox[2]; var recipeTables = new DataGrid[2]; var recipeNotes = new TextBlock[2];
        for (var i = 0; i < 2; i++)
        {
            var panel = new StackPanel(); Tab(i == 0 ? "Smart_Crafting" : "Smart_Tempering", panel);
            Note(panel, L("Smart_CompareMine")); leftRecipes[i] = new ComboBox { DisplayMemberPath = "Title" }; panel.Children.Add(leftRecipes[i]);
            Note(panel, L("Smart_CompareSample")); rightRecipes[i] = new ComboBox { DisplayMemberPath = "Title" }; panel.Children.Add(rightRecipes[i]);
            recipeNotes[i] = Note(panel, ""); recipeTables[i] = Table(panel);
        }
        Note(body, L("Smart_CompareCopyHint")); var groups = new WrapPanel(); body.Children.Add(groups);
        CheckBox Group(string key) { var box = new CheckBox { Content = L(key), Margin = new Thickness(0, 8, 18, 8) }; groups.Children.Add(box); return box; }
        var combat = Group("Smart_CompareCombat"); var economy = Group("Smart_CompareEconomy"); var material = Group("Smart_MaterialTitle");
        var crafting = Group("Smart_Crafting"); var tempering = Group("Smart_Tempering");
        var changes = new TextBox { Name = "CompareChanges", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(changes);
        void Invalidate() { _pending = null; apply.IsEnabled = false; changes.Text = ""; undo.IsEnabled = vm.CanUndoTuning; }
        string Ref(PluginRecordReference? r) => r == null ? "—" : vm.ComparisonRecordName(r) is { } name && name != r.ToString() ? name + " [" + r + "]" : r.ToString();
        WeaponComparisonValue TextRow(string label, string mine, string sample) => new(label, mine, sample, mine == sample ? "=" : "≠", mine == sample);
        string Materials(WeaponDraftFields fields) => string.Join(", ", fields.Keywords.Select(k => vm.Options.Keywords.FirstOrDefault(o => WeaponComparison.SameRecord(o.Reference, k))).Where(o => o != null && (o.EditorId.StartsWith("WeapMaterial", StringComparison.Ordinal) || o.EditorId.StartsWith("DLC1WeapMaterial", StringComparison.Ordinal))).Select(o => {
            var key = "Smart_Material" + o!.EditorId.Replace("DLC1WeapMaterial", "", StringComparison.Ordinal).Replace("WeapMaterial", "", StringComparison.Ordinal);
            var name = L(key); return (name == key ? o.DisplayName : name) + " [" + o.Reference + "]";
        }));
        void RefreshTables()
        {
            mineTitle.Text = L("Smart_CompareMine") + ": " + _item.Name + " [" + _item.EditorId + "] · " + vm.Name + " · " + L("WeaponType_" + _item.Kind);
            if (Sample == null) { stats.ItemsSource = null; foreach (var t in recipeTables) t.ItemsSource = null; return; }
            var mine = _item.Fields; var sample = Sample.Fields;
            var rows = new[] { WeaponComparison.Numeric(L("Weapons_Damage"), mine.Damage, sample.Damage), WeaponComparison.Numeric(L("Weapons_Weight"), mine.Weight, sample.Weight),
                WeaponComparison.Numeric(L("Weapons_Value"), mine.Value, sample.Value), WeaponComparison.Numeric(L("Weapons_Speed"), mine.Speed, sample.Speed), WeaponComparison.Numeric(L("Weapons_Reach"), mine.Reach, sample.Reach),
                WeaponComparison.Numeric(L("Weapons_Charge"), mine.EnchantmentCharge, sample.EnchantmentCharge),
                TextRow(L("Smart_MaterialTitle"), Materials(mine), Materials(sample)), TextRow(L("Weapons_Enchantment"), Ref(mine.Enchantment), Ref(sample.Enchantment)) };
            stats.ItemsSource = rows.Where(r => different.IsChecked != true || !r.Equal).ToArray();
            for (var i = 0; i < 2; i++)
            {
                var a = leftRecipes[i].SelectedItem as WeaponComparisonRecipe; var b = rightRecipes[i].SelectedItem as WeaponComparisonRecipe;
                recipeNotes[i].Text = (a == null || b == null ? L("Smart_CompareRecipeMissing") + Environment.NewLine : "") + (a?.CanCopy == false || b?.CanCopy == false ? L("Smart_CompareRecipeBlocked") : "");
                recipeNotes[i].ToolTip = string.Join(Environment.NewLine, new[] { a?.CopyBlock, b?.CopyBlock }.Where(s => !string.IsNullOrEmpty(s)));
                recipeTables[i].ItemsSource = new[] { TextRow(L("Smart_Workbench"), Ref(a?.Data.Workbench), Ref(b?.Data.Workbench)),
                    WeaponComparison.Numeric(L("Smart_RecipeOutputShort"), a?.Data.OutputCount, b?.Data.OutputCount),
                    TextRow(L("Smart_RequiredPerk"), Ref(a?.Data.RequiredPerk), Ref(b?.Data.RequiredPerk)), TextRow(L("Smart_CompareTemperingGuard"), Ref(a?.Data.TemperingPerk), Ref(b?.Data.TemperingPerk)) }
                    .Concat(WeaponComparison.Ingredients(a?.Data, b?.Data, vm.ComparisonRecordName)).ToArray();
            }
        }
        void RefreshLeftRecipes()
        {
            for (var i = 0; i < 2; i++) { var old = (leftRecipes[i].SelectedItem as WeaponComparisonRecipe)?.Data.Id; leftRecipes[i].ItemsSource = _item.Recipes.Where(r => r.IsTempering == (i == 1)).Select(r => new WeaponComparisonRecipe(r.EditorId, r.Capture())).ToArray(); leftRecipes[i].SelectedItem = ((IEnumerable<WeaponComparisonRecipe>)leftRecipes[i].ItemsSource).FirstOrDefault(r => r.Data.Id == old); if (leftRecipes[i].SelectedIndex < 0) leftRecipes[i].SelectedIndex = 0; }
        }
        void Normalize() { var extent = scale.IsChecked == true ? Math.Max(MinePreview.OriginalExtent, SamplePreview.OriginalExtent) : (double?)null; MinePreview.SetNormalizationExtent(extent); SamplePreview.SetNormalizationExtent(extent); }
        MinePreview.SceneChanged += Normalize; SamplePreview.SceneChanged += Normalize;
        MinePreview.ViewChanged += view => { if (sync.IsChecked == true) SamplePreview.SetView(view); };
        SamplePreview.ViewChanged += view => { if (sync.IsChecked == true) MinePreview.SetView(view); };
        sync.Checked += (_, _) => SamplePreview.SetView(MinePreview.ViewState);
        scale.Checked += (_, _) => Normalize(); scale.Unchecked += (_, _) => Normalize();
        void UpdateEnvironment() { var color = background.SelectedIndex == 0 ? Color.FromRgb(14, 22, 35) : Color.FromRgb(90, 95, 104); MinePreview.SetEnvironment(light.Value, color); SamplePreview.SetEnvironment(light.Value, color); }
        light.ValueChanged += (_, _) => UpdateEnvironment(); background.SelectionChanged += (_, _) => UpdateEnvironment(); UpdateEnvironment();
        void Filter()
        {
            var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kind = type.SelectedIndex == 1 ? (WeaponKind?)null : (type.SelectedItem as SmartModTypeRow)?.Kind;
            var all = vm.ComparisonChoices.Where(c => (kind == null || c.Kind == kind) && (sources.SelectedIndex == 0 || c.Source == sources.SelectedItem as string) && words.All(w => (c.DisplayName + " " + c.Source).Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
            results.ItemsSource = all.Take(200).ToArray(); resultCount.Text = string.Format(L("Smart_CompareResults"), Math.Min(all.Length, 200), all.Length);
        }
        search.TextChanged += (_, _) => Filter(); sources.SelectionChanged += (_, _) => Filter(); type.SelectionChanged += (_, _) => Filter();
        results.SelectionChanged += async (_, _) =>
        {
            _loading?.Cancel(); _loading?.Dispose(); _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); var token = _loading.Token; var generation = ++_generation;
            Sample = null; Invalidate(); RefreshTables(); if (sync.IsChecked == true) SamplePreview.SetView(MinePreview.ViewState); _ = SamplePreview.LoadAsync(null, preserveView: true);
            foreach (var combo in rightRecipes) combo.ItemsSource = null;
            if (results.SelectedItem is not SmartComparisonChoice choice) { status.Text = L("Smart_CompareChoose"); sampleTitle.Text = status.Text; return; }
            status.Text = L("Preview_Loading");
            try
            {
                var sample = await vm.LoadComparisonAsync(choice, token); if (_closed || token.IsCancellationRequested || generation != _generation) return;
                Sample = sample; sampleTitle.Text = L("Smart_CompareSample") + ": " + sample.Fields.Name + " [" + sample.Fields.EditorId + "] · " + sample.Source + " · " + L("WeaponType_" + sample.Kind);
                selector.IsExpanded = false;
                status.Text = sample.Notice + (sample.Kind != _item.Kind ? Environment.NewLine + L("Smart_CompareDifferentTypes") : "");
                for (var i = 0; i < 2; i++) { rightRecipes[i].ItemsSource = sample.Recipes.Where(r => r.Data.IsTempering == (i == 1)).ToArray(); rightRecipes[i].SelectedIndex = 0; }
                RefreshTables(); await SamplePreview.LoadAsync(new(sample.UseProjectResources ? vm.ProjectDirectory : "", sample.Fields.ModelPath, sample.Fields.FirstPersonPath, vm.GameDataPath), preserveView: true);
                if (!_closed && generation == _generation && sync.IsChecked == true) SamplePreview.SetView(MinePreview.ViewState);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed && generation == _generation) status.Text = L("Preview_Failed") + " " + ex.Message; }
        };
        foreach (var combo in leftRecipes.Concat(rightRecipes)) combo.SelectionChanged += (_, _) => { Invalidate(); RefreshTables(); };
        different.Checked += (_, _) => RefreshTables(); different.Unchecked += (_, _) => RefreshTables();
        foreach (var box in new[] { combat, economy, material, crafting, tempering }) { box.Checked += (_, _) => Invalidate(); box.Unchecked += (_, _) => Invalidate(); }
        review.Click += (_, _) =>
        {
            try
            {
                if (Sample == null) throw new InvalidOperationException(L("Smart_CompareChoose"));
                if (crafting.IsChecked == true && rightRecipes[0].SelectedItem == null || tempering.IsChecked == true && rightRecipes[1].SelectedItem == null) throw new InvalidOperationException(L("Smart_CompareRecipeMissing"));
                _pending = vm.PreviewComparison(Sample, new(combat.IsChecked == true, economy.IsChecked == true, material.IsChecked == true,
                    crafting.IsChecked == true ? rightRecipes[0].SelectedItem as WeaponComparisonRecipe : null, tempering.IsChecked == true ? rightRecipes[1].SelectedItem as WeaponComparisonRecipe : null));
                changes.Text = _pending.Description; apply.IsEnabled = true;
            }
            catch (Exception ex) { Invalidate(); status.Text = ex.Message; }
        };
        apply.Click += async (_, _) =>
        {
            if (_pending == null || vm.IsBusy) return; var preview = _pending; Invalidate();
            await vm.ApplyTuningAsync(preview); if (_closed) return; status.Text = vm.Error.Length > 0 ? vm.Error : L("Smart_CompareApplied"); RefreshLeftRecipes(); RefreshTables(); undo.IsEnabled = vm.CanUndoTuning;
        };
        undo.Click += async (_, _) => { if (!vm.IsBusy) { await vm.UndoTuningAsync(); Invalidate(); RefreshLeftRecipes(); RefreshTables(); status.Text = vm.Error; } };
        string? mineIdentity = null;
        void ItemChanged() { if (_closed) return; Invalidate(); RefreshLeftRecipes(); RefreshTables(); _leftTimer.Stop(); _leftTimer.Start(); }
        _item.Changed += ItemChanged;
        _leftTimer.Tick += async (_, _) => { _leftTimer.Stop(); await LoadMine(); };
        Task LoadMine()
        {
            var identity = vm.PreviewIdentity; if (mineIdentity == identity) return Task.CompletedTask; mineIdentity = identity;
            return MinePreview.LoadAsync(new(vm.ProjectDirectory, _item.ModelPath, _item.FirstPersonPath, vm.GameDataPath), preserveView: true);
        }
        Loaded += async (_, _) => { Filter(); RefreshLeftRecipes(); RefreshTables(); undo.IsEnabled = vm.CanUndoTuning; await LoadMine(); };
        Closed += (_, _) => { _closed = true; _generation++; _leftTimer.Stop(); _item.Changed -= ItemChanged; _loading?.Cancel(); _loading?.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); MinePreview.Dispose(); SamplePreview.Dispose(); };
    }
}
