using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicSwitcher.Core;

/// <summary>One microphone the app manages: which Windows device it is, how it looks and how it behaves.</summary>
public sealed class MicSlot
{
    /// <summary>Stable id of the slot (not the Windows device id).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Windows endpoint id (stable across reboots); null = no device chosen yet.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Friendly device name, kept so the UI can show something useful when the device is unplugged.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Name the user gave this mic; null or empty means "use the device name".</summary>
    public string? Label { get; set; }

    /// <summary>Name of a built-in icon (a Fluent symbol such as "Mic24"); null means the default icon.</summary>
    public string? IconKey { get; set; }

    /// <summary>Path of an image the user picked; takes priority over <see cref="IconKey"/> when the file exists.</summary>
    public string? IconPath { get; set; }

    /// <summary>Only used when no non-fallback mic is present (like a laptop's built-in mic).</summary>
    public bool Fallback { get; set; }

    /// <summary>Colour (#RRGGBB) used for this mic in the tray icon and timeline; null = automatic by position.</summary>
    public string? Color { get; set; }

    /// <summary>Voice threshold in dBFS (set by calibration).</summary>
    public double VoiceThresholdDb { get; set; } = -45;

    /// <summary>Measured level difference (mic minus playback, dB) from bleed calibration; null = not calibrated.</summary>
    public double? BleedCouplingDb { get; set; }

    /// <summary>When this mic becomes the default, also make this playback device the default (null = leave alone).</summary>
    public string? OutputDeviceId { get; set; }

    public string? OutputDeviceName { get; set; }

    /// <summary>When this mic becomes the default, set the output volume to this percentage (null = leave alone).</summary>
    public int? OutputVolumePercent { get; set; }

    public string DisplayName(int position) =>
        !string.IsNullOrWhiteSpace(Label) ? Label.Trim()
        : !string.IsNullOrWhiteSpace(DeviceName) ? DeviceName
        : $"Microphone {position + 1}";
}

/// <summary>A named set of switching behaviour the user can swap with one click or hotkey.</summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public SwitchMode Mode { get; set; } = SwitchMode.Priority;

    /// <summary>Seconds every higher mic must be free of voice before a lower mic that hears a voice takes over.</summary>
    public double SilenceTimeoutSeconds { get; set; } = 2;

    /// <summary>Milliseconds a voice must be heard continuously on a mic before switching to it.</summary>
    public double ReturnDelayMs { get; set; } = 300;

    public double MinSwitchIntervalSeconds { get; set; } = 1;

    /// <summary>Ignore sound that leaks into a mic from the speakers or headphones.</summary>
    public bool IgnoreBleed { get; set; } = true;

    /// <summary>Use the speech-shape check (not just loudness) so keyboards, fans and bumps are not counted as voice.</summary>
    public bool SmartVoice { get; set; }

    /// <summary>Mics this profile never switches to.</summary>
    public List<string> ExcludedMicIds { get; set; } = new();

    /// <summary>When set, this mic is used all the time and nothing switches automatically.</summary>
    public string? ForcedMicId { get; set; }
}

public enum HotkeyAction
{
    SwitchToMic,
    TogglePause,
    NextMic,
    ReleaseLock,
    SwitchProfile,
}

/// <summary>A global keyboard shortcut and what it does.</summary>
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }

    /// <summary>The mic for <see cref="HotkeyAction.SwitchToMic"/>.</summary>
    public string? MicId { get; set; }

    /// <summary>The profile for <see cref="HotkeyAction.SwitchProfile"/>.</summary>
    public string? ProfileId { get; set; }

    /// <summary>Like "Ctrl+Alt+F1". See <see cref="HotkeyGesture"/>.</summary>
    public string Gesture { get; set; } = string.Empty;
}

/// <summary>All persisted user settings.</summary>
public sealed class AppSettings
{
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>"System" (follow Windows), "Light", "Dark" or "Black" (pure black for OLED screens).</summary>
    public string Theme { get; set; } = "System";

    /// <summary>"auto" (follow Windows) or a language code such as "en" or "ar-EG".</summary>
    public string Language { get; set; } = "auto";

    public bool FirstRunCompleted { get; set; }

    /// <summary>The managed mics, best first.</summary>
    public List<MicSlot> Mics { get; set; } = new();

    public List<Profile> Profiles { get; set; } = new() { new Profile { Id = "default", Name = "Default" } };
    public string ActiveProfileId { get; set; } = "default";

    public double VoiceOnsetMs { get; set; } = 150;
    public double VoiceHangoverMs { get; set; } = 400;

    /// <summary>Also set the "default communication device" (used by Teams, Discord, Zoom...).</summary>
    public bool SwitchCommunicationsRole { get; set; } = true;

    /// <summary>Run at logon through an elevated scheduled task (starts hidden in the tray).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Automatic switching is paused (the app keeps monitoring but never changes the default).</summary>
    public bool Paused { get; set; }

    /// <summary>Extra dB above the expected bleed level that a voice must reach.</summary>
    public double BleedMarginDb { get; set; } = 6;

