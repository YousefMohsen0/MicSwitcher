using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class PlaybackTrackerTests
{
    [Fact]
    public void NothingObserved_IsSilence()
    {
        var tracker = new PlaybackTracker();
        Assert.Equal(PlaybackTracker.SilenceDb, tracker.RecentLevelDb(10_000));
    }

    [Fact]
    public void ReturnsTheLoudestRecentLevel()
    {
        var tracker = new PlaybackTracker(holdMs: 400);
        tracker.Observe(-40, 1_000);
        tracker.Observe(-20, 1_100);
        tracker.Observe(-30, 1_200);

        Assert.Equal(-20, tracker.RecentLevelDb(1_250));
    }

    [Fact]
    public void LevelsExpireAfterTheHoldTime()
    {
        var tracker = new PlaybackTracker(holdMs: 400);
        tracker.Observe(-20, 1_000);

        Assert.Equal(-20, tracker.RecentLevelDb(1_300));
        Assert.Equal(PlaybackTracker.SilenceDb, tracker.RecentLevelDb(1_600));
    }

    [Fact]
    public void LouderLevelKeepsTheMemoryAliveOnlyForItsOwnHold()
    {
        var tracker = new PlaybackTracker(holdMs: 400);
        tracker.Observe(-10, 1_000);   // loud, will expire
        tracker.Observe(-35, 1_500);   // quieter, later

        Assert.Equal(-35, tracker.RecentLevelDb(1_550));
    }

    [Fact]
    public void BucketsAreReusedCleanlyOverLongRuns()
    {
        var tracker = new PlaybackTracker(holdMs: 400);
        for (long t = 0; t < 600_000; t += 25)
        {
            tracker.Observe(-30, t);
        }

        Assert.Equal(-30, tracker.RecentLevelDb(599_990));
        Assert.Equal(PlaybackTracker.SilenceDb, tracker.RecentLevelDb(601_000));
    }
}

public class BleedEstimatorTests
{
    private static void Feed(BleedEstimator estimator, double micDb, double playbackDb, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.025)
        {
            estimator.Observe(micDb, playbackDb, 25);
        }
    }

    [Fact]
    public void NoEstimate_UntilEnoughPlaybackWasObserved()
    {
        var estimator = new BleedEstimator(minSeconds: 3);
        Feed(estimator, -55, -20, 2);
        Assert.Null(estimator.CouplingDb);

        Feed(estimator, -55, -20, 2);
        Assert.NotNull(estimator.CouplingDb);
    }

    [Fact]
    public void LearnsAConstantCoupling()
    {
        var estimator = new BleedEstimator();
        Feed(estimator, micDb: -55, playbackDb: -20, seconds: 10);

        Assert.InRange(estimator.CouplingDb!.Value, -36, -34); // -55 - (-20) = -35
    }

    [Fact]
    public void IgnoresBlocksWhileNothingIsPlaying()
    {
        var estimator = new BleedEstimator();
        Feed(estimator, micDb: -30, playbackDb: -100, seconds: 30);

        Assert.Null(estimator.CouplingDb);
        Assert.Equal(0, estimator.ObservedSeconds);
    }

    [Fact]
    public void ShortBurstsOfTheUsersVoice_DoNotInflateTheEstimate()
    {
        var estimator = new BleedEstimator();
        for (int cycle = 0; cycle < 6; cycle++)
        {
            Feed(estimator, micDb: -55, playbackDb: -20, seconds: 7);  // bleed only
            Feed(estimator, micDb: -15, playbackDb: -20, seconds: 3);  // user talks (30 % of the time)
        }

        Assert.InRange(estimator.CouplingDb!.Value, -37, -33);
    }

    [Fact]
    public void FollowsAChangeInTheRoom()
    {
        var estimator = new BleedEstimator();
        Feed(estimator, micDb: -55, playbackDb: -20, seconds: 20);
        Assert.InRange(estimator.CouplingDb!.Value, -36, -34);

        Feed(estimator, micDb: -45, playbackDb: -20, seconds: 120); // mic moved closer: bleed is now -25 dB
        Assert.InRange(estimator.CouplingDb!.Value, -26, -24);
    }

    [Fact]
    public void KeepsItsEstimateThroughLongSilences()
    {
        var estimator = new BleedEstimator();
        Feed(estimator, micDb: -55, playbackDb: -20, seconds: 10);
        double before = estimator.CouplingDb!.Value;

        Feed(estimator, micDb: -70, playbackDb: -100, seconds: 600);

        Assert.Equal(before, estimator.CouplingDb!.Value);
    }
}

