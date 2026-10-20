using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class SwitchingEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly MicState Absent = new(false, false);
    private static readonly MicState Quiet = new(true, false);
    private static readonly MicState Talking = new(true, true);

    // Exact tick arithmetic: DateTime.AddSeconds(double) can land one tick off for values like 4.3.
    private static DateTime At(double seconds) => T0.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    [Fact]
    public void ProWithVoice_StaysOnPro()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t <= 20; t += 0.1)
        {
            var d = engine.Update(At(t), MicRole.Pro, Talking, Quiet, Quiet);
            Assert.False(d.ShouldSwitch);
        }
    }

    [Fact]
    public void ProSilentForTimeout_SwitchesToHeadset()
    {
        var engine = new SwitchingEngine();

        Assert.False(engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(2.9), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);

        var d = engine.Update(At(3.0), MicRole.Pro, Quiet, Quiet, Quiet);
        Assert.Equal(MicRole.Headset, d.Target);
    }

    [Fact]
    public void VoiceResettingTheSilenceTimer_DelaysTheSwitch()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);
        engine.Update(At(2), MicRole.Pro, Talking, Quiet, Quiet); // voice at 2s restarts the clock

        Assert.False(engine.Update(At(4.9), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
        Assert.Equal(MicRole.Headset, engine.Update(At(5.0), MicRole.Pro, Quiet, Quiet, Quiet).Target);
    }

    [Fact]
    public void SustainedVoiceOnPro_SwitchesBackFromHeadset()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);
        Assert.Equal(MicRole.Headset, engine.Update(At(3), MicRole.Pro, Quiet, Quiet, Quiet).Target);

        // Now the headset is the default. Voice starts at 4.0s but must last 300ms.
        Assert.False(engine.Update(At(4.0), MicRole.Headset, Talking, Quiet, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(4.2), MicRole.Headset, Talking, Quiet, Quiet).ShouldSwitch);
        Assert.Equal(MicRole.Pro, engine.Update(At(4.3), MicRole.Headset, Talking, Quiet, Quiet).Target);
    }

    [Fact]
    public void ShortBlipOnPro_DoesNotSwitchBack()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);
        engine.Update(At(3), MicRole.Pro, Quiet, Quiet, Quiet); // -> headset

        engine.Update(At(5.0), MicRole.Headset, Talking, Quiet, Quiet);
        engine.Update(At(5.1), MicRole.Headset, Quiet, Quiet, Quiet); // blip ends after 100 ms
        Assert.False(engine.Update(At(5.2), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(5.6), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
    }

    [Fact]
    public void ProMissing_UsesHeadset()
    {
        var engine = new SwitchingEngine();
        var d = engine.Update(At(0), MicRole.Pro, Absent, Quiet, Quiet);
        Assert.Equal(MicRole.Headset, d.Target);
    }

    [Fact]
    public void ProAndHeadsetMissing_UsesLaptop()
    {
        var engine = new SwitchingEngine();
        var d = engine.Update(At(0), MicRole.Headset, Absent, Absent, Quiet);
        Assert.Equal(MicRole.Laptop, d.Target);
    }

    [Fact]
    public void NothingAvailable_NoChange()
    {
        var engine = new SwitchingEngine();
        Assert.False(engine.Update(At(0), null, Absent, Absent, Absent).ShouldSwitch);
    }

    [Fact]
    public void LaptopIsNeverChosen_WhileHeadsetOrProExist()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t < 30; t += 0.5)
        {
            var d = engine.Update(At(t), MicRole.Headset, Quiet, Quiet, Quiet);
            Assert.NotEqual(MicRole.Laptop, d.Target);
        }
    }

    [Fact]
    public void ProSilentButHeadsetMissing_KeepsPro()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Pro, Quiet, Absent, Quiet);
        var d = engine.Update(At(10), MicRole.Pro, Quiet, Absent, Quiet);
        Assert.False(d.ShouldSwitch);
    }

    [Fact]
    public void OnLaptopAndProReturns_SwitchesToPro()
    {
        var engine = new SwitchingEngine();
        var d = engine.Update(At(0), MicRole.Laptop, Quiet, Absent, Quiet);
        Assert.Equal(MicRole.Pro, d.Target);
    }

    [Fact]
    public void UnknownDefault_PicksProImmediately()
    {
        var engine = new SwitchingEngine();
        var d = engine.Update(At(0), null, Quiet, Quiet, Quiet);
        Assert.Equal(MicRole.Pro, d.Target);
    }

    [Fact]
    public void DefaultPointingAtDisconnectedMic_IsTreatedAsNoDefault()
    {
        var engine = new SwitchingEngine();
        // Windows still reports the headset as default but it is gone; pro is available.
        var d = engine.Update(At(0), MicRole.Headset, Quiet, Absent, Quiet);
        Assert.Equal(MicRole.Pro, d.Target);
    }

    [Fact]
    public void MinSwitchInterval_PreventsRapidFlapping()
    {
        var engine = new SwitchingEngine(new EngineOptions { MinSwitchInterval = TimeSpan.FromSeconds(5) });
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);
        Assert.Equal(MicRole.Headset, engine.Update(At(3), MicRole.Pro, Quiet, Quiet, Quiet).Target);

        // Voice returns quickly, but the last switch was only 1.5 s ago.
        engine.Update(At(4), MicRole.Headset, Talking, Quiet, Quiet);
        Assert.False(engine.Update(At(4.5), MicRole.Headset, Talking, Quiet, Quiet).ShouldSwitch);

        Assert.Equal(MicRole.Pro, engine.Update(At(8.0), MicRole.Headset, Talking, Quiet, Quiet).Target);
    }

    [Fact]
    public void ProReappearing_GetsGracePeriodBeforeSwitchingAway()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Headset, Absent, Quiet, Quiet);

        // Pro is plugged in at 100 s while the headset is the default and nobody speaks yet.
        Assert.False(engine.Update(At(100), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(102), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
    }
}
