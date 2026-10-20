namespace MicSwitcher.Core;

/// <summary>
/// Simple level-based voice activity detector with onset and hangover smoothing.
/// Feed it one level reading (dBFS) per audio block; it reports whether a voice is currently present.
/// </summary>
/// <remarks>
/// The threshold is meant to be set by the calibration step, which measures the room's noise floor and the
/// user's speaking level. The detector is deliberately behind a tiny API so it can be swapped for a smarter
/// one (e.g. WebRTC VAD) later without touching the engine.
/// </remarks>
public sealed class EnergyVoiceDetector
{
    private double _aboveMs;
    private double _holdMs;

    public EnergyVoiceDetector(double thresholdDb = -45, double onsetMs = 150, double hangoverMs = 400)
    {
        ThresholdDb = thresholdDb;
        OnsetMs = onsetMs;
        HangoverMs = hangoverMs;
    }

    /// <summary>Level (dBFS) above which a block counts as "loud enough to be speech".</summary>
    public double ThresholdDb { get; set; }

    /// <summary>The level must stay above the threshold this long before voice is reported (filters clicks/bumps).</summary>
    public double OnsetMs { get; set; }

    /// <summary>Voice stays reported for this long after the level drops (bridges pauses between words).</summary>
    public double HangoverMs { get; set; }

    public bool IsVoice { get; private set; }

    /// <summary>Process one block. Returns the current voice state.</summary>
    /// <param name="levelDb">Block level in dBFS (0 = full scale, more negative = quieter).</param>
    /// <param name="blockMs">Duration of the block in milliseconds.</param>
    public bool Process(double levelDb, double blockMs)
    {
        if (levelDb >= ThresholdDb)
        {
            _aboveMs += blockMs;
            _holdMs = HangoverMs;
            if (_aboveMs >= OnsetMs)
            {
                IsVoice = true;
            }
        }
        else
        {
            _aboveMs = 0;
            if (IsVoice)
            {
                _holdMs -= blockMs;
                if (_holdMs <= 0)
                {
                    IsVoice = false;
                }
            }
        }

        return IsVoice;
    }

    public void Reset()
    {
        _aboveMs = 0;
        _holdMs = 0;
        IsVoice = false;
    }
}
