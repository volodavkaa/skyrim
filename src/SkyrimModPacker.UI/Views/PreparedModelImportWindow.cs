using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.Views;

/// <summary>One confirmation screen for the exact NIF and DDS created by the exporter.</summary>
public sealed class PreparedModelImportWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<WeaponFolderRequest, CancellationToken, Task<WeaponFolderPlan>> _preview;
    private readonly WeaponFolderRequest _request;
    private readonly Func<WeaponFolderPlan, Task<(bool Success, string Error)>> _execute;
    private readonly Func<string, string> _label;
    private readonly TextBox _name = new() { Name = "PreparedProjectName" };
    private readonly TextBox _parent = new() { Name = "PreparedParentFolder" };
    private readonly Button _refresh = new() { Name = "PreparedRefresh" };
    private readonly StackPanel _destination = new();
    private bool _busy, _closed;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _import = new() { IsEnabled = false, MinWidth = 170 };
    private WeaponFolderPlan? _plan;

    public WeaponFolderPlan? Plan => DialogResult == true ? _plan : null;

    public PreparedModelImportWindow(PreparedModelHandoff handoff, WeaponFolderRequest request,
        Func<WeaponFolderRequest, CancellationToken, Task<WeaponFolderPlan>> preview,
        Func<WeaponFolderPlan, Task<(bool Success, string Error)>> execute,
        Func<string, string> label)
    {
        _request = request;
        _preview = preview;
        _execute = execute; _label = label;
        Title = label("Smart_PreparedReview");
        Width = 740; Height = 660; MinWidth = 560; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(16, 23, 34));
        Foreground = Brushes.White; FontSize = 14;
        Resources.MergedDictionaries.Add(new() { Source = new Uri(
            "/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });

        var layout = new DockPanel { Margin = new Thickness(20) };
        Content = layout;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = Title, FontSize = 23,
            FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = label("Smart_PreparedReviewHint"),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 5, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = label("Smart_Cancel"), MinWidth = 110,
            IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        _import.Content = label("Smart_PreparedImport");
        _import.Style = (Style)FindResource("SmartPrimary");
        _refresh.Content = label("Smart_PreparedRefresh");
        _refresh.Click += async (_, _) => await LoadPlanAsync();
        _import.Click += async (_, _) => await ImportAsync();
        buttons.Children.Add(_refresh); buttons.Children.Add(cancel); buttons.Children.Add(_import);
        footer.Children.Add(_status); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);

        var description = new StackPanel();
        description.Children.Add(new TextBlock { Text = handoff.Name + " · " +
            label("WeaponType_" + handoff.Kind), FontWeight = FontWeights.SemiBold });
        description.Children.Add(new TextBlock { Text = label("Models_Third") + ": " +
            Path.GetRelativePath(handoff.Folder, handoff.ThirdPerson), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0) });
        description.Children.Add(new TextBlock { Text = label("Models_First") + ": " +
            Path.GetRelativePath(handoff.Folder, handoff.FirstPerson), TextWrapping = TextWrapping.Wrap });
        if (request.NewProjectPluginName != null)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(request.ProjectPath))!;
            _name.Text = Path.GetFileNameWithoutExtension(request.NewProjectPluginName);
            _parent.Text = Directory.GetParent(directory)?.FullName ?? "";
            _destination.Children.Add(new TextBlock { Text = label("Smart_FolderProjectName"), Margin = new Thickness(0, 8, 0, 0) });
            _destination.Children.Add(_name);
            _destination.Children.Add(new TextBlock { Text = label("Smart_Workspace"), Margin = new Thickness(0, 6, 0, 0) });
            var line = new DockPanel(); var browse = new Button { Content = "…" };
            DockPanel.SetDock(browse, Dock.Right); line.Children.Add(browse); line.Children.Add(_parent); _destination.Children.Add(line);
            var suggest = new Button { Name = "PreparedSuggest", Content = label("Smart_FolderAvailableName"), HorizontalAlignment = HorizontalAlignment.Left };
            _destination.Children.Add(suggest); description.Children.Add(_destination);
            browse.Click += (_, _) => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) _parent.Text = dialog.FolderName; };
            suggest.Click += (_, _) => { try { _name.Text = WeaponImportDestination.AvailableName(_parent.Text, _name.Text); } catch (Exception ex) { _details.Text = ex.Message; } };
            _name.TextChanged += (_, _) => Invalidate(); _parent.TextChanged += (_, _) => Invalidate();
        }
        else description.Children.Add(new TextBlock { Text = label("Smart_Workspace") + ": " + Path.GetDirectoryName(request.ProjectPath), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        _details.MinHeight = 150; _details.MaxHeight = 220;
        description.Children.Add(_details);
        layout.Children.Add(new ScrollViewer { Content = description, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Loaded += async (_, _) => await LoadPlanAsync();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    private void Invalidate() { _plan = null; _import.IsEnabled = false; _status.Text = _label("Smart_PreparedRecheck"); }
    private void SetBusy(bool busy) { _busy = busy; _destination.IsEnabled = !busy; _refresh.IsEnabled = !busy; _import.IsEnabled = !busy && _plan?.CanImport == true; }
    private async Task LoadPlanAsync()
    {
        if (_busy) return;
        _plan = null; SetBusy(true); _status.Text = _label("Smart_PreparedChecking");
        try
        {
            var request = _request.NewProjectPluginName == null ? _request : _request with {
                ProjectPath = WeaponImportDestination.ProjectPath(_parent.Text, _name.Text), NewProjectPluginName = _name.Text.Trim() + ".esp" };
            var plan = await _preview(request, _cancellation.Token);
            if (_closed) return;
            _plan = plan;
            _details.Text = string.Join(Environment.NewLine,
                plan.Files.Select(file => file.RelativePath +
                    (file.Conflict == null ? "" : " — " + file.Conflict))
                .Concat(plan.Dependencies.Where(item => item.Status != "Import")
                    .Select(item => item.Path + " — " + item.Status))
                .Concat(plan.Warnings));
            _status.Text = plan.CanImport
                ? string.Format(_label("Smart_PreparedReady"), plan.Files.Count)
                : _label("Smart_PreparedBlocked");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _status.Text = _label("Smart_PreparedBlocked");
            _details.Text = error.Message;
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private async Task ImportAsync()
    {
        if (_busy || _plan?.CanImport != true) return;
        var plan = _plan; SetBusy(true); _status.Text = _label("Smart_PreparedImporting");
        try
        {
            var result = await _execute(plan);
            if (result.Success) { _busy = false; DialogResult = true; return; }
            _plan = null; _status.Text = _label("Smart_PreparedBlocked");
            _details.Text = string.IsNullOrWhiteSpace(result.Error) ? _label("Smart_PreparedRecheck") : result.Error;
        }
        catch (Exception error) { _plan = null; _status.Text = _label("Smart_PreparedBlocked"); _details.Text = error.Message; }
        finally { if (!_closed) SetBusy(false); }
    }
}
