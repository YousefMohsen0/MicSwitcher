using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "micswitcher-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var settings = SettingsStore.Load(SettingsPath);

        Assert.Empty(settings.Mics);
        Assert.Equal(2, settings.ActiveProfile.SilenceTimeoutSeconds);
        Assert.True(settings.SwitchCommunicationsRole);
        Assert.False(settings.Paused);
        Assert.False(settings.FirstRunCompleted);
        Assert.Equal("System", settings.Theme);
        Assert.Equal("auto", settings.Language);
        Assert.True(settings.ActiveProfile.IgnoreBleed);
        Assert.False(settings.ActiveProfile.SmartVoice);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var settings = new AppSettings { Theme = "Black", Language = "ar-EG", Paused = true, StartWithWindows = true, NotifyOnSwitch = true };
        settings.Mics.Add(new MicSlot
        {
            Id = "one", DeviceId = "{0.0.1.00000000}.{abc}", DeviceName = "Studio Mic", Label = "Main", IconKey = "Mic24",
            VoiceThresholdDb = -38.5, BleedCouplingDb = -24, OutputDeviceId = "out", OutputVolumePercent = 40,
        });
        settings.Mics.Add(new MicSlot { Id = "two", Fallback = true });
        settings.Hotkeys.Add(new HotkeyBinding { Action = HotkeyAction.SwitchToMic, MicId = "one", Gesture = "Ctrl+Alt+1" });

        SettingsStore.Save(settings, SettingsPath);
        var loaded = SettingsStore.Load(SettingsPath);

        Assert.Equal("Black", loaded.Theme);
        Assert.Equal("ar-EG", loaded.Language);
        Assert.True(loaded.Paused);
        Assert.True(loaded.NotifyOnSwitch);
        Assert.Equal(2, loaded.Mics.Count);
        Assert.Equal("Studio Mic", loaded.Mics[0].DeviceName);
        Assert.Equal("Main", loaded.Mics[0].Label);
        Assert.Equal(-38.5, loaded.Mics[0].VoiceThresholdDb);
        Assert.Equal(-24, loaded.Mics[0].BleedCouplingDb);
        Assert.Equal(40, loaded.Mics[0].OutputVolumePercent);
        Assert.True(loaded.Mics[1].Fallback);
        Assert.Equal("Ctrl+Alt+1", Assert.Single(loaded.Hotkeys).Gesture);
    }

    [Fact]
    public void CorruptFile_YieldsDefaults_AndKeepsBackup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = SettingsStore.Load(SettingsPath);

        Assert.Equal(2, settings.ActiveProfile.SilenceTimeoutSeconds);
        Assert.True(File.Exists(SettingsPath + ".bak"));
    }

    [Fact]
    public void EngineOptions_AreClampedToSaneValues()
    {
        var settings = new AppSettings();
        settings.ActiveProfile.SilenceTimeoutSeconds = 0;
        settings.ActiveProfile.ReturnDelayMs = -5;
        settings.ActiveProfile.MinSwitchIntervalSeconds = -1;

        var options = settings.ToEngineOptions();

        Assert.True(options.SilenceTimeout >= TimeSpan.FromSeconds(0.5));
        Assert.True(options.ReturnDelay >= TimeSpan.Zero);
        Assert.True(options.MinSwitchInterval >= TimeSpan.Zero);
    }

    [Fact]
    public void ForcedMic_MeansManualMode()
    {
        var settings = new AppSettings();
        settings.Mics.Add(new MicSlot { Id = "m" });
        settings.ActiveProfile.ForcedMicId = "m";

        Assert.Equal(SwitchMode.Manual, settings.ToEngineOptions().Mode);
    }

    [Fact]
    public void Version1File_IsMigratedToMicSlotsAndAProfile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "Theme": "Dark",
              "Pro": { "DeviceId": "pro-id", "DeviceName": "Studio", "Label": "My studio mic", "IconKey": "Star24" },
              "Headset": { "DeviceId": "head-id", "DeviceName": "Headset" },
              "Laptop": { "DeviceId": "lap-id", "DeviceName": "Built-in" },
              "VoiceThresholdDb": -40,
              "HeadsetVoiceThresholdDb": -33,
              "SilenceTimeoutSeconds": 3,
              "ReturnDelayMs": 450,
              "IgnoreBleed": false,
              "ProBleedCouplingDb": -31,
              "Paused": true
            }
            """);

        var loaded = SettingsStore.Load(SettingsPath);

        Assert.Equal("Dark", loaded.Theme);
        Assert.True(loaded.Paused);
        Assert.Equal(3, loaded.Mics.Count);

        Assert.Equal("pro", loaded.Mics[0].Id);
        Assert.Equal("pro-id", loaded.Mics[0].DeviceId);
        Assert.Equal("My studio mic", loaded.Mics[0].Label);
        Assert.Equal("Star24", loaded.Mics[0].IconKey);
        Assert.Equal(-40, loaded.Mics[0].VoiceThresholdDb);
        Assert.Equal(-31, loaded.Mics[0].BleedCouplingDb);
        Assert.False(loaded.Mics[0].Fallback);

        Assert.Equal("Headset mic", loaded.Mics[1].Label);
        Assert.Equal(-33, loaded.Mics[1].VoiceThresholdDb);

        Assert.True(loaded.Mics[2].Fallback);

        Assert.Equal(3, loaded.ActiveProfile.SilenceTimeoutSeconds);
        Assert.Equal(450, loaded.ActiveProfile.ReturnDelayMs);
        Assert.False(loaded.ActiveProfile.IgnoreBleed);
        Assert.True(loaded.FirstRunCompleted);
    }

    [Fact]
    public void Normalize_RepairsBrokenReferences()
    {
        var settings = new AppSettings { ActiveProfileId = "gone" };
        settings.Mics.Add(new MicSlot { Id = "x", VoiceThresholdDb = -500 });
        settings.Mics.Add(new MicSlot { Id = "x" });
        settings.ActiveProfile.ExcludedMicIds.Add("missing");
        settings.ActiveProfile.ForcedMicId = "missing";
        settings.Hotkeys.Add(new HotkeyBinding { Gesture = "" });

        settings.Normalize();

        Assert.NotEqual(settings.Mics[0].Id, settings.Mics[1].Id);
        Assert.Equal(-90, settings.Mics[0].VoiceThresholdDb);
        Assert.Equal(settings.Profiles[0].Id, settings.ActiveProfileId);
        Assert.Empty(settings.ActiveProfile.ExcludedMicIds);
        Assert.Null(settings.ActiveProfile.ForcedMicId);
        Assert.Empty(settings.Hotkeys);
    }

    [Fact]
    public void MicNames_FallBackToDeviceNameThenPosition()
    {
        var settings = new AppSettings();
        settings.Mics.Add(new MicSlot { Id = "a", Label = "Boss" });
        settings.Mics.Add(new MicSlot { Id = "b", DeviceName = "USB Audio" });
        settings.Mics.Add(new MicSlot { Id = "c" });

        Assert.Equal("Boss", settings.MicName("a"));
        Assert.Equal("USB Audio", settings.MicName("b"));
        Assert.Equal("Microphone 3", settings.MicName("c"));
    }

    [Fact]
    public void BleedAndUpdateSettings_RoundTrip()
    {
        var settings = new AppSettings { BleedMarginDb = 9, AutoCheckUpdates = false, AutoInstallUpdates = true, IncludePrereleases = false };
        settings.ActiveProfile.IgnoreBleed = false;

        SettingsStore.Save(settings, SettingsPath);
        var loaded = SettingsStore.Load(SettingsPath);

        Assert.Equal(9, loaded.BleedMarginDb);
        Assert.False(loaded.ActiveProfile.IgnoreBleed);
        Assert.False(loaded.AutoCheckUpdates);
        Assert.True(loaded.AutoInstallUpdates);
        Assert.False(loaded.IncludePrereleases);
    }
}
