using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>
/// ViewModel for the Profiles screen — allows creating, editing, duplicating, and deleting mod profiles.
/// </summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileRepository _profileRepository;
    private readonly ILogger<ProfilesViewModel>? _logger;

    [ObservableProperty]
    private ObservableCollection<ModProfile> _profiles = [];

    [ObservableProperty]
    private ModProfile? _selectedProfile;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _version = "1.0";

    [ObservableProperty]
    private string _pluginFileName = string.Empty;

    [ObservableProperty]
    private string _pluginSourcePath = string.Empty;

    [ObservableProperty]
    private string _weaponProjectPath = string.Empty;

    [ObservableProperty]
    private bool _enableEsl;

    [ObservableProperty]
    private ObservableCollection<string> _keywords = [];

    [ObservableProperty]
    private string _newKeyword = string.Empty;

    [ObservableProperty]
    private string? _selectedKeyword;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isEditing;

    public ProfilesViewModel(
        IProfileRepository profileRepository,
        ILogger<ProfilesViewModel>? logger = null)
    {
        _profileRepository = profileRepository;
        _logger = logger;
        PageTitle = "Profiles";

        _ = LoadProfilesAsync();
    }

    public async Task LoadProfilesAsync()
    {
        try
        {
            var list = await _profileRepository.LoadAllAsync();
            Profiles = new ObservableCollection<ModProfile>(list);

            if (Profiles.Count > 0)
            {
                SelectedProfile = Profiles[0];
            }
            else
            {
                // Create a default starter profile if none exist
                CreateNewProfile();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load profiles");
            ErrorMessage = $"Failed to load profiles: {ex.Message}";
        }
    }

    partial void OnSelectedProfileChanged(ModProfile? value)
    {
        if (value == null)
        {
            IsEditing = false;
            return;
        }

        Name = value.Name;
        Version = value.Version;
        PluginFileName = value.PluginFileName;
        PluginSourcePath = value.PluginSourcePath;
        WeaponProjectPath = value.WeaponProjectPath;
        EnableEsl = value.EnableEsl;
        Keywords = new ObservableCollection<string>(value.Keywords);
        NewKeyword = string.Empty;
        SelectedKeyword = null;
        ErrorMessage = null;
        StatusMessage = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CreateNewProfile()
    {
        var newProfile = new ModProfile
        {
            Name = "NewMod",
            Version = "1.0",
            Keywords = ["NewMod"],
            PluginFileName = "NewMod.esp"
        };

        Profiles.Add(newProfile);
        SelectedProfile = newProfile;
        StatusMessage = "New profile created. Don't forget to save changes.";
    }

    [RelayCommand]
    private void AddKeyword()
    {
        if (string.IsNullOrWhiteSpace(NewKeyword))
            return;

        var trimmed = NewKeyword.Trim();
        if (!Keywords.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            Keywords.Add(trimmed);
            NewKeyword = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveKeyword(string? keyword)
    {
        var target = keyword ?? SelectedKeyword;
        if (!string.IsNullOrEmpty(target))
        {
            Keywords.Remove(target);
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (SelectedProfile == null)
            return;

        var updatedProfile = new ModProfile
        {
            Id = SelectedProfile.Id,
            Name = Name.Trim(),
            Version = Version.Trim(),
            PluginFileName = PluginFileName.Trim(),
            PluginSourcePath = PluginSourcePath.Trim(),
            WeaponProjectPath = WeaponProjectPath.Trim(),
            EnableEsl = EnableEsl,
            Keywords = Keywords.ToList(),
            CreatedAt = SelectedProfile.CreatedAt,
            ModifiedAt = DateTime.UtcNow
        };

        var validationResult = ModProfileValidator.Validate(updatedProfile);
        if (validationResult.IsFailure)
        {
            ErrorMessage = validationResult.Error;
            return;
        }

        try
        {
            await _profileRepository.SaveAsync(updatedProfile);

            // Update in collection
            var index = Profiles.IndexOf(SelectedProfile);
            if (index >= 0)
            {
                Profiles[index] = updatedProfile;
                SelectedProfile = updatedProfile;
            }

            ErrorMessage = null;
            StatusMessage = $"Profile '{updatedProfile.Name}' saved successfully.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save profile");
            ErrorMessage = $"Failed to save: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile == null)
            return;

        try
        {
            await _profileRepository.DeleteAsync(SelectedProfile.Id);
            var removed = SelectedProfile;
            Profiles.Remove(removed);
            SelectedProfile = Profiles.FirstOrDefault();
            StatusMessage = $"Profile '{removed.Name}' deleted.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete profile");
            ErrorMessage = $"Failed to delete: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DuplicateProfileAsync()
    {
        if (SelectedProfile == null)
            return;

        var clone = new ModProfile
        {
            Name = $"{SelectedProfile.Name} - Copy",
            Version = SelectedProfile.Version,
            PluginFileName = SelectedProfile.PluginFileName,
            PluginSourcePath = SelectedProfile.PluginSourcePath,
            WeaponProjectPath = SelectedProfile.WeaponProjectPath,
            EnableEsl = SelectedProfile.EnableEsl,
            Keywords = new List<string>(SelectedProfile.Keywords)
        };

        try
        {
            await _profileRepository.SaveAsync(clone);
            Profiles.Add(clone);
            SelectedProfile = clone;
            StatusMessage = $"Profile duplicated as '{clone.Name}'.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to duplicate profile");
            ErrorMessage = $"Failed to duplicate: {ex.Message}";
        }
    }
}
