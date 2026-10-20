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

    /// <param name="noiseLevelsDb">Block levels while the user was quiet.</param>
    /// <param name="speechLevelsDb">Block levels while the user spoke into this mic.</param>
    /// <param name="targetSpeechDb">Level the speech should land on when gain adjustment is enabled.</param>
    /// <param name="adjustGain">Whether to suggest a gain change.</param>
    /// <param name="maxGainStepDb">Upper limit for the suggested gain change.</param>
    /// <param name="interferenceLevelsDb">
    /// Optional: block levels this mic picked up while the user spoke into <i>another</i> mic. The voice threshold is
    /// placed above this level so the other mic's voice does not count as voice here.
    /// </param>
    public static CalibrationResult Compute(
        IReadOnlyList<double> noiseLevelsDb,
        IReadOnlyList<double> speechLevelsDb,
        double targetSpeechDb,
        bool adjustGain,
        double maxGainStepDb = 24,
        IReadOnlyList<double>? interferenceLevelsDb = null)
    {
        double noiseFloor = noiseLevelsDb.Count == 0 ? -90 : Percentile(noiseLevelsDb, 0.9);
        bool hasInterference = interferenceLevelsDb is { Count: > 0 };
        double interference = hasInterference ? Percentile(interferenceLevelsDb!, 0.9) : double.NegativeInfinity;
        double floor = Math.Max(noiseFloor, interference);

        var speechBlocks = speechLevelsDb.Where(l => l > noiseFloor + SpeechMarginDb).ToList();
        bool reliable = speechBlocks.Count >= MinSpeechBlocks;
        if (!reliable)
        {
            return new CalibrationResult(
                noiseFloor,
                speechBlocks.Count == 0 ? noiseFloor : Percentile(speechBlocks, 0.7),
                ThresholdDb: floor + 12,
                GainDeltaDb: 0,
                Reliable: false,
                Warning: "Not enough speech was detected. Speak at your normal volume close to the mic and try again.");
        }

        double speech = Percentile(speechBlocks, 0.7);
        double gain = adjustGain ? Math.Clamp(targetSpeechDb - speech, -maxGainStepDb, maxGainStepDb) : 0;

        double headroom = speech - floor;
        double threshold = Math.Min(floor + Math.Max(6, headroom * 0.4), speech - 2) + gain;

        string? warning = null;
        if (headroom < 10)
        {
            warning = interference > noiseFloor
                ? "Your voice into the other mic reaches this mic almost as loudly as speaking into it, so the two cannot be told apart reliably. Move the mics further apart or lower this mic's volume."
                : "Speech is only slightly louder than the background noise. Move closer to the mic or reduce noise for reliable detection.";
        }

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
