using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using MicSwitcher.Core;
using NAudio.Wave;

namespace MicSwitcher.App.Audio;

public enum CaptureKind
{
    /// <summary>A microphone (capture device).</summary>
    Microphone,

    /// <summary>What a playback device (speakers, headphones) is currently playing.</summary>
    Loopback,
}

/// <summary>
/// Keeps one shared-mode capture stream open per monitored source and reports the level of every ~25 ms of audio.
/// Reading a mic that is not the Windows default is fine in shared mode.
/// </summary>
/// <remarks>
/// To save CPU the streams are polled with a large buffer (about ten wake-ups per second) instead of being woken for
/// every 10 ms of audio; each buffer is then split into short chunks so the level resolution stays fine.
/// </remarks>
public sealed class CaptureMonitor : IDisposable
{
    private const int MicBufferMs = 160;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private sealed class Session
    {
        public required string DeviceId { get; init; }
        public required CaptureKind Kind { get; init; }
        public required MMDevice Device { get; init; }
        public required WasapiCapture Capture { get; init; }

        public volatile bool Stopped;
        public volatile bool Unsupported;
        public bool FormatKnown;
        public SampleFormat Format;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly Dictionary<string, (string DeviceId, DateTime At)> _failures = new();

    /// <summary>Raised on a capture thread: source key, level in dBFS, chunk length in ms, whether it sounds like speech.</summary>
    public event Action<string, double, double, bool>? BlockMeasured;

    /// <summary>When true each chunk of a microphone is also checked for a speech-like spectrum (a little more CPU).</summary>
    public volatile bool SpeechAnalysis;

    [ThreadStatic] private static float[]? _monoScratch;

    /// <summary>
    /// Makes sure <paramref name="key"/> is being captured from <paramref name="deviceId"/>. Passing null stops that
    /// source. Safe to call repeatedly.
    /// </summary>
    public void Ensure(string key, string? deviceId, CaptureKind kind)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out Session? existing))
            {
                if (deviceId == existing.DeviceId && kind == existing.Kind && !existing.Stopped)
                {
                    return;
                }

                StopLocked(key);
            }

            if (deviceId is null)
            {
                _failures.Remove(key);
                return;
            }

            if (_failures.TryGetValue(key, out var failure)
                && failure.DeviceId == deviceId
                && DateTime.UtcNow - failure.At < RetryDelay)
            {
                return;
            }

            try
            {
                StartLocked(key, deviceId, kind);
                _failures.Remove(key);
            }
            catch (Exception ex)
            {
                bool firstTime = !_failures.TryGetValue(key, out var previous) || previous.DeviceId != deviceId;
                _failures[key] = (deviceId, DateTime.UtcNow);
                if (firstTime)
                {
                    AppLog.Info($"Could not listen to \"{key}\": {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The output volume (dB) of the playback device behind a loopback source, or -100 when it is muted. Loopback
    /// audio is captured before the volume is applied, so this is needed to know how loud the sound really is.
    /// </summary>
    public double? GetRenderVolumeDb(string key)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out Session? session) || session.Kind != CaptureKind.Loopback)
            {
                return null;
            }

            try
            {
                AudioEndpointVolume volume = session.Device.AudioEndpointVolume;
                return volume.Mute ? -100 : volume.MasterVolumeLevel;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Stops everything (used while the computer sleeps or switching is paused).</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            foreach (string key in new List<string>(_sessions.Keys))
            {
                StopLocked(key);
            }

            _failures.Clear();
        }
    }

    /// <summary>Forgets earlier failures so sources are retried immediately (e.g. after waking from sleep).</summary>
    public void ResetFailures()
    {
        lock (_gate)
        {
            _failures.Clear();
        }
    }

    private void StartLocked(string key, string deviceId, CaptureKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = enumerator.GetDevice(deviceId);

        WasapiCapture capture;
        try
        {
            capture = kind == CaptureKind.Loopback
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device, false, MicBufferMs);
        }
        catch
        {
            device.Dispose();
            throw;
        }

        var session = new Session { DeviceId = deviceId, Kind = kind, Device = device, Capture = capture };

        capture.DataAvailable += (_, e) => OnData(key, session, e);
        capture.RecordingStopped += (_, e) =>
        {
            session.Stopped = true;
            if (e.Exception is not null)
            {
                AppLog.Info($"Listening to \"{key}\" stopped: {e.Exception.Message}");
            }
        };

        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.Dispose();
            device.Dispose();
            throw;
        }

        _sessions[key] = session;
    }

    private void OnData(string key, Session session, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || session.Unsupported)
        {
            return;
        }

        if (!session.FormatKnown)
        {
            if (!LevelMeter.TryDescribe(session.Capture.WaveFormat, out SampleFormat described))
            {
                session.Unsupported = true;
                AppLog.Info($"\"{key}\" uses an audio format MicSwitcher cannot read.");
                return;
            }

            session.Format = described;
            session.FormatKnown = true;
        }

        Action<string, double, double, bool>? handler = BlockMeasured;
        if (handler is null)
        {
            return;
        }

        SampleFormat f = session.Format;
        int chunkBytes = Math.Max(1, f.SampleRate / 40) * f.BlockAlign; // about 25 ms
        int total = e.BytesRecorded - (e.BytesRecorded % f.BlockAlign);
        double bytesPerMs = (double)f.SampleRate * f.BlockAlign / 1000.0;

        for (int offset = 0; offset < total; offset += chunkBytes)
        {
            int count = Math.Min(chunkBytes, total - offset);
            double db = LevelMeter.ComputeDb(e.Buffer, offset, count, f);
            bool speechLike = true;
            if (SpeechAnalysis && session.Kind == CaptureKind.Microphone && db > LevelMeter.FloorDb)
            {
                float[] mono = _monoScratch ??= new float[2048];
                int frames = LevelMeter.ToMono(e.Buffer, offset, count, f, mono);
                speechLike = SpeechAnalyzer.LooksLikeSpeech(mono.AsSpan(0, frames), f.SampleRate);
            }

            handler(key, db, count / bytesPerMs, speechLike);
        }
    }

    private void StopLocked(string key)
    {
        if (!_sessions.Remove(key, out Session? session))
        {
            return;
        }

        try
        {
            session.Capture.StopRecording();
        }
        catch (Exception)
        {
            // Already stopped or the device is gone.
        }

        try
        {
            session.Capture.Dispose();
            session.Device.Dispose();
        }
        catch (Exception)
        {
            // Nothing more to release.
        }
    }

    public void Dispose() => StopAll();
}
