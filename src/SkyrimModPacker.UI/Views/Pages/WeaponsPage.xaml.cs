using System.Windows.Controls;
using System.Windows;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views.Pages;

/// <summary>Weapons editor page; initialization is delegated to the ViewModel.</summary>
public partial class WeaponsPage : Page
{
    public WeaponsPage(WeaponsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WeaponsViewModel viewModel)
            await viewModel.InitializeCommand.ExecuteAsync(null);
    }
}
