using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>State of one managed mic, for the UI.</summary>
public sealed record MicStatus(string Id, bool Present, bool Voice, bool Noise = false);

/// <summary>What the UI and tray need to know about the current state.</summary>
public sealed class ControllerStatus
{
    public ControllerStatus(
        string? activeMicId,
        IReadOnlyList<MicStatus> mics,
        bool paused,
        string message,
        string? lockedMicId,
        DateTime? lockUntil)
    {
        ActiveMicId = activeMicId;
        Mics = mics;
        Paused = paused;
        Message = message;
        LockedMicId = lockedMicId;
        LockUntil = lockUntil;
        Signature = string.Join('|', mics.Select(m => $"{m.Id}:{(m.Present ? 1 : 0)}{(m.Voice ? 1 : 0)}{(m.Noise ? 1 : 0)}"))
            + $"#{activeMicId}#{paused}#{message}#{lockedMicId}#{lockUntil?.Ticks}";
    }

    public string? ActiveMicId { get; }
    public IReadOnlyList<MicStatus> Mics { get; }
    public bool Paused { get; }
    public string Message { get; }
    public string? LockedMicId { get; }
    public DateTime? LockUntil { get; }

    /// <summary>Changes whenever anything shown to the user changes; used to skip redundant UI updates.</summary>
    public string Signature { get; }

    public MicStatus? Find(string id) => Mics.FirstOrDefault(m => m.Id == id);
}

