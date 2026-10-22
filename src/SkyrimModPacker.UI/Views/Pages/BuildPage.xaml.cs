using System.Windows;
using System.Windows.Controls;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views.Pages;

/// <summary>
/// Build page — main workflow: scan → preview → pack with drag-and-drop support.
/// </summary>
public partial class BuildPage : Page
{
    public BuildPage(BuildViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BuildViewModel viewModel && !viewModel.IsLoading)
            await viewModel.LoadProfilesAsync();
    }

    private void OnPageDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnPageDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            DataContext is BuildViewModel vm)
        {
            _ = vm.HandleDropAsync(files);
        }
    }
}
