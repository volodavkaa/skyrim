using System.Windows;
using System.Windows.Controls;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.UI.Controls;

public partial class PluginDiagnosticsView : UserControl
{
    public static readonly DependencyProperty DiagnosticsProperty = DependencyProperty.Register(nameof(Diagnostics),
        typeof(IEnumerable<PluginDiagnostic>), typeof(PluginDiagnosticsView), new PropertyMetadata(null, OnDiagnosticsChanged));

    public IEnumerable<PluginDiagnostic>? Diagnostics
    {
        get => (IEnumerable<PluginDiagnostic>?)GetValue(DiagnosticsProperty);
        set => SetValue(DiagnosticsProperty, value);
    }

    public PluginDiagnosticsView() => InitializeComponent();

    private static void OnDiagnosticsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (PluginDiagnosticsView)sender;
        var values = (args.NewValue as IEnumerable<PluginDiagnostic>)?.ToArray() ?? [];
        view.Errors.ItemsSource = values.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        view.Warnings.ItemsSource = values.Where(d => d.Severity == DiagnosticSeverity.Warning).ToArray();
        view.Unsupported.ItemsSource = values.Where(d => d.Severity == DiagnosticSeverity.Unsupported).ToArray();
        view.Panel.Visibility = values.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
