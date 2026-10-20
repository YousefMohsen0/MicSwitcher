namespace MicSwitcher.Core;

/// <summary>Outcome of a calibration run for the pro mic.</summary>
/// <param name="NoiseFloorDb">Typical background level while the user was quiet (dBFS).</param>
/// <param name="SpeechLevelDb">Typical level while the user was speaking (dBFS), before any gain change.</param>
/// <param name="ThresholdDb">Voice threshold to use, already shifted by <paramref name="GainDeltaDb"/>.</param>
/// <param name="GainDeltaDb">Gain change (dB) to apply to the Windows mic level so speech lands on the target level.</param>
/// <param name="Reliable">False when too little speech was captured to trust the numbers.</param>
/// <param name="Warning">Optional hint to show the user.</param>
public sealed record CalibrationResult(
    double NoiseFloorDb,
    double SpeechLevelDb,
    double ThresholdDb,
    double GainDeltaDb,
    bool Reliable,
    string? Warning);

/// <summary>Computes voice threshold and gain from two short recordings: silence, then normal speech.</summary>
public static class Calibration
{
    /// <summary>Speech blocks must exceed the noise floor by this much to count as speech.</summary>
    public const double SpeechMarginDb = 6;

    /// <summary>Minimum number of speech blocks needed for a trustworthy result.</summary>
    public const int MinSpeechBlocks = 10;

    public static CalibrationResult Compute(
        IReadOnlyList<double> noiseLevelsDb,
        IReadOnlyList<double> speechLevelsDb,
        double targetSpeechDb,
        bool adjustGain,
        double maxGainStepDb = 24)
    {
        double noiseFloor = noiseLevelsDb.Count == 0 ? -90 : Percentile(noiseLevelsDb, 0.9);

        var speechBlocks = speechLevelsDb.Where(l => l > noiseFloor + SpeechMarginDb).ToList();
        bool reliable = speechBlocks.Count >= MinSpeechBlocks;
        if (!reliable)
        {
            return new CalibrationResult(
                noiseFloor,
                speechBlocks.Count == 0 ? noiseFloor : Percentile(speechBlocks, 0.7),
                ThresholdDb: noiseFloor + 12,
                GainDeltaDb: 0,
                Reliable: false,
                Warning: "Not enough speech was detected. Speak at your normal volume close to the pro mic and try again.");
        }

        double speech = Percentile(speechBlocks, 0.7);
        double gain = adjustGain ? Math.Clamp(targetSpeechDb - speech, -maxGainStepDb, maxGainStepDb) : 0;

        double headroom = speech - noiseFloor;
        double threshold = noiseFloor + Math.Max(6, headroom * 0.4) + gain;

        string? warning = headroom < 10
            ? "Speech is only slightly louder than the background noise. Move closer to the mic or reduce noise for reliable detection."
            : null;

        return new CalibrationResult(noiseFloor, speech, threshold, gain, true, warning);
    }

    /// <summary>Linear-rank percentile (0..1) of a non-empty list.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) throw new ArgumentException("No values.", nameof(values));
        var sorted = values.OrderBy(v => v).ToList();
        int index = (int)Math.Round(Math.Clamp(p, 0, 1) * (sorted.Count - 1));
        return sorted[index];
    }
}
