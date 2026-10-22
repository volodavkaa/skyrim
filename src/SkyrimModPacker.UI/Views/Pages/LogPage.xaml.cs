using System.Windows.Controls;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views.Pages;

/// <summary>
/// Log/journal page.
/// </summary>
public partial class LogPage : Page
{
    public LogPage(LogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
