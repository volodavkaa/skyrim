using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.Esl;
using Microsoft.Win32;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>
/// ViewModel for the Build screen — handles profile selection, file scanning,
/// interactive tree preview with file selection, and packing pipeline execution.
/// </summary>
public sealed partial class BuildViewModel : ViewModelBase
{
    private readonly IProfileRepository _profileRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IModScanner _modScanner;
    private readonly PackingPipeline _pipeline;
    private readonly ILogger<BuildViewModel>? _logger;
    private readonly IEslService? _eslService;

    [ObservableProperty]
    private EslAnalysisResult? _eslAnalysis;

    [ObservableProperty]
    private string? _analyzedPluginPath;

    [ObservableProperty]
    private string? _eslAnalysisError;

    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    private ObservableCollection<ModProfile> _profiles = [];

    [ObservableProperty]
    private ModProfile? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<FileTreeNode> _fileTree = [];

    [ObservableProperty]
    private List<ScannedFile> _scannedFiles = [];

    [ObservableProperty]
    private bool _hasScannedFiles;

    [ObservableProperty]
    private string _summaryText = "Select a profile and click Scan to discover mod files.";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string? _lastArchiveCreatedPath;

    [ObservableProperty]
    private bool _hasSuccessNotification;

    [ObservableProperty]
    private string? _errorMessage;

    public BuildViewModel(
        IProfileRepository profileRepository,
        ISettingsRepository settingsRepository,
        IModScanner modScanner,
        PackingPipeline pipeline,
        ILogger<BuildViewModel>? logger = null,
        IEslService? eslService = null)
    {
        _profileRepository = profileRepository;
        _settingsRepository = settingsRepository;
        _modScanner = modScanner;
        _pipeline = pipeline;
        _logger = logger;
        _eslService = eslService;
        PageTitle = "Build";

        // Load profiles on initialization
        _ = LoadProfilesAsync();
    }

