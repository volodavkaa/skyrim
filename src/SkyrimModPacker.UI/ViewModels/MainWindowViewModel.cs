using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.Views.Pages;
using Wpf.Ui.Controls;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>
/// ViewModel for the main window — defines navigation items for the sidebar and updates their titles on language changes.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILocalizationService? _localizationService;

    private readonly NavigationViewItem _buildItem = new("Build", SymbolRegular.BuildingFactory24, typeof(BuildPage));
    private readonly NavigationViewItem _profilesItem = new("Profiles", SymbolRegular.PersonBoard24, typeof(ProfilesPage));
    private readonly NavigationViewItem _settingsItem = new("Settings", SymbolRegular.Settings24, typeof(SettingsPage));
    private readonly NavigationViewItem _logItem = new("Log", SymbolRegular.ClipboardTextLtr24, typeof(LogPage));

    [ObservableProperty]
    private ObservableCollection<object> _menuItems;

    [ObservableProperty]
    private ObservableCollection<object> _footerMenuItems;

    public MainWindowViewModel(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        PageTitle = "Skyrim Mod Packer";

        _menuItems = [_buildItem, _profilesItem];
        _footerMenuItems = [_settingsItem, _logItem];

        UpdateLocalizedNavigation();

        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += UpdateLocalizedNavigation;
        }
    }

    private void UpdateLocalizedNavigation()
    {
        if (_localizationService == null)
            return;

        _buildItem.Content = _localizationService.GetString("Nav_Build");
        _profilesItem.Content = _localizationService.GetString("Nav_Profiles");
        _settingsItem.Content = _localizationService.GetString("Nav_Settings");
        _logItem.Content = _localizationService.GetString("Nav_Log");
    }
}
