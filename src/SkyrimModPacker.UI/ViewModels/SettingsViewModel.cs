using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Services;
using Wpf.Ui.Appearance;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>
/// ViewModel for Settings — controls game path auto-detection, output directories,
/// packing performance preferences, and UI appearance.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly ISkyrimLocator _skyrimLocator;
    private readonly Services.ILocalizationService? _localizationService;
    private readonly ILogger<SettingsViewModel>? _logger;

    [ObservableProperty]
    private string _skyrimDataPath = string.Empty;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private bool _useTempDirectory = true;

    [ObservableProperty]
    private bool _overwriteExistingArchive;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private string _language = "en";

    [ObservableProperty]
    private bool _isValidSkyrimData;

    [ObservableProperty]
    private bool _isLocating;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        ISkyrimLocator skyrimLocator,
        Services.ILocalizationService? localizationService = null,
        ILogger<SettingsViewModel>? logger = null)
    {
        _settingsRepository = settingsRepository;
        _skyrimLocator = skyrimLocator;
        _localizationService = localizationService;
        _logger = logger;
        PageTitle = "Settings";

        _ = LoadSettingsAsync();
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _settingsRepository.LoadAsync();
            SkyrimDataPath = settings.SkyrimDataPath;
            OutputDirectory = settings.OutputDirectory;
            UseTempDirectory = settings.UseTempDirectory;
            OverwriteExistingArchive = settings.OverwriteExistingArchive;
            Theme = settings.Theme;
            Language = settings.Language;

            ValidateDataPath();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load settings");
            ErrorMessage = $"Failed to load settings: {ex.Message}";
        }
    }

    partial void OnSkyrimDataPathChanged(string value)
    {
        ValidateDataPath();
    }

    partial void OnLanguageChanged(string value)
    {
        _localizationService?.SetLanguage(value);
    }

    private void ValidateDataPath()
    {
        IsValidSkyrimData = SkyrimLocator.IsValidSkyrimData(SkyrimDataPath);
    }

    [RelayCommand]
    private void BrowseSkyrimData()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Skyrim Data Directory",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            SkyrimDataPath = dialog.FolderName;
            ValidateDataPath();
            StatusMessage = IsValidSkyrimData
                ? "Valid Skyrim Data directory selected."
                : "Warning: Directory does not appear to contain standard Skyrim Data files (Skyrim.esm, meshes, textures).";
        }
    }

    [RelayCommand]
    private async Task AutoDetectSkyrimAsync()
    {
        IsLocating = true;
        StatusMessage = "Searching for Skyrim installation via Registry and Steam...";
        ErrorMessage = null;

        try
        {
            var result = await Task.Run(() => _skyrimLocator.LocateSkyrimDataPath());

            if (result.IsSuccess)
            {
                SkyrimDataPath = result.Value;
                ValidateDataPath();
                StatusMessage = $"Skyrim detected at: {SkyrimDataPath}";
            }
            else
            {
                ErrorMessage = result.Error;
                StatusMessage = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Auto-detection failed");
            ErrorMessage = $"Auto-detection failed: {ex.Message}";
        }
        finally
        {
            IsLocating = false;
        }
    }

    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Output Directory for Packed Archives",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void ResetOutputDirectory()
    {
        OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        StatusMessage = "Output directory reset to Desktop.";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettings
        {
            SkyrimDataPath = SkyrimDataPath.Trim(),
            OutputDirectory = OutputDirectory.Trim(),
            UseTempDirectory = UseTempDirectory,
            OverwriteExistingArchive = OverwriteExistingArchive,
            Theme = Theme,
            Language = Language
        };

        try
        {
            await _settingsRepository.SaveAsync(settings);

            // Apply theme dynamically
            ApplyTheme(Theme);

            ErrorMessage = null;
            StatusMessage = "Settings saved successfully.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save settings");
            ErrorMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    private static void ApplyTheme(string theme)
    {
        try
        {
            var appTheme = theme switch
            {
                "Light" => ApplicationTheme.Light,
                "Dark" => ApplicationTheme.Dark,
                _ => ApplicationTheme.Dark
            };

            ApplicationThemeManager.Apply(appTheme);
        }
        catch
        {
            // Ignore theme application error in non-WPF contexts (e.g. tests)
        }
    }
}
