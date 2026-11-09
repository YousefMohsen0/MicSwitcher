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
    bool HeadsetVoice,
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
    private readonly CaptureMonitor _monitor;
    private readonly object _sync = new();

    // Separate lock so capture threads never wait on the (slower) device/engine lock; avoids a stop/start deadlock.
    private readonly object _detectorLock = new();
    private readonly EnergyVoiceDetector _detector = new();
    private readonly EnergyVoiceDetector _headsetDetector = new();
    private readonly PlaybackTracker _playback = new();
    private readonly BleedEstimator _proBleed = new();
    private readonly BleedEstimator _headsetBleed = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _renderVolumeDb;
    private volatile bool _suspended;

    private SwitchingEngine _engine;
    private Timer? _timer;
    private int _ticking;
    private DateTime _lastRefresh = DateTime.MinValue;
    private HashSet<string> _activeIds = new();
    private MicRole? _currentDefault;
    private bool _metersWanted;
    private ControllerStatus? _lastStatus;
    private string _lastMessage = "Starting…";

    public SwitchController(AppSettings settings, AudioDeviceService devices, CaptureMonitor monitor)
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

    /// <summary>Playback level (dBFS, after volume) heard over the last moments; -100 when silent. For the UI.</summary>
    public double PlaybackDb => _playback.RecentLevelDb(_clock.ElapsedMilliseconds);

    /// <summary>The bleed coupling currently in effect for a mic (learned, capped by the calibrated value).</summary>
    public double? CouplingFor(MicRole role) => role switch
    {
        MicRole.Pro => BleedGuard.ChooseCoupling(_proBleed.CouplingDb, _settings.ProBleedCouplingDb),
        MicRole.Headset => BleedGuard.ChooseCoupling(_headsetBleed.CouplingDb, _settings.HeadsetBleedCouplingDb),
        _ => null,
    };

    /// <summary>Stops listening and switching (computer going to sleep). <see cref="Resume"/> undoes it.</summary>
    public void Suspend()
    {
        _suspended = true;
        _monitor.StopAll();
    }

    public void Resume()
    {
        lock (_sync)
        {
            _suspended = false;
            _monitor.ResetFailures();
            _lastRefresh = DateTime.MinValue;
        }
    }

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

            _headsetDetector.ThresholdDb = _settings.HeadsetVoiceThresholdDb;
            _headsetDetector.OnsetMs = _settings.VoiceOnsetMs;
            _headsetDetector.HangoverMs = _settings.VoiceHangoverMs;
        }
    }

    private const string PlaybackKey = "playback";

    private void OnBlock(string key, double levelDb, double blockMs)
    {
        if (key == PlaybackKey)
        {
            // Loopback is captured before the output volume is applied; add it back to get the real loudness.
            double heard = levelDb <= LevelMeter.FloorDb ? PlaybackTracker.SilenceDb : levelDb + Math.Min(0, _renderVolumeDb);
            lock (_detectorLock)
            {
                _playback.Observe(heard, _clock.ElapsedMilliseconds);
            }

            return;
        }

        if (!Enum.TryParse(key, out MicRole role))
        {
            return;
        }

        if (role == MicRole.Pro || role == MicRole.Headset)
        {
            lock (_detectorLock)
            {
                bool isPro = role == MicRole.Pro;
                EnergyVoiceDetector detector = isPro ? _detector : _headsetDetector;
                double userThreshold = isPro ? _settings.VoiceThresholdDb : _settings.HeadsetVoiceThresholdDb;

                if (_settings.IgnoreBleed)
                {
                    double playbackDb = _playback.RecentLevelDb(_clock.ElapsedMilliseconds);
                    BleedEstimator estimator = isPro ? _proBleed : _headsetBleed;
                    estimator.Observe(levelDb, playbackDb, blockMs);

                    double? coupling = BleedGuard.ChooseCoupling(
                        estimator.CouplingDb, isPro ? _settings.ProBleedCouplingDb : _settings.HeadsetBleedCouplingDb);
                    detector.ThresholdDb = BleedGuard.EffectiveThresholdDb(
                        userThreshold, playbackDb, coupling, _settings.BleedMarginDb);
                }
                else
                {
                    detector.ThresholdDb = userThreshold;
                }

                detector.Process(levelDb, blockMs);
            }
        }

        LevelMeasured?.Invoke(role, levelDb);
    }

    private void Tick()
    {
        if (_suspended || Interlocked.Exchange(ref _ticking, 1) == 1)
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
                MicState headset = StateOf(MicRole.Headset, _headsetDetector.IsVoice);
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
                    _currentDefault, pro.Present, headset.Present, laptop.Present, pro.Voice, headset.Voice,
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
            _activeIds = _devices.GetActiveCaptureIds().ToHashSet();
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

            _monitor.Ensure(nameof(MicRole.Pro), PresentIdOrNull(MicRole.Pro), CaptureKind.Microphone);
            // The headset mic is listened to continuously: the switching rule needs to know when a voice is on it.
            _monitor.Ensure(nameof(MicRole.Headset), PresentIdOrNull(MicRole.Headset), CaptureKind.Microphone);
            _monitor.Ensure(nameof(MicRole.Laptop), _metersWanted ? PresentIdOrNull(MicRole.Laptop) : null, CaptureKind.Microphone);

            // What the speakers/headphones are playing, so it is not mistaken for someone talking.
            _monitor.Ensure(PlaybackKey, _settings.IgnoreBleed ? _devices.GetDefaultRenderId() : null, CaptureKind.Loopback);
            _renderVolumeDb = _monitor.GetRenderVolumeDb(PlaybackKey) ?? 0;

            lock (_detectorLock)
            {
                if (PresentIdOrNull(MicRole.Pro) is null)
                {
                    _detector.Reset();
                    _proBleed.Reset();
                }

                if (PresentIdOrNull(MicRole.Headset) is null)
                {
                    _headsetDetector.Reset();
                    _headsetBleed.Reset();
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
