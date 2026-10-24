using System.Windows.Controls;
using System.Windows;
using System.IO;
using System.ComponentModel;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.Views.Pages;

/// <summary>Weapons editor page; initialization is delegated to the ViewModel.</summary>
public partial class WeaponsPage : Page
{
    private readonly ISmartModWindowLauncher? _smartModLauncher;
    public static readonly DependencyProperty EditorScaleProperty = DependencyProperty.Register(nameof(EditorScale), typeof(double), typeof(WeaponsPage),
        new FrameworkPropertyMetadata(1d), value => double.IsFinite((double)value) && (double)value >= 0.85 && (double)value <= 1.3);

    public double EditorScale { get => (double)GetValue(EditorScaleProperty); set => SetValue(EditorScaleProperty, value); }

    public WeaponsPage(WeaponsViewModel viewModel, ISmartModWindowLauncher? smartModLauncher = null)
    {
        _smartModLauncher = smartModLauncher;
        DataContext = viewModel;
        InitializeComponent();
    }

    private async void OnSmartMod(object sender, RoutedEventArgs e)
    {
        if (_smartModLauncher == null || DataContext is not WeaponsViewModel vm || !await vm.RequestLeaveEditorAsync()) return;
        _smartModLauncher.Show(Window.GetWindow(this), vm.ProjectFilePath);
        await vm.ReloadAfterSmartModAsync();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WeaponsViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnWizardStepChanged;
            viewModel.PropertyChanged += OnWizardStepChanged;
            await viewModel.InitializeCommand.ExecuteAsync(null);
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WeaponsViewModel viewModel) viewModel.PropertyChanged -= OnWizardStepChanged;
    }

    private void OnWizardStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WeaponsViewModel.CurrentStep)) WizardScroll.ScrollToTop();
    }

    private void OnModelDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = DataContext is WeaponsViewModel { IsLoading: false } vm && !string.IsNullOrWhiteSpace(vm.ProjectFilePath) &&
            files is { Length: 1 } && Path.GetExtension(files[0]).Equals(".nif", StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnModelDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is WeaponsViewModel { IsLoading: false } vm && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
            Path.GetExtension(files[0]).Equals(".nif", StringComparison.OrdinalIgnoreCase))
            await vm.ImportModelCommand.ExecuteAsync(files[0]);
    }
}