/// <summary>
/// Glue between the pure <see cref="MicEngine"/> and Windows: refreshes devices once per second, feeds audio levels into
/// the voice detectors, asks the engine for a decision ten times per second and applies it.
/// </summary>
public sealed class SwitchController : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(1);
    private const string PlaybackKey = "playback";

    private sealed class MicRuntime
    {
        public readonly EnergyVoiceDetector Detector = new();
        public readonly SpeechGate Gate = new();
        public readonly BleedEstimator Bleed = new();
        public double PeakDb = LevelMeter.FloorDb;
        public double ThresholdDb = -45;
        public long NoiseAtMs = -10_000;
    }

    private readonly AppSettings _settings;
    private readonly AudioDeviceService _devices;
    private readonly CaptureMonitor _monitor;
    private readonly SwitchHistory _history;
    private readonly object _sync = new();

    // Separate lock so capture threads never wait on the (slower) device/engine lock; avoids a stop/start deadlock.
    private readonly object _detectorLock = new();
    private readonly Dictionary<string, MicRuntime> _runtime = new();
    private readonly PlaybackTracker _playback = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _renderVolumeDb;
    private volatile bool _suspended;

    private MicEngine _engine;
    private Timer? _timer;
    private int _ticking;
    private DateTime _lastRefresh = DateTime.MinValue;
    private HashSet<string> _activeDeviceIds = new();
    private string? _currentDefaultDeviceId;
    private bool _metersWanted;
    private ControllerStatus? _lastStatus;
    private string _lastMessage = string.Empty;
    private string? _lockedMicId;
    private DateTime? _lockUntil;
    private string? _lastAppliedProfileId;

    public SwitchController(AppSettings settings, AudioDeviceService devices, CaptureMonitor monitor, SwitchHistory history)
    {
        _settings = settings;
        _devices = devices;
        _monitor = monitor;
        _history = history;
        _engine = new MicEngine(settings.ToEngineOptions());
        _monitor.BlockMeasured += OnBlock;
        _lastMessage = Loc.T("Status_Starting");
    }

    /// <summary>Raised when what the user sees changes.</summary>
    public event Action<ControllerStatus>? StatusChanged;

    /// <summary>Raised on a capture thread for every measured block of a mic: mic id, level in dBFS.</summary>
    public event Action<string, double>? LevelMeasured;

    /// <summary>Raised after the Windows default mic was changed by the app.</summary>
    public event Action<SwitchEvent>? Switched;

    public ControllerStatus? LastStatus => _lastStatus;

    public SwitchHistory History => _history;

    public bool IsLocked => _lockedMicId is not null;
    public string? LockedMicId => _lockedMicId;

    /// <summary>Playback level (dBFS, after volume) heard over the last moments; -100 when silent. For the UI.</summary>
    public double PlaybackDb => _playback.RecentLevelDb(_clock.ElapsedMilliseconds);

    /// <summary>The bleed coupling currently in effect for a mic (learned, capped by the calibrated value).</summary>
    public double? CouplingFor(string micId)
    {
        lock (_detectorLock)
        {
            return _runtime.TryGetValue(micId, out MicRuntime? rt)
                ? BleedGuard.ChooseCoupling(rt.Bleed.CouplingDb, _settings.FindMic(micId)?.BleedCouplingDb)
                : null;
        }
    }

    public void Start()
    {
        ReloadSettings();
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TickInterval);
    }

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

    /// <summary>Call after the settings (mics, profile, timings…) changed.</summary>
    public void ReloadSettings()
    {
        lock (_sync)
        {
            _engine = new MicEngine(_settings.ToEngineOptions());
            _monitor.SpeechAnalysis = _settings.ActiveProfile.SmartVoice;
            _lastRefresh = DateTime.MinValue;

            lock (_detectorLock)
            {
                var ids = _settings.Mics.Select(m => m.Id).ToHashSet();
                foreach (string stale in _runtime.Keys.Where(k => !ids.Contains(k)).ToList())
                {
                    _runtime.Remove(stale);
                }

                foreach (MicSlot mic in _settings.Mics)
                {
                    if (!_runtime.TryGetValue(mic.Id, out MicRuntime? rt))
                    {
                        _runtime[mic.Id] = rt = new MicRuntime();
                    }

                    rt.Detector.ThresholdDb = mic.VoiceThresholdDb;
                    rt.Detector.OnsetMs = _settings.VoiceOnsetMs;
                    rt.Detector.HangoverMs = _settings.VoiceHangoverMs;
                    rt.ThresholdDb = mic.VoiceThresholdDb;
                    if (_lastAppliedProfileId != _settings.ActiveProfileId)
                    {
                        rt.Detector.Reset();
                        rt.Gate.Reset();
                    }
                }
            }

            _lastAppliedProfileId = _settings.ActiveProfileId;
        }
    }

    /// <summary>When the settings window is open all mics are metered so their levels can be shown.</summary>
    public void SetMetersWanted(bool wanted)
    {
        lock (_sync)
        {
            if (_metersWanted == wanted)
            {
                return;
            }

            _metersWanted = wanted;
            _lastRefresh = DateTime.MinValue;
        }
    }

    /// <summary>Makes a mic the Windows default right now. With <paramref name="lockFor"/> automatic switching stays off.</summary>
    /// <param name="lockFor">null = no lock; <see cref="TimeSpan.MaxValue"/> = until released.</param>
    public bool SwitchTo(string micId, string source, TimeSpan? lockFor = null)
    {
        lock (_sync)
        {
            MicSlot? mic = _settings.FindMic(micId);
            if (mic?.DeviceId is null)
            {
                return false;
            }

            RefreshDevices(force: true);
            if (!_activeDeviceIds.Contains(mic.DeviceId))
            {
                AppLog.Info(Loc.F("Log_NotConnected", _settings.MicName(micId)));
                return false;
            }

            if (lockFor is TimeSpan span)
            {
                _lockedMicId = micId;
                _lockUntil = span == TimeSpan.MaxValue ? null : DateTime.UtcNow + span;
            }

            Apply(mic, Loc.T("Reason_Manual"), source);
            return true;
        }
    }

    /// <summary>Goes back to automatic switching after a lock.</summary>
    public void ReleaseLock()
    {
        lock (_sync)
        {
            if (_lockedMicId is null)
            {
                return;
            }

            _lockedMicId = null;
            _lockUntil = null;
            AppLog.Info(Loc.T("Log_LockReleased"));
        }
    }

    /// <summary>Switches to the next mic in the list that is connected (wraps around). Locks until released.</summary>
    public void SwitchToNext(string source)
    {
        string? target = null;
        lock (_sync)
        {
            RefreshDevices(force: true);
            List<MicSlot> usable = _settings.Mics
                .Where(m => m.DeviceId is not null && _activeDeviceIds.Contains(m.DeviceId))
                .ToList();
            if (usable.Count == 0)
            {
                return;
            }

            int current = usable.FindIndex(m => m.DeviceId == _currentDefaultDeviceId);
            target = usable[(current + 1) % usable.Count].Id;
        }

        SwitchTo(target, source, TimeSpan.MaxValue);
    }

    private void OnBlock(string key, double levelDb, double blockMs, bool speechLike)
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

        lock (_detectorLock)
        {
            if (!_runtime.TryGetValue(key, out MicRuntime? rt))
            {
                return;
            }

            Profile profile = _settings.ActiveProfile;
            double threshold = rt.Detector.ThresholdDb;
            double effective = threshold;
            if (profile.IgnoreBleed)
            {
                double playbackDb = _playback.RecentLevelDb(_clock.ElapsedMilliseconds);
                rt.Bleed.Observe(levelDb, playbackDb, blockMs);
                double? coupling = BleedGuard.ChooseCoupling(rt.Bleed.CouplingDb, _settings.FindMic(key)?.BleedCouplingDb);
                effective = BleedGuard.EffectiveThresholdDb(threshold, playbackDb, coupling, _settings.BleedMarginDb);
            }

            rt.ThresholdDb = effective;
            double measured = levelDb;
            if (profile.SmartVoice && !rt.Gate.Process(speechLike, blockMs))
            {
                measured = LevelMeter.FloorDb; // loud but not speech-shaped: treat as silence for the detector
                if (levelDb >= effective)
                {
                    rt.NoiseAtMs = _clock.ElapsedMilliseconds;
                }
            }

            // The detector's own threshold is the effective one for this block.
            double saved = rt.Detector.ThresholdDb;
            rt.Detector.ThresholdDb = effective;
            rt.Detector.Process(measured, blockMs);
            rt.Detector.ThresholdDb = saved;

            rt.PeakDb = Math.Max(levelDb, rt.PeakDb - 0.04 * blockMs);
        }

        LevelMeasured?.Invoke(key, levelDb);
    }

    private void Tick()
    {
        if (_suspended || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                Evaluate();
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

    private void Evaluate()
    {
        RefreshDevices(force: false);
        Profile profile = _settings.ActiveProfile;

        bool Present(MicSlot m) => m.DeviceId is not null && _activeDeviceIds.Contains(m.DeviceId);
        bool Excluded(MicSlot m) => profile.ExcludedMicIds.Contains(m.Id);

        string? forced = _lockedMicId ?? profile.ForcedMicId;
        bool needsVoice = profile.ForcedMicId is null && _lockedMicId is null && _settings.ToEngineOptions().Mode != SwitchMode.Manual;

        // Which sources to listen to: the voice-driven engine needs the normal mics; the window wants everything.
        foreach (MicSlot mic in _settings.Mics)
        {
            bool listen = Present(mic) && !_suspended && (_metersWanted || (needsVoice && !Excluded(mic) && !mic.Fallback));
            _monitor.Ensure(mic.Id, listen ? mic.DeviceId : null, CaptureKind.Microphone);
        }

        // What the speakers/headphones are playing, so it is not mistaken for someone talking.
        _monitor.Ensure(PlaybackKey, profile.IgnoreBleed && (needsVoice || _metersWanted) ? _devices.GetDefaultRenderId() : null, CaptureKind.Loopback);
        _renderVolumeDb = _monitor.GetRenderVolumeDb(PlaybackKey) ?? 0;

        if (_lockUntil is DateTime until && DateTime.UtcNow >= until)
        {
            _lockedMicId = null;
            _lockUntil = null;
            forced = profile.ForcedMicId;
            AppLog.Info(Loc.T("Log_LockExpired"));
        }

        MicSlot? current = _settings.Mics.FirstOrDefault(m => m.DeviceId is not null && m.DeviceId == _currentDefaultDeviceId);
        string message = _lastMessage;

        if (!_settings.Paused)
        {
            MicSlot? forcedMic = _settings.FindMic(forced);
            if (forced is not null && forcedMic is not null && Present(forcedMic))
            {
                if (current?.Id != forcedMic.Id)
                {
                    Apply(forcedMic, Loc.T(_lockedMicId is not null ? "Reason_Locked" : "Reason_Forced"), "auto");
                    current = forcedMic;
                }

                message = Loc.F("Status_Locked", _settings.MicName(forcedMic.Id));
            }
            else
            {
                if (_lockedMicId is not null)
                {
                    // The locked mic is gone: fall back to automatic switching.
                    _lockedMicId = null;
                    _lockUntil = null;
                }

                MicInput[] inputs;
                lock (_detectorLock)
                {
                    inputs = _settings.Mics
                        .Select((m, index) => (m, index))
                        .Where(x => !Excluded(x.m))
                        .Select(x => new MicInput(
                            x.m.Id,
                            x.m.DisplayName(x.index),
                            Present(x.m),
                            _runtime.TryGetValue(x.m.Id, out MicRuntime? rt) && rt.Detector.IsVoice,
                            _runtime.TryGetValue(x.m.Id, out MicRuntime? r2) ? r2.PeakDb - r2.ThresholdDb : 0,
                            x.m.Fallback))
                        .ToArray();
                }

                SwitchDecision decision = _engine.Update(DateTime.UtcNow, current?.Id, inputs);
                if (decision.TargetId is not null && _settings.FindMic(decision.TargetId) is MicSlot target)
                {
                    Apply(target, decision.Reason, "auto");
                    current = target;
                }

                message = current is null
                    ? Loc.T(inputs.Any(i => i.Present) ? "Status_NoDefault" : "Status_NoMics")
                    : Loc.F("Status_Using", _settings.MicName(current.Id));
            }
        }
        else
        {
            message = Loc.T("Status_Paused");
        }

        _lastMessage = message;
        Publish(current?.Id, message);
    }

    private void Publish(string? activeId, string message)
    {
        MicStatus[] mics;
        lock (_detectorLock)
        {
            mics = _settings.Mics
                .Select(m => new MicStatus(
                    m.Id,
                    m.DeviceId is not null && _activeDeviceIds.Contains(m.DeviceId),
                    _runtime.TryGetValue(m.Id, out MicRuntime? rt) && rt.Detector.IsVoice,
                    rt is not null && _clock.ElapsedMilliseconds - rt.NoiseAtMs < 600))
                .ToArray();
        }

        var status = new ControllerStatus(activeId, mics, _settings.Paused, message, _lockedMicId, _lockUntil);
        if (_lastStatus?.Signature == status.Signature)
        {
            return;
        }

        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private void RefreshDevices(bool force)
    {
        if (!force && DateTime.UtcNow - _lastRefresh < DeviceRefreshInterval)
        {
            return;
        }

        _lastRefresh = DateTime.UtcNow;
        try
        {
            _activeDeviceIds = _devices.GetActiveCaptureIds().ToHashSet();
            _currentDefaultDeviceId = _devices.GetDefaultCaptureId();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not read the audio devices: {ex.Message}");
        }
    }

    private void Apply(MicSlot mic, string reason, string source)
    {
        if (mic.DeviceId is null)
        {
            return;
        }

        try
        {
            _devices.SetDefaultCapture(mic.DeviceId, _settings.SwitchCommunicationsRole);
        }
        catch (Exception ex)
        {
            AppLog.Info(Loc.F("Log_SwitchFailed", _settings.MicName(mic.Id), ex.Message));
            return;
        }

        _currentDefaultDeviceId = mic.DeviceId;
        ApplyOutput(mic);

        string name = _settings.MicName(mic.Id);
        AppLog.Info(Loc.F("Log_Switched", name, reason));
        var item = new SwitchEvent(DateTime.UtcNow, mic.Id, name, reason, source);
        _history.Add(item);
        Switched?.Invoke(item);
    }

    /// <summary>Optionally moves playback to the device (and volume) the user attached to this mic.</summary>
    private void ApplyOutput(MicSlot mic)
    {
        try
        {
            if (!string.IsNullOrEmpty(mic.OutputDeviceId))
            {
                _devices.SetDefaultRender(mic.OutputDeviceId, _settings.SwitchCommunicationsRole);
            }

            if (mic.OutputVolumePercent is int percent)
            {
                string? target = !string.IsNullOrEmpty(mic.OutputDeviceId) ? mic.OutputDeviceId : _devices.GetDefaultRenderId();
                if (target is not null)
                {
                    _devices.SetVolumePercent(target, percent);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not change the output: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _monitor.BlockMeasured -= OnBlock;
        _timer?.Dispose();
        _monitor.Dispose();
    }
}
