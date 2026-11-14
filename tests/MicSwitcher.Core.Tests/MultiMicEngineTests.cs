using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class MultiMicEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double s) => T0.AddTicks((long)Math.Round(s * TimeSpan.TicksPerSecond));

    private static MicInput M(string id, bool present = true, bool voice = false, double headroom = 0, bool fallback = false) =>
        new(id, id.ToUpperInvariant(), present, voice, headroom, fallback);

    private static string? Step(MicEngine engine, double t, string? current, params MicInput[] mics) =>
        engine.Update(At(t), current, mics).TargetId;

    [Fact]
    public void FiveMics_HighestPriorityIsChosenWhenNoDefault()
    {
        var engine = new MicEngine();
        Assert.Equal("a", Step(engine, 0, null, M("a"), M("b"), M("c"), M("d"), M("e")));
    }

    [Fact]
    public void WhenSeveralLowerMicsHearAVoice_TheHighestOfThemWins()
    {
        var engine = new MicEngine();
        Step(engine, 0, "a", M("a"), M("b"), M("c"));

        string? first = null;
        for (double t = 1; t <= 6 && first is null; t += 0.1)
        {
            first = Step(engine, t, "a", M("a"), M("b", voice: true), M("c", voice: true));
        }

        Assert.Equal("b", first);
    }

    [Fact]
    public void LowerMic_IsBlocked_WhileAMicAboveItStillHearsAVoice()
    {
        var engine = new MicEngine();
        Step(engine, 0, "a", M("a"), M("b"), M("c"));

        // "b" keeps hearing voice (it is above "c"), "a" is silent: "c" must not win; "b" does after the silence timeout.
        string? first = null;
        for (double t = 1; t <= 6 && first is null; t += 0.1)
        {
            first = Step(engine, t, "a", M("a"), M("b", voice: true), M("c", voice: true));
        }

        Assert.Equal("b", first);
    }

    [Fact]
    public void LowerMic_TakesOver_AfterAllHigherMicsWereQuietLongEnough()
    {
        var engine = new MicEngine();
        Step(engine, 0, "a", M("a"), M("b"), M("c"));
        Step(engine, 1, "a", M("a"), M("b"), M("c", voice: true));
        Assert.Null(Step(engine, 1.5, "a", M("a"), M("b"), M("c", voice: true)));
        Assert.Equal("c", Step(engine, 2.0, "a", M("a"), M("b"), M("c", voice: true)));
    }

    [Fact]
    public void HigherMic_WinsBack_AfterSustainedVoice()
    {
        var engine = new MicEngine();
        Assert.Null(Step(engine, 10.0, "c", M("a", voice: true), M("b"), M("c")));
        Assert.Equal("a", Step(engine, 10.3, "c", M("a", voice: true), M("b"), M("c")));
    }

    [Fact]
    public void WhenTheTopMicTalks_ItWinsEvenIfEveryOtherMicHearsItToo()
    {
        var engine = new MicEngine();
        for (double t = 0; t <= 10; t += 0.1)
        {
            Assert.Null(Step(engine, t, "a", M("a", voice: true), M("b", voice: true), M("c", voice: true), M("d", voice: true)));
        }
    }

    [Fact]
    public void FallbackMics_AreOnlyUsed_WhenNoNormalMicIsPresent()
    {
        var engine = new MicEngine();
        Assert.Equal("laptop", Step(engine, 0, null, M("a", present: false), M("laptop", fallback: true)));
        Assert.Equal("a", Step(engine, 5, "laptop", M("a"), M("laptop", fallback: true)));
        Assert.Null(Step(engine, 10, "a", M("a"), M("laptop", voice: true, fallback: true)));
    }

    [Fact]
    public void NothingPresent_NoChange()
    {
        var engine = new MicEngine();
        Assert.Null(Step(engine, 0, null, M("a", present: false), M("b", present: false)));
    }

    [Fact]
    public void ManualMode_NeverSwitchesByItself_ButStillReplacesAMissingDefault()
    {
        var engine = new MicEngine(new EngineOptions { Mode = SwitchMode.Manual });
        for (double t = 0; t < 20; t += 0.2)
        {
            Assert.Null(Step(engine, t, "b", M("a", voice: true), M("b")));
        }

        Assert.Equal("a", Step(engine, 30, "b", M("a"), M("b", present: false)));
    }

    [Fact]
    public void LoudestMode_SwitchesToTheMicWithMoreHeadroom_AfterTheMargin()
    {
        var engine = new MicEngine(new EngineOptions { Mode = SwitchMode.Loudest });
        Step(engine, 0, "a", M("a"), M("b"));

        // Both hear the voice, "b" is only 3 dB better: not enough.
        for (double t = 1; t <= 3; t += 0.1)
        {
            Assert.Null(Step(engine, t, "a", M("a", voice: true, headroom: 10), M("b", voice: true, headroom: 13)));
        }

        // "b" is 12 dB better and has been heard for longer than the return delay: switch.
        Assert.Equal("b", Step(engine, 4, "a", M("a", voice: true, headroom: 4), M("b", voice: true, headroom: 16)));
    }

    [Fact]
    public void LoudestMode_DoesNotSwitch_WhenTheCurrentMicAndOthersAreSilent()
    {
        var engine = new MicEngine(new EngineOptions { Mode = SwitchMode.Loudest });
        for (double t = 0; t < 30; t += 0.5)
        {
            Assert.Null(Step(engine, t, "a", M("a"), M("b"), M("c")));
        }
    }

    [Fact]
    public void MinSwitchInterval_AppliesToAllMics()
    {
        var engine = new MicEngine(new EngineOptions { MinSwitchInterval = TimeSpan.FromSeconds(5) });
        Step(engine, 0, "a", M("a"), M("b"), M("c"));
        Step(engine, 1, "a", M("a"), M("b"), M("c", voice: true));
        Assert.Equal("c", Step(engine, 2.5, "a", M("a"), M("b"), M("c", voice: true)));

        Step(engine, 3, "c", M("a", voice: true), M("b"), M("c"));
        Assert.Null(Step(engine, 3.5, "c", M("a", voice: true), M("b"), M("c")));
        Assert.Equal("a", Step(engine, 7.6, "c", M("a", voice: true), M("b"), M("c")));
    }

    [Fact]
    public void DefaultThatIsNotManaged_CountsAsNoDefault()
    {
        var engine = new MicEngine();
        Assert.Equal("a", Step(engine, 0, "some-other-device", M("a"), M("b")));
    }
}
