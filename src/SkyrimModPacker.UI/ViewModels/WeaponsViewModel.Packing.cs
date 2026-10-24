using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty] private string? _lastProjectArchivePath;
    [ObservableProperty] private bool _enableProjectEsl;
    [ObservableProperty] private ObservableCollection<PluginDiagnostic> _pluginDiagnostics = [];
    partial void OnPluginPathChanged(string value)
    {
        PluginDiagnostics = [];
        _draftScheduler?.Cancel();
        DraftFilePath = DraftSaveStatus = DraftSaveError = null;
    }

    partial void OnSelectedPackingProfileChanged(ModProfile? value) => EnableProjectEsl = value?.EnableEsl ?? false;

    partial void OnWeaponKeywordsChanged(ObservableCollection<RecordOption>? oldValue, ObservableCollection<RecordOption> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnDraftKeywordsChanged;
        newValue.CollectionChanged += OnDraftKeywordsChanged;
        IsWizardSaved = false;
        UpdateUnsavedChanges();
    }

    private void OnDraftKeywordsChanged(object? sender, NotifyCollectionChangedEventArgs e) { IsWizardSaved = false; UpdateUnsavedChanges(); }

    [RelayCommand]
    private Task PackProjectAsync() => RunOperationAsync(async token =>
    {
        PluginDiagnostics = [];
        LastProjectArchivePath = null;
        RequireProject();
        RequirePlugin();
        if (!IsZipStep || !IsWizardSaved || HasUnsavedChanges)
            throw new InvalidOperationException(Text("Weapons_SaveBeforePacking", "Save the sword on Summary before packing. Unsaved changes are not included."));
        if (_packingPipeline == null) throw new InvalidOperationException("The packing pipeline is not available.");
        var settings = await _settings.LoadAsync(token);
        GameDataPath = settings.SkyrimDataPath;
        var profiles = await _profiles.LoadAllAsync(token);
        var existing = profiles.FirstOrDefault(p => p.Id == SelectedPackingProfile?.Id);
        if (existing != null && !string.Equals(existing.PluginSourcePath, PluginPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Text("Weapons_LinkBeforePacking", "Link the selected profile to this workspace mod first, or use Create profile."));
        var profile = new ModProfile
        {
            Id = existing?.Id ?? Guid.NewGuid(), Name = existing?.Name ?? Path.GetFileNameWithoutExtension(PluginPath),
            Version = existing?.Version ?? "1.0", Keywords = existing?.Keywords.ToList() ?? [], EnableEsl = EnableProjectEsl, IncludeExportReport = true,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
            PluginFileName = Path.GetFileName(PluginPath), PluginSourcePath = PluginPath, WeaponProjectPath = ProjectFilePath
        };
        // Validate before updating a saved profile or starting archive creation.
        var files = await _projects.GetPackingFilesAsync(ProjectFilePath, GameDataPath, token);
        if (files.IsFailure) throw new InvalidOperationException(files.Error);
        await _profiles.SaveAsync(profile, token);
        PackingProfiles = new ObservableCollection<ModProfile>(await _profiles.LoadAllAsync(token));
        SelectedPackingProfile = PackingProfiles.FirstOrDefault(p => p.Id == profile.Id);
        var progress = new Progress<double>(p => OperationProgress = $"{Math.Clamp(p, 0, 1) * 100:0}%");
        var result = await _packingPipeline.ExecuteAsync(profile, settings, progress, token);
        PluginDiagnostics = new ObservableCollection<PluginDiagnostic>(result.PluginDiagnostics);
        if (!result.IsSuccess) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors));
        LastProjectArchivePath = result.OutputArchivePath;
        StatusMessage = Text("Weapons_ProjectPacked", "Project archive created, including the plugin and registered resources.");
        if (result.Warnings.Count > 0)
            StatusMessage += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
    });
}
