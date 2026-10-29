using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SkyrimModPacker.Application;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Logging;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views;
using SkyrimModPacker.UI.Views.Pages;

namespace SkyrimModPacker.UI;

/// <summary>
/// Application entry point. Configures DI, Serilog logging, global unhandled exception handling, and localization.
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyrimModPacker");

    private static readonly InMemoryLogSink _inMemoryLogSink = new();

    private static readonly IHost _host = Host
        .CreateDefaultBuilder()
        .UseSerilog((context, configuration) =>
        {
            configuration
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(AppDataDir, "logs", "log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .WriteTo.Sink(_inMemoryLogSink);
        })
        .ConfigureServices((context, services) =>
        {
            // Localization
            services.AddSingleton<ILocalizationService, LocalizationService>();
            services.AddSingleton<IUnsavedChangesPrompt, UnsavedChangesPrompt>();
            services.AddSingleton<IDraftAutosaveScheduler, DraftAutosaveScheduler>();
            services.AddSingleton<IWorkspaceConflictPrompt, WorkspaceConflictPrompt>();
            services.AddSingleton<IWorkspaceFolderOpener, WorkspaceFolderOpener>();
            services.AddSingleton<IModelPreviewPresenter, ModelPreviewPresenter>();
            services.AddSingleton<IModelThumbnailService, ModelThumbnailService>();
            services.AddTransient<SmartModViewModel>();

            // The mod editor is the single creation workflow.
            services.AddTransient<SettingsPage>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<LogPage>();
            services.AddTransient<LogViewModel>();

            // Application layer
            services.AddApplicationServices();

            // Infrastructure layer (with shared in-memory log sink)
            services.AddInfrastructureServices(AppDataDir, _inMemoryLogSink);
        })
        .Build();

    public App()
    {
        // Global Unhandled Exception Handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        // Apply saved language setting
        try
        {
            var settingsRepo = _host.Services.GetRequiredService<ISettingsRepository>();
            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            var settings = await settingsRepo.LoadAsync();
            if (!string.IsNullOrEmpty(settings.Language))
            {
                localization.SetLanguage(settings.Language);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply initial language setting");
        }

        var editor = new SmartModWindow(
            _host.Services.GetRequiredService<SmartModViewModel>(),
            _host.Services.GetRequiredService<IModelPreviewService>())
        {
            ShowSettings = owner => ShowToolPage<SettingsPage>(owner, "Nav_Settings"),
            ShowLog = owner => ShowToolPage<LogPage>(owner, "Nav_Log")
        };
        MainWindow = editor;
        editor.Show();

        base.OnStartup(e);
    }

    private void ShowToolPage<TPage>(Window owner, string titleKey) where TPage : System.Windows.Controls.Page
    {
        var localization = _host.Services.GetRequiredService<ILocalizationService>();
        var window = new Window
        {
            Owner = owner,
            Title = localization.GetString(titleKey),
            Content = _host.Services.GetRequiredService<TPage>(),
            Width = 960,
            Height = 720,
            MinWidth = 700,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.ShowDialog();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled UI Exception occurred");

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails have been written to the log.",
            "Skyrim Mod Packer - Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "Unhandled AppDomain Exception occurred");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved Task Exception occurred");
        e.SetObserved();
    }
}
