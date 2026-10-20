using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class EnergyVoiceDetectorTests
{
    [Fact]
    public void QuietSignal_IsNotVoice()
    {
        var vad = new EnergyVoiceDetector(thresholdDb: -45);
        for (int i = 0; i < 50; i++)
        {
            Assert.False(vad.Process(-70, 20));
        }
    }

    [Fact]
    public void LoudSignal_BecomesVoiceAfterOnset()
    {
        var vad = new EnergyVoiceDetector(thresholdDb: -45, onsetMs: 100);

        Assert.False(vad.Process(-20, 50));
        Assert.True(vad.Process(-20, 50));
    }

    [Fact]
    public void ShortClick_IsIgnored()
    {
        var vad = new EnergyVoiceDetector(thresholdDb: -45, onsetMs: 150);

        Assert.False(vad.Process(-10, 20));  // a click
        Assert.False(vad.Process(-80, 20));  // gone again
        Assert.False(vad.Process(-10, 20));  // counter restarted, still short
    }

    [Fact]
    public void Voice_StaysOnDuringHangover_ThenTurnsOff()
    {
        var vad = new EnergyVoiceDetector(thresholdDb: -45, onsetMs: 50, hangoverMs: 200);
        vad.Process(-20, 50);
        Assert.True(vad.IsVoice);

        Assert.True(vad.Process(-80, 100));   // 100 ms of the 200 ms hangover used
        Assert.True(vad.Process(-80, 50));    // 150 ms
        Assert.False(vad.Process(-80, 100));  // hangover exhausted
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var vad = new EnergyVoiceDetector(thresholdDb: -45, onsetMs: 50);
        vad.Process(-20, 100);
        Assert.True(vad.IsVoice);

        vad.Reset();
        Assert.False(vad.IsVoice);
    }
}
