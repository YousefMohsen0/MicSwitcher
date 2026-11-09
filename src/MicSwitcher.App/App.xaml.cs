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
    private CaptureMonitor? _monitor;
    private SwitchController? _controller;
    private TrayManager? _tray;
    private MainWindow? _window;
    private UpdateService? _updates;
    private Timer? _updateTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--enable-startup", StringComparer.OrdinalIgnoreCase))
        {
            // Used by the installer: registers the "start with Windows" task (one UAC prompt) and exits.
            string? error = StartupManager.SetEnabled(true);
            if (error is null)
            {
                _settings = SettingsStore.Load();
                _settings.StartWithWindows = true;
                SettingsStore.Save(_settings);
            }
            else
            {
                AppLog.Info($"Could not enable start with Windows: {error}");
            }

            Shutdown();
            return;
        }

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
        // Tray-icon clicks run on WinForms' own handler, which would otherwise show a crash dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, args) => AppLog.Info($"Unexpected error: {args.Exception}");
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _settings = SettingsStore.Load();
        AppTheme.Apply(_settings.Theme);
        _devices = new AudioDeviceService();
        _monitor = new CaptureMonitor();
        _controller = new SwitchController(_settings, _devices, _monitor);
        _tray = new TrayManager(ShowWindow, SetPaused, ExitApplication, _settings.Paused, role => MicAppearance.NameOf(_settings, role));

        _controller.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() =>
        {
            _tray?.Update(status);
            _window?.UpdateStatus(status);
        }));
        _controller.Start();
        _updates = new UpdateService(_settings);
        // Check shortly after start, then every 12 hours; one small request, so the cost is negligible.
        _updateTimer = new Timer(_ => _ = BackgroundUpdateCheck(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));

        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AppLog.Info(elevated ? "MicSwitcher started (administrator)" : "MicSwitcher started");

        bool background = e.Args.Contains(StartupManager.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        if (!background)
        {
            ShowWindow();
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MemoryTrimmer.Trim));
        }
    }

    private void ShowWindow()
    {
        if (_controller is null || _devices is null)
        {
            return;
        }

        // A window that was built before sleep can come back broken (its resources were lost), so on any failure
        // the window is thrown away and built once more from scratch.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (_window is null)
                {
                    _window = new MainWindow(_settings, _controller, _devices, Persist, SetPaused, _updates ??= new UpdateService(_settings));
                    _window.Closed += (_, _) =>
                    {
                        _window = null;
                        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MemoryTrimmer.Trim));
                    };
                }

                _window.Show();
                if (_window.WindowState == WindowState.Minimized)
                {
                    _window.WindowState = WindowState.Normal;
                }

                _window.Activate();
                return;
            }
            catch (Exception ex)
            {
                AppLog.Info($"Could not open the window (attempt {attempt}): {ex}");
                DiscardWindow();
                if (attempt == 2)
                {
                    MessageBox.Show(
                        "MicSwitcher could not open its window:\n\n" + ex.Message +
                        "\n\nThe details were saved to %AppData%\\MicSwitcher\\log.txt.",
                        "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private void DiscardWindow()
    {
        MainWindow? broken = _window;
        _window = null;
        try
        {
            broken?.AllowClose();
            broken?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not close the broken window: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task BackgroundUpdateCheck()
    {
        try
        {
            if (_updates is null || !_settings.AutoCheckUpdates)
            {
                return;
            }

            ReleaseInfo? release = await _updates.CheckAsync();
            if (release is null)
            {
                return;
            }

            AppLog.Info($"Update available: {release.Tag}");
            if (_settings.AutoInstallUpdates && await _updates.DownloadAndInstallAsync(release))
            {
                await Dispatcher.BeginInvoke(new Action(ExitApplication));
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"Update check failed: {ex.Message}");
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                _controller?.Suspend();
                Dispatcher.BeginInvoke(new Action(DiscardWindow));
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                _controller?.Resume();
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"Power change handling failed: {ex.Message}");
        }
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
        if (_controller?.LastStatus is ControllerStatus current)
        {
            _tray?.Update(current);
        }
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
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _updateTimer?.Dispose();
        DiscardWindow();
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