    /// <summary>Show a small notification whenever the default mic changes.</summary>
    public bool NotifyOnSwitch { get; set; }

    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }
    public bool IncludePrereleases { get; set; } = true;

    /// <summary>Let calibration change the Windows input volume.</summary>
    public bool AdjustGainOnCalibrate { get; set; } = true;

    /// <summary>Level (dBFS) calibration aims for when you speak normally.</summary>
    public double TargetSpeechDb { get; set; } = -20;

    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    /// <summary>Opt-in offline voice commands such as "hey mic, switch to headset".</summary>
    public bool VoiceCommandsEnabled { get; set; }

    public string WakePhrase { get; set; } = "hey mic";

    /// <summary>The mic that listens for commands (null = the current default mic).</summary>
    public string? VoiceCommandMicId { get; set; }

    /// <summary>Where the settings window was last placed (null = centre of the screen).</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    [JsonIgnore]
    public Profile ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];

    public MicSlot? FindMic(string? id) => id is null ? null : Mics.FirstOrDefault(m => m.Id == id);

    public int IndexOfMic(string id) => Mics.FindIndex(m => m.Id == id);

    public string MicName(string id)
    {
        int index = IndexOfMic(id);
        return index < 0 ? "Unknown mic" : Mics[index].DisplayName(index);
    }

    public EngineOptions ToEngineOptions()
    {
        Profile p = ActiveProfile;
        return new EngineOptions
        {
            Mode = p.ForcedMicId is not null ? SwitchMode.Manual : p.Mode,
            SilenceTimeout = TimeSpan.FromSeconds(Math.Max(0.5, p.SilenceTimeoutSeconds)),
            ReturnDelay = TimeSpan.FromMilliseconds(Math.Max(0, p.ReturnDelayMs)),
            MinSwitchInterval = TimeSpan.FromSeconds(Math.Max(0, p.MinSwitchIntervalSeconds)),
        };
    }

    /// <summary>Repairs anything a hand-edited or older file could break (always at least one profile, etc.).</summary>
    public void Normalize()
    {
        Mics ??= new();
        Profiles ??= new();
        Hotkeys ??= new();
        Mics.RemoveAll(m => m is null);
        Profiles.RemoveAll(p => p is null);
        if (Profiles.Count == 0)
        {
            Profiles.Add(new Profile { Id = "default", Name = "Default" });
        }

        if (Profiles.All(p => p.Id != ActiveProfileId))
        {
            ActiveProfileId = Profiles[0].Id;
        }

        var seen = new HashSet<string>();
        foreach (MicSlot mic in Mics)
        {
            if (string.IsNullOrEmpty(mic.Id) || !seen.Add(mic.Id))
            {
                mic.Id = Guid.NewGuid().ToString("N");
                seen.Add(mic.Id);
            }

            mic.VoiceThresholdDb = Math.Clamp(mic.VoiceThresholdDb, -90, 0);
        }

        foreach (Profile profile in Profiles)
        {
            profile.ExcludedMicIds ??= new();
            profile.ExcludedMicIds.RemoveAll(id => !seen.Contains(id));
            if (profile.ForcedMicId is not null && !seen.Contains(profile.ForcedMicId))
            {
                profile.ForcedMicId = null;
            }
        }

        Hotkeys.RemoveAll(h => h is null || string.IsNullOrWhiteSpace(h.Gesture));
    }
}

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON, upgrading files from older versions.</summary>
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
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.TryGetProperty("Mics", out _))
            {
                MigrateFromVersion1(settings, document.RootElement);
            }

            settings.SchemaVersion = AppSettings.CurrentSchema;
            settings.Normalize();
            return settings;
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

    /// <summary>Turns the fixed pro / headset / laptop settings of versions up to 0.3 into mic slots and a profile.</summary>
    private static void MigrateFromVersion1(AppSettings settings, JsonElement root)
    {
        double Number(string name, double fallback) =>
            root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : fallback;

        double? OptionalNumber(string name) =>
            root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

        MicSlot? Legacy(string name, string id, string label, string icon, bool fallback, double threshold, double? coupling)
        {
            if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Text(string key) =>
                element.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

            return new MicSlot
            {
                Id = id,
                DeviceId = Text("DeviceId"),
                DeviceName = Text("DeviceName"),
                Label = string.IsNullOrWhiteSpace(Text("Label")) ? label : Text("Label"),
                IconKey = Text("IconKey") ?? icon,
                IconPath = Text("IconPath"),
                Fallback = fallback,
                VoiceThresholdDb = threshold,
                BleedCouplingDb = coupling,
            };
        }

        settings.Mics.Clear();
        foreach (MicSlot? slot in new[]
        {
            Legacy("Pro", "pro", "Pro mic", "Mic24", false, Number("VoiceThresholdDb", -45), OptionalNumber("ProBleedCouplingDb")),
            Legacy("Headset", "headset", "Headset mic", "Headphones24", false, Number("HeadsetVoiceThresholdDb", -45), OptionalNumber("HeadsetBleedCouplingDb")),
            Legacy("Laptop", "laptop", "Laptop mic", "Laptop24", true, -45, null),
        })
        {
            if (slot is not null)
            {
                settings.Mics.Add(slot);
            }
        }

        settings.Profiles = new List<Profile>
        {
            new()
            {
                Id = "default",
                Name = "Default",
                SilenceTimeoutSeconds = Number("SilenceTimeoutSeconds", 2),
                ReturnDelayMs = Number("ReturnDelayMs", 300),
                MinSwitchIntervalSeconds = Number("MinSwitchIntervalSeconds", 1),
                IgnoreBleed = !root.TryGetProperty("IgnoreBleed", out JsonElement bleed) || bleed.ValueKind != JsonValueKind.False,
            },
        };
        settings.ActiveProfileId = "default";

        // Someone who already used the app has seen the setup; do not show the first-run wizard to them.
        settings.FirstRunCompleted = settings.Mics.Any(m => m.DeviceId is not null);
    }
}
