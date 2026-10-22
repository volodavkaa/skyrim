using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Logging;

namespace SkyrimModPacker.UI.ViewModels;

/// <summary>
/// ViewModel for the Log & History screen — displays recent packing history,
/// live console output from Serilog, and log file shortcuts.
/// </summary>
public sealed partial class LogViewModel : ViewModelBase
{
    private readonly IBuildHistoryRepository _historyRepository;
    private readonly ILogEventService? _logService;
    private readonly ILogger<LogViewModel>? _logger;

    [ObservableProperty]
    private ObservableCollection<BuildRecord> _historyRecords = [];

    [ObservableProperty]
    private ObservableCollection<LogEntry> _liveLogs = [];

    [ObservableProperty]
    private string _logsDirectoryPath;

    [ObservableProperty]
    private string? _statusMessage;

    public LogViewModel(
        IBuildHistoryRepository historyRepository,
        ILogEventService? logService = null,
        ILogger<LogViewModel>? logger = null)
    {
        _historyRepository = historyRepository;
        _logService = logService;
        _logger = logger;
        PageTitle = "Log";

        _logsDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkyrimModPacker",
            "logs");

        _ = LoadHistoryAsync();
        InitLiveLogs();
    }

    private void InitLiveLogs()
    {
        if (_logService == null)
            return;

        var recents = _logService.GetRecentEntries();
        LiveLogs = new ObservableCollection<LogEntry>(recents);

        _logService.LogReceived += entry =>
        {
            var app = System.Windows.Application.Current;
            if (app != null)
            {
                app.Dispatcher.InvokeAsync(() =>
                {
                    LiveLogs.Add(entry);
                    if (LiveLogs.Count > 1000)
                    {
                        LiveLogs.RemoveAt(0);
                    }
                });
            }
            else
            {
                LiveLogs.Add(entry);
            }
        };
    }

    public async Task LoadHistoryAsync()
    {
        try
        {
            var records = await _historyRepository.LoadAllAsync();
            HistoryRecords = new ObservableCollection<BuildRecord>(
                records.OrderByDescending(r => r.Timestamp));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load build history");
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _historyRepository.ClearAsync();
            HistoryRecords.Clear();
            StatusMessage = "Build history cleared.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear build history");
            StatusMessage = $"Failed to clear history: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearLiveLogs()
    {
        _logService?.Clear();
        LiveLogs.Clear();
        StatusMessage = "Console logs cleared.";
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            if (!Directory.Exists(LogsDirectoryPath))
            {
                Directory.CreateDirectory(LogsDirectoryPath);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", LogsDirectoryPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open logs folder");
        }
    }

    [RelayCommand]
    private void OpenArchive(string? archivePath)
    {
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{archivePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to locate archive in explorer");
        }
    }
}
