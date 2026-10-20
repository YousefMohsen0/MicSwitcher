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

        Assert.Null(settings.Pro.DeviceId);
        Assert.Equal(2, settings.SilenceTimeoutSeconds);
        Assert.True(settings.SwitchCommunicationsRole);
        Assert.False(settings.Paused);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var settings = new AppSettings
        {
            VoiceThresholdDb = -38.5,
            SilenceTimeoutSeconds = 7,
            Paused = true,
            StartWithWindows = true,
        };
        settings.Pro.DeviceId = "{0.0.1.00000000}.{abc}";
        settings.Pro.DeviceName = "Studio Mic";
        settings.Headset.DeviceId = "{0.0.1.00000000}.{def}";

        SettingsStore.Save(settings, SettingsPath);
        var loaded = SettingsStore.Load(SettingsPath);

        Assert.Equal(-38.5, loaded.VoiceThresholdDb);
        Assert.Equal(7, loaded.SilenceTimeoutSeconds);
        Assert.True(loaded.Paused);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal("{0.0.1.00000000}.{abc}", loaded.Pro.DeviceId);
        Assert.Equal("Studio Mic", loaded.Pro.DeviceName);
        Assert.Equal("{0.0.1.00000000}.{def}", loaded.Get(MicRole.Headset).DeviceId);
    }

    [Fact]
    public void CorruptFile_YieldsDefaults_AndKeepsBackup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = SettingsStore.Load(SettingsPath);

        Assert.Equal(2, settings.SilenceTimeoutSeconds);
        Assert.True(File.Exists(SettingsPath + ".bak"));
    }

    [Fact]
    public void EngineOptions_AreClampedToSaneValues()
    {
        var settings = new AppSettings { SilenceTimeoutSeconds = 0, ReturnDelayMs = -5, MinSwitchIntervalSeconds = -1 };

        var options = settings.ToEngineOptions();

        Assert.True(options.SilenceTimeout >= TimeSpan.FromSeconds(0.5));
        Assert.True(options.ReturnDelay >= TimeSpan.Zero);
        Assert.True(options.MinSwitchInterval >= TimeSpan.Zero);
    }
}
