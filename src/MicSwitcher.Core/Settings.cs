using System.Text.Json;

namespace MicSwitcher.Core;

/// <summary>Which Windows capture device is assigned to a role.</summary>
public sealed class MicSettings
{
    /// <summary>Windows endpoint id (stable across reboots).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Friendly name, kept so the UI can show something useful when the device is unplugged.</summary>
    public string? DeviceName { get; set; }
}

/// <summary>All persisted user settings.</summary>
public sealed class AppSettings
{
    public MicSettings Pro { get; set; } = new();
    public MicSettings Headset { get; set; } = new();
    public MicSettings Laptop { get; set; } = new();

    /// <summary>Voice threshold for the pro mic in dBFS (set by calibration).</summary>
    public double VoiceThresholdDb { get; set; } = -45;

    /// <summary>Voice threshold for the headset mic in dBFS (set by calibration).</summary>
    public double HeadsetVoiceThresholdDb { get; set; } = -45;

    public double VoiceOnsetMs { get; set; } = 150;
    public double VoiceHangoverMs { get; set; } = 400;

    /// <summary>
    /// Seconds the pro mic must be free of voice before a voice heard only on the headset mic takes over.
    /// </summary>
    public double SilenceTimeoutSeconds { get; set; } = 2;

    /// <summary>Milliseconds a voice must be heard continuously on a mic before switching to it.</summary>
    public double ReturnDelayMs { get; set; } = 300;

    public double MinSwitchIntervalSeconds { get; set; } = 1;

    /// <summary>Also set the "default communication device" (used by Teams, Discord, Zoom...).</summary>
    public bool SwitchCommunicationsRole { get; set; } = true;

    /// <summary>Run at logon through an elevated scheduled task (starts hidden in the tray).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Automatic switching is paused (the app keeps monitoring but never changes the default).</summary>
    public bool Paused { get; set; }

    /// <summary>Let calibration change the Windows input volume of the pro mic.</summary>
    public bool AdjustGainOnCalibrate { get; set; } = true;

    /// <summary>Level (dBFS) calibration aims for when you speak normally.</summary>
    public double TargetSpeechDb { get; set; } = -20;

    public MicSettings Get(MicRole role) => role switch
    {
        MicRole.Pro => Pro,
        MicRole.Headset => Headset,
        MicRole.Laptop => Laptop,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public EngineOptions ToEngineOptions() => new()
    {
        SilenceTimeout = TimeSpan.FromSeconds(Math.Max(0.5, SilenceTimeoutSeconds)),
        ReturnDelay = TimeSpan.FromMilliseconds(Math.Max(0, ReturnDelayMs)),
        MinSwitchInterval = TimeSpan.FromSeconds(Math.Max(0, MinSwitchIntervalSeconds)),
    };
}

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MicSwitcher",
        "settings.json");

    /// <summary>Loads settings; a missing or corrupt file yields defaults (a corrupt file is kept as .bak).</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppSettings();

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
    }

    /// <summary>Saves settings atomically (write temp file, then replace).</summary>
    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
