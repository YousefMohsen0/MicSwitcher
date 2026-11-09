using System.Text.Json;

namespace MicSwitcher.Core;

/// <summary>Which Windows capture device is assigned to a role.</summary>
public sealed class MicSettings
{
    /// <summary>Windows endpoint id (stable across reboots).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Friendly name, kept so the UI can show something useful when the device is unplugged.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Name the user gave this mic; null or empty means the default ("Pro mic", …).</summary>
    public string? Label { get; set; }

    /// <summary>Name of a built-in icon (a Fluent symbol such as "Mic24"); null means the default for the role.</summary>
    public string? IconKey { get; set; }

    /// <summary>Path of an image the user picked; takes priority over <see cref="IconKey"/> when the file exists.</summary>
    public string? IconPath { get; set; }
}

/// <summary>All persisted user settings.</summary>
public sealed class AppSettings
{
    /// <summary>"System" (follow Windows), "Light", "Dark" or "Black" (pure black for OLED screens).</summary>
    public string Theme { get; set; } = "System";

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

    /// <summary>
    /// Ignore sound that leaks into a mic from the speakers or headphones (call participants, music): the voice
    /// threshold is raised while the PC is playing sound.
    /// </summary>
    public bool IgnoreBleed { get; set; } = true;

    /// <summary>Extra dB above the expected bleed level that a voice must reach.</summary>
    public double BleedMarginDb { get; set; } = 6;

    /// <summary>Measured level difference (mic minus playback, dB) from bleed calibration; null = not calibrated.</summary>
    public double? ProBleedCouplingDb { get; set; }

    public double? HeadsetBleedCouplingDb { get; set; }

    /// <summary>Check GitHub for new versions in the background.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Download and install updates automatically, at a moment when nobody is talking.</summary>
    public bool AutoInstallUpdates { get; set; }

    /// <summary>Also offer releases marked as pre-release.</summary>
    public bool IncludePrereleases { get; set; } = true;

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
