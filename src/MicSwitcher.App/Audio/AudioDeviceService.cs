using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MicSwitcher.App.Audio;

/// <summary>A capture (recording) device as shown to the user.</summary>
public sealed record CaptureDevice(string Id, string Name);

/// <summary>Everything that talks to the Windows audio system about devices: listing, default switching, gain.</summary>
/// <remarks>
/// A fresh <see cref="MMDeviceEnumerator"/> is created per call so the service is safe to use from any thread.
/// </remarks>
public sealed class AudioDeviceService
{
    public IReadOnlyList<CaptureDevice> GetActiveCaptureDevices()
    {
        var result = new List<CaptureDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                result.Add(new CaptureDevice(device.ID, device.FriendlyName));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // A device vanished while we were reading its properties; ignore it this round.
            }
            finally
            {
                device.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// Ids of all active capture devices. Cheaper than <see cref="GetActiveCaptureDevices"/> because no device
    /// properties (names) are read; used for the frequent "is my mic still there" check.
    /// </summary>
    public IReadOnlyList<string> GetActiveCaptureIds()
    {
        var result = new List<string>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                result.Add(device.ID);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // The device vanished while it was being read.
            }
            finally
            {
                device.Dispose();
            }
        }

        return result;
    }

    /// <summary>Endpoint id of the default capture device for a role, or null when there is none.</summary>
    public string? GetDefaultCaptureId(Role role = Role.Console) => GetDefaultId(DataFlow.Capture, role);

    /// <summary>Endpoint id of the default playback device (speakers, headphones) for a role, or null.</summary>
    public string? GetDefaultRenderId(Role role = Role.Console) => GetDefaultId(DataFlow.Render, role);

    private static string? GetDefaultId(DataFlow flow, Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Active playback devices (speakers, headphones).</summary>
    public IReadOnlyList<CaptureDevice> GetActiveRenderDevices()
    {
        var result = new List<CaptureDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                result.Add(new CaptureDevice(device.ID, device.FriendlyName));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // Vanished while being read.
            }
            finally
            {
                device.Dispose();
            }
        }

        return result;
    }

    /// <summary>Makes a playback device the Windows default (and optionally the default communications device).</summary>
    public void SetDefaultRender(string deviceId, bool includeCommunications) => SetDefaultCapture(deviceId, includeCommunications);

    /// <summary>Sets the master volume of a playback device (0–100 %).</summary>
    public void SetVolumePercent(string deviceId, int percent)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDevice(deviceId);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
    }

    /// <summary>Makes a capture device the Windows default (and optionally the default communications device).</summary>
    public void SetDefaultCapture(string deviceId, bool includeCommunications)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Check(config.SetDefaultEndpoint(deviceId, ERole.Console));
            Check(config.SetDefaultEndpoint(deviceId, ERole.Multimedia));
            if (includeCommunications)
            {
                Check(config.SetDefaultEndpoint(deviceId, ERole.Communications));
            }
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    /// <summary>
    /// Changes the Windows input volume of a device by <paramref name="deltaDb"/>, clamped to the device's range.
    /// Returns the change that was actually applied.
    /// </summary>
    public double AdjustGainDb(string deviceId, double deltaDb)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDevice(deviceId);
        AudioEndpointVolume volume = device.AudioEndpointVolume;

        float before = volume.MasterVolumeLevel;
        float min = volume.VolumeRange.MinDecibels;
        float max = volume.VolumeRange.MaxDecibels;
        volume.MasterVolumeLevel = (float)Math.Clamp(before + deltaDb, min, max);
        return volume.MasterVolumeLevel - before;
    }

    private static void Check(int hr)
    {
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }
}
