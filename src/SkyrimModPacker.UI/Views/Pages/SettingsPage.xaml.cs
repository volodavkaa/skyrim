using System.Windows.Controls;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views.Pages;

/// <summary>
/// Settings page.
/// </summary>
public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
