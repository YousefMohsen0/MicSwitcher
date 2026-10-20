using System;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Application entry point: wires settings, audio, the switching controller, the tray icon and the window.</summary>
public partial class MicSwitcherApp : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private AudioDeviceService? _devices;
    private MicMonitor? _monitor;
    private SwitchController? _controller;
    private TrayManager? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool createdNew;
        try
        {
            _singleInstance = new Mutex(true, "MicSwitcher.SingleInstance", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // The other instance runs elevated and owns the mutex.
            createdNew = false;
        }

        if (!createdNew)
        {
            MessageBox.Show(
                "MicSwitcher is already running. Look for its icon in the system tray (click the ^ arrow if it is hidden).",
                "MicSwitcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        _settings = SettingsStore.Load();
        _devices = new AudioDeviceService();
        _monitor = new MicMonitor();
        _controller = new SwitchController(_settings, _devices, _monitor);
        _tray = new TrayManager(ShowWindow, SetPaused, ExitApplication, _settings.Paused);

        _controller.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() =>
        {
            _tray?.Update(status);
            _window?.UpdateStatus(status);
        }));
        _controller.Start();

        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AppLog.Info(elevated ? "MicSwitcher started (administrator)" : "MicSwitcher started");

        bool background = e.Args.Contains(StartupManager.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        if (!background)
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_controller is null || _devices is null)
        {
            return;
        }

        _window ??= new MainWindow(_settings, _controller, _devices, Persist, SetPaused);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>Saves settings and tells the controller to pick them up.</summary>
    private void Persist()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not save settings: {ex.Message}");
        }

        _controller?.ReloadSettings();
    }

    private void SetPaused(bool paused)
    {
        if (_settings.Paused == paused)
        {
            return;
        }

        _settings.Paused = paused;
        Persist();
        _tray?.SetPaused(paused);
        _window?.SyncPaused();
        AppLog.Info(paused ? "Automatic switching paused" : "Automatic switching resumed");
    }

    private void ExitApplication()
    {
        _window?.AllowClose();
        _window?.Close();
        _tray?.Dispose();
        _controller?.Dispose();
        _monitor?.Dispose();
        Shutdown();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Info($"Unexpected error: {e.Exception}");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex was not owned by this thread (second instance path).
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
