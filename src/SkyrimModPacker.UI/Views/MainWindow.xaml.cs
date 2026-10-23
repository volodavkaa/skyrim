using SkyrimModPacker.UI.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;
using System.Windows;
using System.ComponentModel;

namespace SkyrimModPacker.UI.Views;

/// <summary>
/// Main application window with WPF-UI Fluent navigation.
/// No business logic — only wires up the navigation service to the NavigationView control.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindowViewModel ViewModel { get; }
    private readonly WeaponsViewModel _weaponsEditor;
    private bool _closeApproved, _closePending;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService, WeaponsViewModel weaponsEditor)
    {
        _weaponsEditor = weaponsEditor;
        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        // Connect WPF-UI's navigation service to the NavigationView control
        navigationService.SetNavigationControl(RootNavigation);
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        try
        {
            // Return from the first Closing event before requesting a second close.
            await System.Windows.Threading.Dispatcher.Yield();
            if (!await _weaponsEditor.RequestLeaveEditorAsync()) return;
            _closeApproved = true;
            Close();
        }
        finally { _closeApproved = false; _closePending = false; }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RootNavigation == null) return;
        RootNavigation.PaneDisplayMode = ActualWidth < 1000
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : NavigationViewPaneDisplayMode.Left;
    }
}
