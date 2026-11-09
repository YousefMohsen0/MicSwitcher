namespace MicSwitcher.Core;

/// <summary>
/// Decides how loud a microphone has to be, while the PC is playing sound, before it counts as "someone is talking".
/// Sound that merely leaks from the speakers or headphones into the mic stays below that level.
/// </summary>
public static class BleedGuard
{
    /// <summary>Playback quieter than this counts as "nothing is playing".</summary>
    public const double PlaybackFloorDb = -60;

    /// <summary>Assumed level difference until a real one has been learned or calibrated.</summary>
    public const double DefaultCouplingDb = -30;

    /// <summary>How far above the calibrated value the learned value may drift before it is capped.</summary>
    public const double CalibratedHeadroomDb = 8;

    /// <summary>
    /// The voice threshold to use right now: the user's own threshold, raised above the level the playback is expected
    /// to leak into the mic (playback level + coupling + margin) while something is playing.
    /// </summary>
    public static double EffectiveThresholdDb(double userThresholdDb, double playbackDb, double? couplingDb, double marginDb)
    {
        if (playbackDb < PlaybackFloorDb)
        {
            return userThresholdDb;
        }

        double expectedBleed = playbackDb + (couplingDb ?? DefaultCouplingDb) + Math.Max(0, marginDb);
        return Math.Max(userThresholdDb, expectedBleed);
    }

    /// <summary>
    /// Combines the learned and the calibrated coupling. The learned value follows changes in the room, but may not
    /// stray far above the calibrated one, so a stretch of the user talking cannot teach it to ignore real speech.
    /// </summary>
    public static double? ChooseCoupling(double? learnedDb, double? calibratedDb)
    {
        if (learnedDb is null)
        {
            return calibratedDb;
        }

        if (calibratedDb is null)
        {
            return learnedDb;
        }

        return Math.Min(learnedDb.Value, calibratedDb.Value + CalibratedHeadroomDb);
    }
}
