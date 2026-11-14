using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Application entry point: wires settings, audio, the switching controller, the tray icon and the windows.</summary>
public partial class MicSwitcherApp : Application
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicSwitcher", "history.json");

    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private AudioDeviceService? _devices;
    private CaptureMonitor? _monitor;
    private SwitchController? _controller;
    private SwitchHistory? _history;
    private TrayManager? _tray;
    private HotkeyManager? _hotkeys;
    private VoiceCommandService? _voice;
    private MainWindow? _window;
    private SetupWizard? _wizard;
    private UpdateService? _updates;
    private AppServices? _services;
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

        _settings = SettingsStore.Load();
        Loc.Apply(_settings.Language);

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
                Loc.T("App_AlreadyRunning"),
                "MicSwitcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Info($"Fatal error: {args.ExceptionObject}");
        // Tray-icon clicks run on WinForms' own handler, which would otherwise show a crash dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, args) => AppLog.Info($"Unexpected error: {args.Exception}");
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        AppTheme.Apply(_settings.Theme);
        _devices = new AudioDeviceService();
        _monitor = new CaptureMonitor();
        _history = SwitchHistory.Load(HistoryPath);
        _controller = new SwitchController(_settings, _devices, _monitor, _history);
        _updates = new UpdateService(_settings);

        _services = new AppServices
        {
            Settings = _settings,
            Controller = _controller,
            Devices = _devices,
            Updates = _updates,
            Persist = Persist,
            SetPaused = SetPaused,
            SwitchProfile = SwitchProfile,
            LanguageChanged = OnLanguageChanged,
            ApplyHotkeys = ApplyHotkeys,
            ApplyVoiceCommands = ApplyVoiceCommands,
            RerunSetup = ShowSetup,
        };

        _tray = new TrayManager(_settings, _controller, new TrayActions(
            Open: ShowWindow,
            SetPaused: SetPaused,
            Exit: ExitApplication,
            SwitchProfile: SwitchProfile,
            SwitchToMic: (id, span) => _controller.SwitchTo(id, "manual", span),
            ReleaseLock: _controller.ReleaseLock));

        _controller.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() =>
        {
            _tray?.Update(status);
            _window?.UpdateStatus(status);
        }));

        _controller.Switched += item =>
        {
            _history?.Save(HistoryPath);
            if (_settings.NotifyOnSwitch)
            {
                Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(Loc.F("Notify_Switched", item.ToName), item.Reason)));
            }
        };

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += binding => RunAction(binding.Action, binding.MicId, binding.ProfileId, "hotkey");
        _voice = new VoiceCommandService(_settings);
        _voice.CommandHeard += command => Dispatcher.BeginInvoke(new Action(() => OnVoiceCommand(command)));

        _controller.Start();
        ApplyHotkeys();
        if (_settings.VoiceCommandsEnabled)
        {
            _ = ApplyVoiceCommands();
        }

        // Check shortly after start, then every 12 hours; one small request, so the cost is negligible.
        _updateTimer = new Timer(_ => _ = BackgroundUpdateCheck(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));

        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AppLog.Info(elevated ? "MicSwitcher started (administrator)" : "MicSwitcher started");

        bool background = e.Args.Contains(StartupManager.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        if (!background)
        {
            if (_settings.FirstRunCompleted)
            {
                ShowWindow();
            }
            else
            {
                ShowSetup();
            }
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MemoryTrimmer.Trim));
        }
    }

    // ----- windows --------------------------------------------------------------------------------------------

    private void ShowWindow()
    {
        if (_services is null)
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
                    _window = new MainWindow(_services);
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
                        Loc.F("App_CannotOpen", ex.Message),
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
            broken?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not close the broken window: {ex.Message}");
        }
    }

    /// <summary>Shows the first-run setup (also used to run it again from the settings).</summary>
    private void ShowSetup()
    {
        if (_services is null)
        {
            return;
        }

        if (_wizard is not null)
        {
            _wizard.Activate();
            return;
        }

        try
        {
            DiscardWindow();
            _wizard = new SetupWizard(_services);
            _wizard.Closed += (_, _) =>
            {
                bool calibrate = _wizard?.CalibrateWhenDone == true;
                _wizard = null;
                _settings.FirstRunCompleted = true;
                Persist();
                ShowWindow();
                if (calibrate)
                {
                    _window?.StartCalibration();
                }
            };
            _wizard.Show();
            _wizard.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not open the setup: {ex}");
            _wizard = null;
            _settings.FirstRunCompleted = true;
            ShowWindow();
        }
    }

    private void OnLanguageChanged()
    {
        Loc.Apply(_settings.Language);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            bool wasOpen = _window is not null;
            DiscardWindow();
            if (wasOpen)
            {
                ShowWindow();
            }
        }));
    }

    // ----- actions (tray, hotkeys, voice) ---------------------------------------------------------------------

    private void RunAction(HotkeyAction action, string? micId, string? profileId, string source)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_controller is null)
            {
                return;
            }

            switch (action)
            {
                case HotkeyAction.SwitchToMic when micId is not null:
                    _controller.SwitchTo(micId, source, TimeSpan.MaxValue);
                    break;
                case HotkeyAction.NextMic:
                    _controller.SwitchToNext(source);
                    break;
                case HotkeyAction.TogglePause:
                    SetPaused(!_settings.Paused);
                    break;
                case HotkeyAction.ReleaseLock:
                    _controller.ReleaseLock();
                    break;
                case HotkeyAction.SwitchProfile when profileId is not null:
                    SwitchProfile(profileId);
                    break;
            }
        }));
    }

    private void OnVoiceCommand(VoiceCommand command)
    {
        AppLog.Info(Loc.F("Log_VoiceCommand", command.Kind));
        switch (command.Kind)
        {
            case VoiceCommandKind.SwitchToMic:
                RunAction(HotkeyAction.SwitchToMic, command.TargetId, null, "voice");
                break;
            case VoiceCommandKind.SwitchProfile:
                RunAction(HotkeyAction.SwitchProfile, null, command.TargetId, "voice");
                break;
            case VoiceCommandKind.NextMic:
                RunAction(HotkeyAction.NextMic, null, null, "voice");
                break;
            case VoiceCommandKind.ReleaseLock:
                RunAction(HotkeyAction.ReleaseLock, null, null, "voice");
                break;
            case VoiceCommandKind.Pause:
                SetPaused(true);
                break;
            case VoiceCommandKind.Resume:
                SetPaused(false);
                break;
        }
    }

    private IReadOnlyList<string> ApplyHotkeys() => _hotkeys?.Register(_settings.Hotkeys) ?? Array.Empty<string>();

    private string? ApplyVoiceCommands()
    {
        if (_voice is null)
        {
            return null;
        }

        return _settings.VoiceCommandsEnabled ? _voice.Start() : StopVoice();
    }

    private string? StopVoice()
    {
        _voice?.Stop();
        return null;
    }

    private void SwitchProfile(string profileId)
    {
        if (_settings.Profiles.All(p => p.Id != profileId) || _settings.ActiveProfileId == profileId)
        {
            return;
        }

        _settings.ActiveProfileId = profileId;
        Persist();
        AppLog.Info(Loc.F("Log_ProfileChanged", _settings.ActiveProfile.Name));
        if (_settings.NotifyOnSwitch)
        {
            _tray?.Notify(Loc.T("Notify_ProfileTitle"), _settings.ActiveProfile.Name);
        }

        _window?.RefreshAll();
    }

    // ----- background work ------------------------------------------------------------------------------------

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
                _voice?.Stop();
                _controller?.Suspend();
                Dispatcher.BeginInvoke(new Action(DiscardWindow));
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                _controller?.Resume();
                ApplyHotkeys();
                if (_settings.VoiceCommandsEnabled)
                {
                    _ = ApplyVoiceCommands();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"Power change handling failed: {ex.Message}");
        }
    }

    /// <summary>Saves settings and tells the controller, tray and hotkeys to pick them up.</summary>
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
        _window?.SyncPaused();
        AppLog.Info(Loc.T(paused ? "Log_Paused" : "Log_Resumed"));
    }

    private void ExitApplication()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _updateTimer?.Dispose();
        _voice?.Dispose();
        _hotkeys?.Dispose();
        _history?.Save(HistoryPath);
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
