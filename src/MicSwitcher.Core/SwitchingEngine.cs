namespace MicSwitcher.Core;

/// <summary>Timing knobs for <see cref="SwitchingEngine"/>.</summary>
public sealed class EngineOptions
{
    /// <summary>
    /// How long the pro mic must have been free of voice before a voice that is only on the headset mic
    /// is allowed to take over.
    /// </summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a voice must be continuously present on a mic before the engine switches to it.</summary>
    public TimeSpan ReturnDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Minimum time between two switches (prevents flapping). Ignored when the current default is unusable.</summary>
    public TimeSpan MinSwitchInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Pure decision logic: given which mics exist, where a voice is currently heard and which mic is the current
/// Windows default, decide which mic should be the default. Contains no audio or OS code so it can be unit tested
/// with simulated time.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="number">
/// <item>Pro mic missing: headset mic, otherwise laptop mic, otherwise nothing.</item>
/// <item>Pro mic present: it is the default. The headset mic only takes over when a voice is heard on the headset
/// mic and <b>not</b> on the pro mic (the pro mic has been voice-free for <see cref="EngineOptions.SilenceTimeout"/>).
/// If nobody is talking at all, nothing changes.</item>
/// <item>While the headset mic is the default, it stays the default until a voice is heard on the pro mic for
/// <see cref="EngineOptions.ReturnDelay"/>.</item>
/// </list>
/// The laptop mic is never chosen while the pro or headset mic is available.
/// </remarks>
public sealed class SwitchingEngine
{
    private readonly EngineOptions _options;
    private DateTime? _lastProVoice;
    private DateTime? _proVoiceSince;
    private DateTime? _headsetVoiceSince;
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
        TrackVoice(now, pro, headset);

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

    private void TrackVoice(DateTime now, MicState pro, MicState headset)
    {
        if (!pro.Present)
        {
            _proWasPresent = false;
            _lastProVoice = null;
            _proVoiceSince = null;
        }
        else
        {
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

        if (headset.Present && headset.Voice)
        {
            _headsetVoiceSince ??= now;
        }
        else
        {
            _headsetVoiceSince = null;
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

        if (current == MicRole.Headset)
        {
            // Stay on the headset until the pro mic really hears someone.
            bool proVoiceSustained = _proVoiceSince is DateTime since && now - since >= _options.ReturnDelay;
            return proVoiceSustained
                ? (MicRole.Pro, "voice detected on pro mic")
                : (MicRole.Headset, "waiting for a voice on the pro mic");
        }

        bool proQuietLongEnough = now - _lastProVoice!.Value >= _options.SilenceTimeout;
        bool headsetVoiceSustained = _headsetVoiceSince is DateTime hs && now - hs >= _options.ReturnDelay;
        bool voiceOnlyOnHeadset = headset.Present && headset.Voice && !pro.Voice
            && proQuietLongEnough && headsetVoiceSustained;

        return voiceOnlyOnHeadset
            ? (MicRole.Headset, "voice heard on the headset mic only")
            : (MicRole.Pro, "pro mic available");
    }

    private static bool IsPresent(MicRole role, MicState pro, MicState headset, MicState laptop) => role switch
    {
        MicRole.Pro => pro.Present,
        MicRole.Headset => headset.Present,
        MicRole.Laptop => laptop.Present,
        _ => false,
    };
}