public class BleedGuardTests
{
    [Fact]
    public void NothingPlaying_UsesTheUsersThreshold()
    {
        Assert.Equal(-45, BleedGuard.EffectiveThresholdDb(-45, playbackDb: -100, couplingDb: -30, marginDb: 6));
    }

    [Fact]
    public void QuietPlayback_DoesNotLowerOrRaiseTheThreshold()
    {
        // expected bleed = -20 - 35 + 6 = -49, below the user's -45
        Assert.Equal(-45, BleedGuard.EffectiveThresholdDb(-45, playbackDb: -20, couplingDb: -35, marginDb: 6));
    }

    [Fact]
    public void LoudPlayback_RaisesTheThreshold()
    {
        // expected bleed = -10 - 30 + 6 = -34
        Assert.Equal(-34, BleedGuard.EffectiveThresholdDb(-45, playbackDb: -10, couplingDb: -30, marginDb: 6));
    }

    [Fact]
    public void UnknownCoupling_UsesTheDefault()
    {
        double expected = -10 + BleedGuard.DefaultCouplingDb + 6;
        Assert.Equal(expected, BleedGuard.EffectiveThresholdDb(-60, playbackDb: -10, couplingDb: null, marginDb: 6));
    }

    [Fact]
    public void NegativeMargin_IsTreatedAsZero()
    {
        Assert.Equal(-40, BleedGuard.EffectiveThresholdDb(-60, playbackDb: -10, couplingDb: -30, marginDb: -5));
    }

    [Fact]
    public void ChooseCoupling_PrefersWhatIsAvailable()
    {
        Assert.Null(BleedGuard.ChooseCoupling(null, null));
        Assert.Equal(-30, BleedGuard.ChooseCoupling(null, -30));
        Assert.Equal(-33, BleedGuard.ChooseCoupling(-33, null));
    }

    [Fact]
    public void ChooseCoupling_CapsTheLearnedValueNearTheCalibratedOne()
    {
        Assert.Equal(-40, BleedGuard.ChooseCoupling(-40, -35));                                      // lower learned value wins
        Assert.Equal(-35 + BleedGuard.CalibratedHeadroomDb, BleedGuard.ChooseCoupling(-5, -35));      // runaway value is capped
    }

    [Fact]
    public void Scenario_MusicLeakingIntoTheHeadsetMic_IsNotVoice_ButSpeechStillIs()
    {
        // Music plays at -10 dB. The headset mic hears it 30 dB lower (-40 dB). The user's threshold is -45 dB, so
        // without the guard the music would count as a voice.
        var unguarded = new EnergyVoiceDetector(thresholdDb: -45, onsetMs: 100);
        var guarded = new EnergyVoiceDetector(thresholdDb: -45, onsetMs: 100);

        double threshold = BleedGuard.EffectiveThresholdDb(-45, playbackDb: -10, couplingDb: -30, marginDb: 6);
        guarded.ThresholdDb = threshold;

        for (int i = 0; i < 20; i++)
        {
            unguarded.Process(-40, 25);
            guarded.Process(-40, 25);
        }

        Assert.True(unguarded.IsVoice);
        Assert.False(guarded.IsVoice);

        // The user starts talking into the headset: -22 dB, far above the raised threshold.
        for (int i = 0; i < 20; i++)
        {
            guarded.Process(-22, 25);
        }

        Assert.True(guarded.IsVoice);
    }
}
