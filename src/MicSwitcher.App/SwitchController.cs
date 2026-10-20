using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>What the UI and tray need to know about the current state.</summary>
public sealed record ControllerStatus(
    MicRole? ActiveRole,
    bool ProPresent,
    bool HeadsetPresent,
    bool LaptopPresent,
    bool ProVoice,
    bool Paused,
    string Message);

/// <summary>
/// Glue between the pure <see cref="SwitchingEngine"/> and Windows: refreshes devices once per second,
/// feeds audio levels into the voice detector, asks the engine for a decision ten times per second and applies it.
/// </summary>
public sealed class SwitchController : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly AppSettings _settings;
    private readonly AudioDeviceService _devices;
    private readonly MicMonitor _monitor;
    private readonly object _sync = new();

    // Separate lock so capture threads never wait on the (slower) device/engine lock; avoids a stop/start deadlock.
    private readonly object _detectorLock = new();
    private readonly EnergyVoiceDetector _detector = new();

    private SwitchingEngine _engine;
    private Timer? _timer;
    private int _ticking;
    private DateTime _lastRefresh = DateTime.MinValue;
    private HashSet<string> _activeIds = new();
    private MicRole? _currentDefault;
    private bool _metersWanted;
    private ControllerStatus? _lastStatus;
    private string _lastMessage = "Starting…";

    public SwitchController(AppSettings settings, AudioDeviceService devices, MicMonitor monitor)
    {
        _settings = settings;
        _devices = devices;
        _monitor = monitor;
        _engine = new SwitchingEngine(settings.ToEngineOptions());
        ApplyDetectorSettings();
    }

    /// <summary>Raised from a background thread whenever the visible state changes.</summary>
    public event Action<ControllerStatus>? StatusChanged;

    /// <summary>Raised from a capture thread for every audio block of every monitored mic (role, dBFS).</summary>
    public event Action<MicRole, double>? LevelMeasured;

    public ControllerStatus? LastStatus => _lastStatus;

    public void Start()
    {
        _monitor.BlockMeasured += OnBlock;
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TickInterval);
    }

    /// <summary>Call after the settings object was modified (devices, thresholds, timings, pause).</summary>
    public void ReloadSettings()
    {
        lock (_sync)
        {
            _engine = new SwitchingEngine(_settings.ToEngineOptions());
            ApplyDetectorSettings();
            _lastRefresh = DateTime.MinValue; // force a device refresh on the next tick
        }
    }

    /// <summary>Headset and laptop levels are only captured while the settings window is open.</summary>
    public void SetMetersWanted(bool wanted)
    {
        lock (_sync)
        {
            _metersWanted = wanted;
            _lastRefresh = DateTime.MinValue;
        }
    }

    /// <summary>Immediately makes <paramref name="role"/> the Windows default (manual override).</summary>
    public void UseNow(MicRole role)
    {
        lock (_sync)
        {
            string? id = _settings.Get(role).DeviceId;
            if (id is null || !_activeIds.Contains(id))
            {
                AppLog.Info($"Cannot use the {role} mic: it is not set or not connected.");
                return;
            }

            Apply(role, "manual selection");
        }
    }

    private void ApplyDetectorSettings()
    {
        lock (_detectorLock)
        {
            _detector.ThresholdDb = _settings.VoiceThresholdDb;
            _detector.OnsetMs = _settings.VoiceOnsetMs;
            _detector.HangoverMs = _settings.VoiceHangoverMs;
        }
    }

    private void OnBlock(MicRole role, double levelDb, double blockMs)
    {
        if (role == MicRole.Pro)
        {
            lock (_detectorLock)
            {
                _detector.Process(levelDb, blockMs);
            }
        }

        LevelMeasured?.Invoke(role, levelDb);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            DateTime now = DateTime.UtcNow;
            string? decisionMessage = null;
            ControllerStatus status;

            lock (_sync)
            {
                if (now - _lastRefresh >= DeviceRefreshInterval)
                {
                    RefreshDevices();
                    _lastRefresh = now;
                }

                MicState pro = StateOf(MicRole.Pro, _detector.IsVoice);
                MicState headset = StateOf(MicRole.Headset, false);
                MicState laptop = StateOf(MicRole.Laptop, false);

                if (!_settings.Paused)
                {
                    SwitchDecision decision = _engine.Update(now, _currentDefault, pro, headset, laptop);
                    if (decision.Target is MicRole target)
                    {
                        if (Apply(target, decision.Reason))
                        {
                            decisionMessage = decision.Reason;
                        }
                    }
                    else if (_currentDefault is null)
                    {
                        decisionMessage = "No managed microphone is available";
                    }
                }

                if (decisionMessage is not null)
                {
                    _lastMessage = decisionMessage;
                }

                status = new ControllerStatus(
                    _currentDefault, pro.Present, headset.Present, laptop.Present, pro.Voice,
                    _settings.Paused, _settings.Paused ? "Automatic switching is paused" : _lastMessage);
            }

            if (!status.Equals(_lastStatus))
            {
                _lastStatus = status;
                StatusChanged?.Invoke(status);
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"Unexpected error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private MicState StateOf(MicRole role, bool voice)
    {
        string? id = _settings.Get(role).DeviceId;
        return new MicState(id is not null && _activeIds.Contains(id), voice);
    }

    /// <summary>Reads the device list and the Windows default, and keeps the capture streams in sync.</summary>
    private void RefreshDevices()
    {
        try
        {
            _activeIds = _devices.GetActiveCaptureDevices().Select(d => d.Id).ToHashSet();
            string? defaultId = _devices.GetDefaultCaptureId();

            _currentDefault = null;
            foreach (MicRole role in Enum.GetValues<MicRole>())
            {
                string? id = _settings.Get(role).DeviceId;
                if (id is not null && id == defaultId)
                {
                    _currentDefault = role;
                    break;
                }
            }

            _monitor.Ensure(MicRole.Pro, PresentIdOrNull(MicRole.Pro));
            _monitor.Ensure(MicRole.Headset, _metersWanted ? PresentIdOrNull(MicRole.Headset) : null);
            _monitor.Ensure(MicRole.Laptop, _metersWanted ? PresentIdOrNull(MicRole.Laptop) : null);

            if (PresentIdOrNull(MicRole.Pro) is null)
            {
                lock (_detectorLock)
                {
                    _detector.Reset();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not read audio devices: {ex.Message}");
        }
    }

    private string? PresentIdOrNull(MicRole role)
    {
        string? id = _settings.Get(role).DeviceId;
        return id is not null && _activeIds.Contains(id) ? id : null;
    }

    private bool Apply(MicRole role, string reason)
    {
        string? id = _settings.Get(role).DeviceId;
        if (id is null)
        {
            return false;
        }

        try
        {
            _devices.SetDefaultCapture(id, _settings.SwitchCommunicationsRole);
            _currentDefault = role;
            string name = _settings.Get(role).DeviceName ?? role.ToString();
            AppLog.Info($"Default microphone → {role} ({name}): {reason}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Failed to switch to the {role} mic: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _monitor.BlockMeasured -= OnBlock;
    }
}
