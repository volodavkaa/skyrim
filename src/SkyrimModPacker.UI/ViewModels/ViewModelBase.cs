using CommunityToolkit.Mvvm.ComponentModel;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>
/// Base class for all ViewModels providing common functionality.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _pageTitle = string.Empty;
}
