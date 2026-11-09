namespace MicSwitcher.Core;

/// <summary>
/// Learns how loudly a microphone hears what the PC is playing ("bleed"), as a level difference in dB:
/// mic level minus playback level. For example -35 means the mic hears playback 35 dB quieter than it is played.
/// </summary>
/// <remarks>
/// Every block of audio recorded while something is playing adds one observation. A low percentile of the observed
/// differences is used, so short bursts of the user's own voice (which make the difference much bigger) do not inflate
/// the estimate. Older observations fade out with a half-life measured in seconds of observed playback.
/// </remarks>
public sealed class BleedEstimator
{
    public const double MinDb = -90;
    public const double MaxDb = 20;

    private const int Bins = 111; // one bin per dB from MinDb to MaxDb

    private readonly double[] _weights = new double[Bins];
    private readonly double _percentile;
    private readonly double _halfLifeSeconds;
    private readonly double _minSeconds;
    private double _total;

    /// <param name="percentile">Which part of the observations to trust (0.3 = a low-to-middle value).</param>
    /// <param name="halfLifeSeconds">How quickly old observations fade, in seconds of observed playback.</param>
    /// <param name="minSeconds">Observed playback needed before an estimate is offered.</param>
    public BleedEstimator(double percentile = 0.3, double halfLifeSeconds = 20, double minSeconds = 3)
    {
        _percentile = Math.Clamp(percentile, 0, 1);
        _halfLifeSeconds = Math.Max(1, halfLifeSeconds);
        _minSeconds = Math.Max(0, minSeconds);
    }

    /// <summary>Seconds of playback observed so far (after fading).</summary>
    public double ObservedSeconds => _total;

    /// <summary>The learned level difference, or null until enough playback has been observed.</summary>
    public double? CouplingDb
    {
        get
        {
            if (_total < _minSeconds || _total <= 0)
            {
                return null;
            }

            double target = _percentile * _total;
            double cumulative = 0;
            for (int i = 0; i < Bins; i++)
            {
                cumulative += _weights[i];
                if (cumulative >= target)
                {
                    return MinDb + i + 0.5;
                }
            }

            return MaxDb;
        }
    }

    /// <summary>Adds one block. Blocks recorded while nothing is playing are ignored.</summary>
    public void Observe(double micDb, double playbackDb, double blockMs)
    {
        if (playbackDb < BleedGuard.PlaybackFloorDb || blockMs <= 0)
        {
            return;
        }

        double seconds = blockMs / 1000.0;
        double keep = Math.Pow(0.5, seconds / _halfLifeSeconds);
        for (int i = 0; i < Bins; i++)
        {
            _weights[i] *= keep;
        }

        _total *= keep;

        double difference = Math.Clamp(micDb - playbackDb, MinDb, MaxDb);
        int bin = Math.Clamp((int)Math.Floor(difference - MinDb), 0, Bins - 1);
        _weights[bin] += seconds;
        _total += seconds;
    }

    public void Reset()
    {
        Array.Clear(_weights);
        _total = 0;
    }
}
