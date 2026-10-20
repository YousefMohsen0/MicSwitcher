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

    // Defaults used below: SilenceTimeout 2 s, ReturnDelay 300 ms, MinSwitchInterval 1 s.

    [Fact]
    public void ProWithVoice_StaysOnPro()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t <= 20; t += 0.1)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Talking, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void NobodyTalking_NeverSwitches_EvenAfterALongTime()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t <= 120; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void NobodyTalking_StaysOnHeadset_WhenHeadsetIsAlreadyTheDefault()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t <= 120; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void SilenceOnProDoesNotSwitch_WhenTheHeadsetIsAlsoSilent()
    {
        // This was the old behaviour (switch after 3 s without voice on the pro mic) and must not come back.
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Pro, Talking, Quiet, Quiet);
        for (double t = 0.5; t <= 30; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void VoiceOnlyOnHeadset_SwitchesToHeadset_AfterProHasBeenQuietLongEnough()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);
        engine.Update(At(1.0), MicRole.Pro, Quiet, Talking, Quiet); // headset voice starts

        Assert.False(engine.Update(At(1.5), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch); // pro quiet only 1.5 s
        var decision = engine.Update(At(2.0), MicRole.Pro, Quiet, Talking, Quiet);

        Assert.Equal(MicRole.Headset, decision.Target);
    }

    [Fact]
    public void HeadsetVoice_WhileProAlsoHearsVoice_DoesNotSwitch()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t <= 10; t += 0.1)
        {
            // Talking into the pro mic: the always-on headset mic hears it too.
            Assert.False(engine.Update(At(t), MicRole.Pro, Talking, Talking, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void HeadsetVoice_ShortlyAfterProVoice_WaitsForTheSilenceTimeout()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(1.0), MicRole.Pro, Talking, Talking, Quiet); // both hear voice, pro last heard at 1.0 s

        Assert.False(engine.Update(At(2.5), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch); // pro quiet 1.5 s
        Assert.Equal(MicRole.Headset, engine.Update(At(3.0), MicRole.Pro, Quiet, Talking, Quiet).Target); // 2.0 s
    }

    [Fact]
    public void ShortHeadsetBlip_DoesNotSwitch()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);

        engine.Update(At(5.0), MicRole.Pro, Quiet, Talking, Quiet);
        engine.Update(At(5.1), MicRole.Pro, Quiet, Quiet, Quiet); // blip ends after 100 ms
        Assert.False(engine.Update(At(5.2), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(5.3), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
    }

    [Fact]
    public void OnHeadset_SustainedProVoice_SwitchesBackToPro()
    {
        var engine = new SwitchingEngine();

        Assert.False(engine.Update(At(10.0), MicRole.Headset, Talking, Quiet, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(10.2), MicRole.Headset, Talking, Quiet, Quiet).ShouldSwitch);
        Assert.Equal(MicRole.Pro, engine.Update(At(10.3), MicRole.Headset, Talking, Quiet, Quiet).Target);
    }

    [Fact]
    public void OnHeadset_ShortBlipOnPro_DoesNotSwitchBack()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(5.0), MicRole.Headset, Talking, Talking, Quiet);
        engine.Update(At(5.1), MicRole.Headset, Quiet, Talking, Quiet); // pro blip lasted 100 ms
        Assert.False(engine.Update(At(5.2), MicRole.Headset, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(9.0), MicRole.Headset, Quiet, Talking, Quiet).ShouldSwitch);
    }

    [Fact]
    public void OnHeadset_Silence_StaysOnHeadset()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t <= 60; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
        }
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
            bool headsetTalking = t > 10;
            var d = engine.Update(At(t), MicRole.Headset, Quiet, headsetTalking ? Talking : Quiet, Quiet);
            Assert.NotEqual(MicRole.Laptop, d.Target);
        }
    }

    [Fact]
    public void HeadsetVoiceButHeadsetMissing_KeepsPro()
    {
        var engine = new SwitchingEngine();
        for (double t = 0; t <= 10; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Quiet, Absent, Quiet).ShouldSwitch);
        }
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
        engine.Update(At(0), MicRole.Pro, Quiet, Talking, Quiet);
        Assert.Equal(MicRole.Headset, engine.Update(At(2), MicRole.Pro, Quiet, Talking, Quiet).Target);

        // The user turns to the pro mic and speaks, but the last switch was only 1.5 s ago.
        engine.Update(At(3), MicRole.Headset, Talking, Talking, Quiet);
        Assert.False(engine.Update(At(3.5), MicRole.Headset, Talking, Talking, Quiet).ShouldSwitch);

        Assert.Equal(MicRole.Pro, engine.Update(At(7.0), MicRole.Headset, Talking, Talking, Quiet).Target);
    }

    [Fact]
    public void FreshlyPluggedProMic_GetsGracePeriodBeforeHeadsetCanTakeOver()
    {
        var engine = new SwitchingEngine();
        engine.Update(At(0), MicRole.Headset, Absent, Talking, Quiet);

        // The pro mic is plugged in at 100 s and Windows made it the default; the headset user keeps talking.
        Assert.False(engine.Update(At(100), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(101), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.Equal(MicRole.Headset, engine.Update(At(102), MicRole.Pro, Quiet, Talking, Quiet).Target);
    }
}
