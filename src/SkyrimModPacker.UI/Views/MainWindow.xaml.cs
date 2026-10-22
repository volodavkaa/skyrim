using SkyrimModPacker.UI.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace SkyrimModPacker.UI.Views;

/// <summary>
/// Main application window with WPF-UI Fluent navigation.
/// No business logic — only wires up the navigation service to the NavigationView control.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService)
    {
        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        // Connect WPF-UI's navigation service to the NavigationView control
        navigationService.SetNavigationControl(RootNavigation);
    }
}
