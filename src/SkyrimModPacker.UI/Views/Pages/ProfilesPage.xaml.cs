using System.Windows.Controls;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views.Pages;

/// <summary>
/// Profiles management page.
/// </summary>
public partial class ProfilesPage : Page
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
