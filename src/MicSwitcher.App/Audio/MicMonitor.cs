using System;
using System.Collections.Generic;
using MicSwitcher.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicSwitcher.App.Audio;

/// <summary>
/// Keeps one shared-mode capture stream open per monitored mic and reports the level of every audio block.
/// Reading a mic that is not the Windows default is fine in shared mode, so the pro mic can be listened to
/// even while the headset mic is the default.
/// </summary>
public sealed class MicMonitor : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private sealed class Session
    {
        public required string DeviceId { get; init; }
        public required MMDevice Device { get; init; }
        public required WasapiCapture Capture { get; init; }
        public bool Stopped { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<MicRole, Session> _sessions = new();
    private readonly Dictionary<MicRole, (string DeviceId, DateTime At)> _failures = new();

    /// <summary>Raised on a capture thread: role, level in dBFS, block length in milliseconds.</summary>
    public event Action<MicRole, double, double>? BlockMeasured;

    /// <summary>
    /// Makes sure <paramref name="role"/> is being captured from <paramref name="deviceId"/>.
    /// Passing null stops capturing that role. Safe to call repeatedly (e.g. once per second).
    /// </summary>
    public void Ensure(MicRole role, string? deviceId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(role, out Session? existing))
            {
                if (deviceId == existing.DeviceId && !existing.Stopped)
                {
                    return;
                }

                StopLocked(role);
            }

            if (deviceId is null)
            {
                _failures.Remove(role);
                return;
            }

            if (_failures.TryGetValue(role, out var failure)
                && failure.DeviceId == deviceId
                && DateTime.UtcNow - failure.At < RetryDelay)
            {
                return;
            }

            try
            {
                StartLocked(role, deviceId);
                _failures.Remove(role);
            }
            catch (Exception ex)
            {
                bool firstTime = !_failures.TryGetValue(role, out var previous) || previous.DeviceId != deviceId;
                _failures[role] = (deviceId, DateTime.UtcNow);
                if (firstTime)
                {
                    AppLog.Info($"Could not listen to the {role} mic: {ex.Message}");
                }
            }
        }
    }

    private void StartLocked(MicRole role, string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = enumerator.GetDevice(deviceId);
        WasapiCapture capture;
        try
        {
            capture = new WasapiCapture(device);
        }
        catch
        {
            device.Dispose();
            throw;
        }

        var session = new Session { DeviceId = deviceId, Device = device, Capture = capture };

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            WaveFormat format = capture.WaveFormat;
            double db = LevelMeter.ComputeDb(e.Buffer, e.BytesRecorded, format);
            double ms = e.BytesRecorded * 1000.0 / format.AverageBytesPerSecond;
            BlockMeasured?.Invoke(role, db, ms);
        };

        capture.RecordingStopped += (_, e) =>
        {
            session.Stopped = true;
            if (e.Exception is not null)
            {
                AppLog.Info($"Capture of the {role} mic stopped: {e.Exception.Message}");
            }
        };

        capture.StartRecording();
        _sessions[role] = session;
    }

    private void StopLocked(MicRole role)
    {
        if (!_sessions.Remove(role, out Session? session))
        {
            return;
        }

        try
        {
            session.Capture.StopRecording();
        }
        catch (Exception)
        {
            // Already stopped or device gone.
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

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (MicRole role in new List<MicRole>(_sessions.Keys))
            {
                StopLocked(role);
            }
        }
    }
}
