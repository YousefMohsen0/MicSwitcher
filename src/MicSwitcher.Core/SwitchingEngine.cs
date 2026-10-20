namespace MicSwitcher.Core;

/// <summary>Timing knobs for <see cref="SwitchingEngine"/>.</summary>
public sealed class EngineOptions
{
    /// <summary>How long the pro mic must be free of voice before the headset mic takes over.</summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long voice must be continuously present on the pro mic before switching back to it.</summary>
    public TimeSpan ReturnDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Minimum time between two switches (prevents flapping). Ignored when the current default is unusable.</summary>
    public TimeSpan MinSwitchInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Pure decision logic: given which mics exist, whether the pro mic hears a voice and which mic is the
/// current Windows default, decide which mic should be the default. Contains no audio or OS code so it can
/// be unit tested with simulated time.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="number">
/// <item>Pro mic missing: headset mic, otherwise laptop mic, otherwise nothing.</item>
/// <item>Pro mic present: it is the default while it hears voice. After <see cref="EngineOptions.SilenceTimeout"/>
/// without voice, the headset mic becomes the default (if present). The pro mic is taken back once voice has been
/// present for <see cref="EngineOptions.ReturnDelay"/>.</item>
/// </list>
/// The laptop mic is never chosen while the pro or headset mic is available.
/// </remarks>
public sealed class SwitchingEngine
{
    private readonly EngineOptions _options;
    private DateTime? _lastProVoice;
    private DateTime? _proVoiceSince;
    private bool _proWasPresent;
    private DateTime _lastSwitch = DateTime.MinValue;

    public SwitchingEngine(EngineOptions? options = null)
    {
        _options = options ?? new EngineOptions();
    }

    /// <summary>Evaluate the current situation.</summary>
    /// <param name="now">Current time (injected so tests can control it).</param>
    /// <param name="currentDefault">Which managed mic is the Windows default right now, or null if none/unknown.</param>
    public SwitchDecision Update(DateTime now, MicRole? currentDefault, MicState pro, MicState headset, MicState laptop)
    {
        TrackProVoice(now, pro);

        // A default that points at a mic that is gone counts as "no default".
        if (currentDefault is MicRole c && !IsPresent(c, pro, headset, laptop))
        {
            currentDefault = null;
        }

        (MicRole? desired, string reason) = Decide(now, currentDefault, pro, headset, laptop);
        if (desired is null || desired == currentDefault)
        {
            return SwitchDecision.NoChange();
        }

        bool currentUsable = currentDefault is not null;
        if (currentUsable && now - _lastSwitch < _options.MinSwitchInterval)
        {
            return SwitchDecision.NoChange();
        }

        _lastSwitch = now;
        return new SwitchDecision(desired, reason);
    }

    private void TrackProVoice(DateTime now, MicState pro)
    {
        if (!pro.Present)
        {
            _proWasPresent = false;
            _lastProVoice = null;
            _proVoiceSince = null;
            return;
        }

        if (!_proWasPresent)
        {
            // Grace period: treat a freshly detected pro mic as if it just heard a voice.
            _proWasPresent = true;
            _lastProVoice = now;
        }

        if (pro.Voice)
        {
            _lastProVoice = now;
            _proVoiceSince ??= now;
        }
        else
        {
            _proVoiceSince = null;
        }
    }

    private (MicRole? Desired, string Reason) Decide(
        DateTime now, MicRole? current, MicState pro, MicState headset, MicState laptop)
    {
        if (!pro.Present)
        {
            if (headset.Present) return (MicRole.Headset, "pro mic not found, using headset mic");
            if (laptop.Present) return (MicRole.Laptop, "pro and headset mics not found, using laptop mic");
            return (null, "no microphones available");
        }

        bool silent = now - _lastProVoice!.Value >= _options.SilenceTimeout;
        if (silent)
        {
            return headset.Present
                ? (MicRole.Headset, $"no voice on pro mic for {_options.SilenceTimeout.TotalSeconds:0.#}s, using headset mic")
                : (MicRole.Pro, "no voice on pro mic but headset mic not found, keeping pro mic");
        }

        if (current == MicRole.Headset)
        {
            bool voiceSustained = _proVoiceSince is DateTime since && now - since >= _options.ReturnDelay;
            return voiceSustained
                ? (MicRole.Pro, "voice detected on pro mic")
                : (MicRole.Headset, "waiting for voice on pro mic");
        }

        return (MicRole.Pro, "pro mic available");
    }

    private static bool IsPresent(MicRole role, MicState pro, MicState headset, MicState laptop) => role switch
    {
        MicRole.Pro => pro.Present,
        MicRole.Headset => headset.Present,
        MicRole.Laptop => laptop.Present,
        _ => false,
    };
}
