using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class MicEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private enum S { Absent, Quiet, Talking }

    private static readonly S Absent = S.Absent;
    private static readonly S Quiet = S.Quiet;
    private static readonly S Talking = S.Talking;

    // Exact tick arithmetic: DateTime.AddSeconds(double) can land one tick off for values like 4.3.
    private static DateTime At(double seconds) => T0.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    private static MicInput In(string id, S s, bool fallback = false) =>
        new(id, id, s != S.Absent, s == S.Talking, s == S.Talking ? 10 : -20, fallback);

    private static string? Id(MicRole? role) => role?.ToString().ToLowerInvariant();

    private enum MicRole { Pro, Headset, Laptop }

    private sealed class Wrapper
    {
        private readonly MicEngine _engine;
        public Wrapper(EngineOptions? options = null) => _engine = new MicEngine(options);

        public Result Update(DateTime now, MicRole? current, S pro, S headset, S laptop) =>
            new(_engine.Update(now, Id(current), new[] { In("pro", pro), In("headset", headset), In("laptop", laptop, fallback: true) }));
    }

    private readonly record struct Result(SwitchDecision Decision)
    {
        public bool ShouldSwitch => Decision.ShouldSwitch;
        public MicRole? Target => Decision.TargetId is null ? null : Enum.Parse<MicRole>(Decision.TargetId, true);
    }

    [Fact]
    public void ProWithVoice_StaysOnPro()
    {
        var engine = new Wrapper();
        for (double t = 0; t <= 20; t += 0.1)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Talking, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void NobodyTalking_NeverSwitches_EvenAfterALongTime()
    {
        var engine = new Wrapper();
        for (double t = 0; t <= 120; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void NobodyTalking_StaysOnHeadset_WhenHeadsetIsAlreadyTheDefault()
    {
        var engine = new Wrapper();
        for (double t = 0; t <= 120; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void SilenceOnProDoesNotSwitch_WhenTheHeadsetIsAlsoSilent()
    {
        // This was the old behaviour (switch after 3 s without voice on the pro mic) and must not come back.
        var engine = new Wrapper();
        engine.Update(At(0), MicRole.Pro, Talking, Quiet, Quiet);
        for (double t = 0.5; t <= 30; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void VoiceOnlyOnHeadset_SwitchesToHeadset_AfterProHasBeenQuietLongEnough()
    {
        var engine = new Wrapper();
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);
        engine.Update(At(1.0), MicRole.Pro, Quiet, Talking, Quiet); // headset voice starts

        Assert.False(engine.Update(At(1.5), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch); // pro quiet only 1.5 s
        var decision = engine.Update(At(2.0), MicRole.Pro, Quiet, Talking, Quiet);

        Assert.Equal(MicRole.Headset, decision.Target);
    }

    [Fact]
    public void HeadsetVoice_WhileProAlsoHearsVoice_DoesNotSwitch()
    {
        var engine = new Wrapper();
        for (double t = 0; t <= 10; t += 0.1)
        {
            // Talking into the pro mic: the always-on headset mic hears it too.
            Assert.False(engine.Update(At(t), MicRole.Pro, Talking, Talking, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void HeadsetVoice_ShortlyAfterProVoice_WaitsForTheSilenceTimeout()
    {
        var engine = new Wrapper();
        engine.Update(At(1.0), MicRole.Pro, Talking, Talking, Quiet); // both hear voice, pro last heard at 1.0 s

        Assert.False(engine.Update(At(2.5), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch); // pro quiet 1.5 s
        Assert.Equal(MicRole.Headset, engine.Update(At(3.0), MicRole.Pro, Quiet, Talking, Quiet).Target); // 2.0 s
    }

    [Fact]
    public void ShortHeadsetBlip_DoesNotSwitch()
    {
        var engine = new Wrapper();
        engine.Update(At(0), MicRole.Pro, Quiet, Quiet, Quiet);

        engine.Update(At(5.0), MicRole.Pro, Quiet, Talking, Quiet);
        engine.Update(At(5.1), MicRole.Pro, Quiet, Quiet, Quiet); // blip ends after 100 ms
        Assert.False(engine.Update(At(5.2), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(5.3), MicRole.Pro, Quiet, Quiet, Quiet).ShouldSwitch);
    }

    [Fact]
    public void OnHeadset_SustainedProVoice_SwitchesBackToPro()
    {
        var engine = new Wrapper();

        Assert.False(engine.Update(At(10.0), MicRole.Headset, Talking, Quiet, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(10.2), MicRole.Headset, Talking, Quiet, Quiet).ShouldSwitch);
        Assert.Equal(MicRole.Pro, engine.Update(At(10.3), MicRole.Headset, Talking, Quiet, Quiet).Target);
    }

    [Fact]
    public void OnHeadset_ShortBlipOnPro_DoesNotSwitchBack()
    {
        var engine = new Wrapper();
        engine.Update(At(5.0), MicRole.Headset, Talking, Talking, Quiet);
        engine.Update(At(5.1), MicRole.Headset, Quiet, Talking, Quiet); // pro blip lasted 100 ms
        Assert.False(engine.Update(At(5.2), MicRole.Headset, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(9.0), MicRole.Headset, Quiet, Talking, Quiet).ShouldSwitch);
    }

    [Fact]
    public void OnHeadset_Silence_StaysOnHeadset()
    {
        var engine = new Wrapper();
        for (double t = 0; t <= 60; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Headset, Quiet, Quiet, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void ProMissing_UsesHeadset()
    {
        var engine = new Wrapper();
        var d = engine.Update(At(0), MicRole.Pro, Absent, Quiet, Quiet);
        Assert.Equal(MicRole.Headset, d.Target);
    }

    [Fact]
    public void ProAndHeadsetMissing_UsesLaptop()
    {
        var engine = new Wrapper();
        var d = engine.Update(At(0), MicRole.Headset, Absent, Absent, Quiet);
        Assert.Equal(MicRole.Laptop, d.Target);
    }

    [Fact]
    public void NothingAvailable_NoChange()
    {
        var engine = new Wrapper();
        Assert.False(engine.Update(At(0), null, Absent, Absent, Absent).ShouldSwitch);
    }

    [Fact]
    public void LaptopIsNeverChosen_WhileHeadsetOrProExist()
    {
        var engine = new Wrapper();
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
        var engine = new Wrapper();
        for (double t = 0; t <= 10; t += 0.5)
        {
            Assert.False(engine.Update(At(t), MicRole.Pro, Quiet, Absent, Quiet).ShouldSwitch);
        }
    }

    [Fact]
    public void OnLaptopAndProReturns_SwitchesToPro()
    {
        var engine = new Wrapper();
        var d = engine.Update(At(0), MicRole.Laptop, Quiet, Absent, Quiet);
        Assert.Equal(MicRole.Pro, d.Target);
    }

    [Fact]
    public void UnknownDefault_PicksProImmediately()
    {
        var engine = new Wrapper();
        var d = engine.Update(At(0), null, Quiet, Quiet, Quiet);
        Assert.Equal(MicRole.Pro, d.Target);
    }

    [Fact]
    public void DefaultPointingAtDisconnectedMic_IsTreatedAsNoDefault()
    {
        var engine = new Wrapper();
        // Windows still reports the headset as default but it is gone; pro is available.
        var d = engine.Update(At(0), MicRole.Headset, Quiet, Absent, Quiet);
        Assert.Equal(MicRole.Pro, d.Target);
    }

    [Fact]
    public void MinSwitchInterval_PreventsRapidFlapping()
    {
        var engine = new Wrapper(new EngineOptions { MinSwitchInterval = TimeSpan.FromSeconds(5) });
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
        var engine = new Wrapper();
        engine.Update(At(0), MicRole.Headset, Absent, Talking, Quiet);

        // The pro mic is plugged in at 100 s and Windows made it the default; the headset user keeps talking.
        Assert.False(engine.Update(At(100), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.False(engine.Update(At(101), MicRole.Pro, Quiet, Talking, Quiet).ShouldSwitch);
        Assert.Equal(MicRole.Headset, engine.Update(At(102), MicRole.Pro, Quiet, Talking, Quiet).Target);
    }
}
