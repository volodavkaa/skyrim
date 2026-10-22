using System.Windows;

namespace SkyrimModPacker.UI.Services;

/// <summary>
/// Service managing dynamic application localization (English & Ukrainian).
/// Swaps active ResourceDictionaries at runtime for instant UI translation.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Current active language code ("en" or "uk").</summary>
    string CurrentLanguage { get; }

    /// <summary>Fired whenever the language is changed.</summary>
    event Action? LanguageChanged;

    /// <summary>Sets the active language and reloads resource dictionaries.</summary>
    void SetLanguage(string languageCode);

    /// <summary>Retrieves a localized string resource by key.</summary>
    string GetString(string key);
}

/// <summary>
/// Implementation of <see cref="ILocalizationService"/> using WPF Merged ResourceDictionaries.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private const string ResourceDictUriPrefix = "pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.";
    private ResourceDictionary? _currentDictionary;

    public string CurrentLanguage { get; private set; } = "en";

    public event Action? LanguageChanged;

    public void SetLanguage(string languageCode)
    {
        var normalized = languageCode.ToLowerInvariant() switch
        {
            "uk" or "ua" or "ukrainian" => "uk",
            _ => "en"
        };

        CurrentLanguage = normalized;

        try
        {
            var appResources = System.Windows.Application.Current?.Resources;
            if (appResources != null)
            {
                var uri = new Uri($"{ResourceDictUriPrefix}{normalized}.xaml", UriKind.Absolute);
                var newDict = new ResourceDictionary { Source = uri };

                if (_currentDictionary != null)
                {
                    appResources.MergedDictionaries.Remove(_currentDictionary);
                }

                appResources.MergedDictionaries.Add(newDict);
                _currentDictionary = newDict;
            }
        }
        catch
        {
            // Fallback gracefully in testing environments where Application.Current or pack URIs are not available
        }

        LanguageChanged?.Invoke();
    }

    public string GetString(string key)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.TryFindResource(key) is string val)
                return val;
        }
        catch
        {
            // Ignore in headless/test environments
        }

        return key;
    }
}
