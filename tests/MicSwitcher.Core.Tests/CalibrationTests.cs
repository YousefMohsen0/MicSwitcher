using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class CalibrationTests
{
    private static List<double> Repeat(double value, int count) => Enumerable.Repeat(value, count).ToList();

    [Fact]
    public void Percentile_PicksRankedValue()
    {
        var values = new List<double> { 5, 1, 3, 2, 4 };
        Assert.Equal(1, Calibration.Percentile(values, 0));
        Assert.Equal(3, Calibration.Percentile(values, 0.5));
        Assert.Equal(5, Calibration.Percentile(values, 1));
    }

    [Fact]
    public void ThresholdSitsBetweenNoiseAndSpeech()
    {
        var result = Calibration.Compute(Repeat(-60, 30), Repeat(-25, 60), targetSpeechDb: -25, adjustGain: false);

        Assert.True(result.Reliable);
        Assert.Equal(-60, result.NoiseFloorDb);
        Assert.Equal(-25, result.SpeechLevelDb);
        Assert.Equal(0, result.GainDeltaDb);
        Assert.InRange(result.ThresholdDb, -60, -25);
        Assert.Equal(-46, result.ThresholdDb, 3); // -60 + 0.4 * 35
    }

    [Fact]
    public void QuietSpeech_GetsPositiveGain_AndThresholdShiftsWithIt()
    {
        var result = Calibration.Compute(Repeat(-70, 30), Repeat(-40, 60), targetSpeechDb: -20, adjustGain: true);

        Assert.Equal(20, result.GainDeltaDb);
        // Without gain the threshold would be -70 + 0.4 * 30 = -58; the gain moves it up by 20 dB.
        Assert.Equal(-38, result.ThresholdDb, 3);
    }

    [Fact]
    public void GainChange_IsLimited()
    {
        var result = Calibration.Compute(Repeat(-90, 30), Repeat(-60, 60), targetSpeechDb: -10, adjustGain: true, maxGainStepDb: 24);
        Assert.Equal(24, result.GainDeltaDb);
    }

    [Fact]
    public void TooLittleSpeech_IsNotReliable_AndChangesNothing()
    {
        var result = Calibration.Compute(Repeat(-60, 30), Repeat(-62, 60), targetSpeechDb: -20, adjustGain: true);

        Assert.False(result.Reliable);
        Assert.Equal(0, result.GainDeltaDb);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void SpeechBarelyAboveNoise_ProducesWarning()
    {
        var result = Calibration.Compute(Repeat(-50, 30), Repeat(-42, 60), targetSpeechDb: -42, adjustGain: false);

        Assert.True(result.Reliable);
        Assert.NotNull(result.Warning);
    }
}