    public async Task LoadProfilesAsync()
    {
        try
        {
            var list = await _profileRepository.LoadAllAsync();
            Profiles = new ObservableCollection<ModProfile>(list);

            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == SelectedProfile?.Id) ?? Profiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load profiles");
        }
    }

    partial void OnSelectedProfileChanged(ModProfile? value)
    {
        // Reset file tree when switching profiles
        ClearScanResults();
        if (value != null)
        {
            SummaryText = $"Profile '{value.Name}' selected. Click Scan to search for mod files.";
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedProfile == null)
        {
            ErrorMessage = "Please select or create a mod profile first.";
            return;
        }

        var settings = await _settingsRepository.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.SkyrimDataPath) || !Directory.Exists(settings.SkyrimDataPath))
        {
            ErrorMessage = "Skyrim Data path is not configured. Please set it in Settings first.";
            return;
        }

        ErrorMessage = null;
        HasSuccessNotification = false;
        IsLoading = true;
        IsIndeterminate = true;
        StatusMessage = $"Scanning Skyrim Data directory for '{SelectedProfile.Name}'...";

        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();

        try
        {
            var scanResult = await _modScanner.ScanAsync(
                settings.SkyrimDataPath,
                SelectedProfile,
                _operationCts.Token);

            if (scanResult.IsFailure)
            {
                ErrorMessage = scanResult.Error;
                ClearScanResults();
                return;
            }

            ScannedFiles = scanResult.Value;

            if (ScannedFiles.Count == 0)
            {
                ErrorMessage = $"No files found matching keywords [{string.Join(", ", SelectedProfile.Keywords)}] or plugin '{SelectedProfile.PluginFileName}'.";
                ClearScanResults();
                return;
            }

            var tree = FileTreeBuilder.BuildTree(ScannedFiles);
            FileTree = new ObservableCollection<FileTreeNode>(tree);
            HasScannedFiles = true;

            HookTreeEvents(FileTree);
            UpdateSummaryMetrics();

            StatusMessage = $"Scan completed: {ScannedFiles.Count} files found.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error occurred during scan");
            ErrorMessage = $"Error during scan: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            IsIndeterminate = false;
        }
    }

    [RelayCommand]
    private async Task PackAsync()
    {
        if (SelectedProfile == null)
        {
            ErrorMessage = "Please select a mod profile.";
            return;
        }

        var selectedCount = FileTree.Sum(n => n.SelectedFileCount);
        // Let project validation report every required file when the entire tree is unchecked.
        if (selectedCount == 0 && string.IsNullOrWhiteSpace(SelectedProfile.WeaponProjectPath))
        {
            ErrorMessage = "No files selected for packing. Please check at least one file.";
            return;
        }

        var settings = await _settingsRepository.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.SkyrimDataPath) || !Directory.Exists(settings.SkyrimDataPath))
        {
            ErrorMessage = "Skyrim Data path is not valid. Please configure it in Settings.";
            return;
        }

        ErrorMessage = null;
        HasSuccessNotification = false;
        IsLoading = true;
        IsIndeterminate = false;
        ProgressValue = 0;
        StatusMessage = $"Packing '{SelectedProfile.Name} {SelectedProfile.Version}'...";

        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();

        var progress = new Progress<double>(p =>
        {
            ProgressValue = p * 100;
        });

        try
        {
            // Execute the packing pipeline with current files
            var context = await _pipeline.ExecuteAsync(
                SelectedProfile,
                settings,
                progress,
                _operationCts.Token,
                fileSelection: ScannedFiles);

            if (context.IsSuccess)
            {
                LastArchiveCreatedPath = context.OutputArchivePath;
                HasSuccessNotification = true;
                StatusMessage = $"Successfully packed {context.OutputArchivePath} ({FileTreeNode.FormatBytes(context.ArchiveSizeBytes)})";
            }
            else
            {
                ErrorMessage = string.Join(Environment.NewLine, context.Errors);
                StatusMessage = "Packing failed.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Packing cancelled.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Packing failed with an exception");
            ErrorMessage = $"Packing failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeEslAsync(string? pluginPath)
    {
        if (IsLoading || _eslService == null)
            return;

        if (string.IsNullOrWhiteSpace(pluginPath))
        {
            var dialog = new OpenFileDialog
            {
                Title = "Analyze ESL compatibility",
                Filter = "Skyrim plugins (*.esp;*.esm;*.esl)|*.esp;*.esm;*.esl",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true)
                return;
            pluginPath = dialog.FileName;
        }

        EslAnalysis = null;
        EslAnalysisError = null;
        AnalyzedPluginPath = pluginPath;
        IsLoading = true;
        IsIndeterminate = true;
        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();
        try
        {
            var result = await _eslService.AnalyzeAsync(pluginPath, _operationCts.Token);
            if (result.IsSuccess)
                EslAnalysis = result.Value;
            else
                EslAnalysisError = result.Error;
        }
        catch (OperationCanceledException)
        {
            AnalyzedPluginPath = null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ESL analysis failed");
            EslAnalysisError = ex.Message;
        }
        finally
        {
            IsLoading = false;
            IsIndeterminate = false;
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _operationCts?.Cancel();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var root in FileTree)
        {
            root.IsChecked = true;
        }
        UpdateSummaryMetrics();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var root in FileTree)
        {
            root.IsChecked = false;
        }
        UpdateSummaryMetrics();
    }

    [RelayCommand]
    private void OpenArchiveLocation()
    {
        if (string.IsNullOrEmpty(LastArchiveCreatedPath) || !File.Exists(LastArchiveCreatedPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastArchiveCreatedPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open archive location");
        }
    }

    [RelayCommand]
    private void DismissNotification()
    {
        HasSuccessNotification = false;
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorMessage = null;
    }

    /// <summary>
    /// Processes folders or plugin files dragged and dropped into the application.
    /// </summary>
    public async Task HandleDropAsync(string[]? droppedPaths)
    {
        if (droppedPaths == null || droppedPaths.Length == 0)
            return;

        foreach (var path in droppedPaths)
        {
            if (Directory.Exists(path))
            {
                var dirName = Path.GetFileName(path);

                // If user dropped a Skyrim Data folder
                if (SkyrimModPacker.Infrastructure.Services.SkyrimLocator.IsValidSkyrimData(path))
                {
                    var settings = await _settingsRepository.LoadAsync();
                    settings.SkyrimDataPath = path;
                    await _settingsRepository.SaveAsync(settings);
                    StatusMessage = $"Skyrim Data path configured to: {path}";
                    continue;
                }

                // If user dropped a mod folder, add as keyword to profile and trigger scan
                if (SelectedProfile != null)
                {
                    if (!SelectedProfile.Keywords.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    {
                        SelectedProfile.Keywords.Add(dirName);
                        await _profileRepository.SaveAsync(SelectedProfile);
                    }

                    StatusMessage = $"Added keyword '{dirName}' from dropped folder. Scanning...";
                    await ScanAsync();
                    return;
                }
            }
            else if (File.Exists(path))
            {
                var ext = Path.GetExtension(path);
                if (ext.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".esl", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".esm", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(path);
                    if (SelectedProfile != null)
                    {
                        SelectedProfile.PluginFileName = fileName;
                        SelectedProfile.PluginSourcePath = Path.GetFullPath(path);
                        SelectedProfile.WeaponProjectPath = string.Empty;
                        await _profileRepository.SaveAsync(SelectedProfile);
                        StatusMessage = $"Plugin set to '{fileName}' from dropped file. Scanning...";
                        await ScanAsync();
                        return;
                    }
                }
            }
        }
    }

    private void HookTreeEvents(IEnumerable<FileTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FileTreeNode.IsChecked))
                {
                    UpdateSummaryMetrics();
                }
            };

            if (node.IsDirectory && node.Children.Count > 0)
            {
                HookTreeEvents(node.Children);
            }
        }
    }

    private void UpdateSummaryMetrics()
    {
        var total = FileTree.Sum(n => n.TotalFileCount);
        var selected = FileTree.Sum(n => n.SelectedFileCount);
        var selectedBytes = FileTree.Sum(n => n.SelectedSizeBytes);

        SummaryText = $"{selected} of {total} files selected ({FileTreeNode.FormatBytes(selectedBytes)})";
    }

    private void ClearScanResults()
    {
        FileTree.Clear();
        ScannedFiles.Clear();
        HasScannedFiles = false;
        HasSuccessNotification = false;
        ErrorMessage = null;
        LastArchiveCreatedPath = null;
        SummaryText = "Select a profile and click Scan to discover mod files.";
    }
}
