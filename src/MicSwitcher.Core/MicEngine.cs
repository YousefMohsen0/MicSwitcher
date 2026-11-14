namespace MicSwitcher.Core;

/// <summary>How the engine chooses between microphones.</summary>
public enum SwitchMode
{
    /// <summary>The order of the list decides: higher mics win, lower mics only take over when higher ones are silent.</summary>
    Priority,

    /// <summary>Whichever mic hears the voice best (relative to its own threshold) becomes the default.</summary>
    Loudest,

    /// <summary>Never switches by itself, except to replace a default that disappeared.</summary>
    Manual,
}

/// <summary>Timing knobs and mode for <see cref="MicEngine"/>.</summary>
public sealed class EngineOptions
{
    public SwitchMode Mode { get; init; } = SwitchMode.Priority;

    /// <summary>How long every higher mic must have been free of voice before a lower mic that hears a voice takes over.</summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a voice must be continuously present on a mic before the engine switches to it.</summary>
    public TimeSpan ReturnDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Minimum time between two switches (prevents flapping). Ignored when the current default is unusable.</summary>
    public TimeSpan MinSwitchInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>In <see cref="SwitchMode.Loudest"/>: how much louder (dB above its threshold) another mic must be.</summary>
    public double LoudestMarginDb { get; init; } = 6;
}

/// <summary>One microphone as seen by the engine. The list passed to the engine is in priority order, best first.</summary>
/// <param name="Id">Stable id of the mic slot.</param>
/// <param name="Name">Name used in reasons.</param>
/// <param name="Present">The device is plugged in / active in Windows.</param>
/// <param name="Voice">A voice is currently detected on it.</param>
/// <param name="HeadroomDb">How far the recent level is above this mic's voice threshold (for Loudest mode).</param>
/// <param name="Fallback">Only used when no non-fallback mic is present.</param>
public readonly record struct MicInput(string Id, string Name, bool Present, bool Voice, double HeadroomDb = 0, bool Fallback = false);

/// <summary>Result of one engine evaluation.</summary>
/// <param name="TargetId">The mic that should become the Windows default, or null for "no change".</param>
/// <param name="Reason">Human readable explanation, used for the log and the history.</param>
public readonly record struct SwitchDecision(string? TargetId, string Reason)
{
    public bool ShouldSwitch => TargetId is not null;

    public static SwitchDecision NoChange() => new(null, string.Empty);
}

/// <summary>
/// Pure decision logic for any number of microphones: given which mics exist, where a voice is heard and which mic is
/// the current Windows default, decide which mic should be the default. No audio or OS code; time is injected.
/// </summary>
/// <remarks>
/// Priority mode (the default), mics listed best first:
/// <list type="bullet">
/// <item>No usable default: the best present mic (fallback mics only when no normal mic is present).</item>
/// <item>A higher mic than the current one hears a voice for <see cref="EngineOptions.ReturnDelay"/>: switch to it.</item>
/// <item>A lower mic hears a voice, and every mic above it has been voice-free for
/// <see cref="EngineOptions.SilenceTimeout"/>: switch to it.</item>
/// <item>Silence everywhere changes nothing.</item>
/// </list>
/// </remarks>
public sealed class MicEngine
{
    private sealed class Track
    {
        public bool WasPresent;
        public DateTime? LastVoice;
        public DateTime? VoiceSince;
    }

    private readonly EngineOptions _options;
    private readonly Dictionary<string, Track> _tracks = new();
    private DateTime _lastSwitch = DateTime.MinValue;

    public MicEngine(EngineOptions? options = null)
    {
        _options = options ?? new EngineOptions();
    }

    public SwitchDecision Update(DateTime now, string? currentId, IReadOnlyList<MicInput> mics)
    {
        foreach (MicInput mic in mics)
        {
            TrackVoice(now, mic);
        }

        // A default that points at a mic that is gone (or not managed) counts as "no default".
        if (currentId is not null && !mics.Any(m => m.Id == currentId && m.Present))
        {
            currentId = null;
        }

        (MicInput? desired, string reason) = Decide(now, currentId, mics);
        if (desired is null || desired.Value.Id == currentId)
        {
            return SwitchDecision.NoChange();
        }

        if (currentId is not null && now - _lastSwitch < _options.MinSwitchInterval)
        {
            return SwitchDecision.NoChange();
        }

        _lastSwitch = now;
        return new SwitchDecision(desired.Value.Id, reason);
    }

    private Track TrackOf(string id)
    {
        if (!_tracks.TryGetValue(id, out Track? track))
        {
            _tracks[id] = track = new Track();
        }

        return track;
    }

    private void TrackVoice(DateTime now, MicInput mic)
    {
        Track track = TrackOf(mic.Id);
        if (!mic.Present)
        {
            track.WasPresent = false;
            track.LastVoice = null;
            track.VoiceSince = null;
            return;
        }

        if (!track.WasPresent)
        {
            // Grace period: a freshly detected mic counts as if it just heard a voice, so a lower mic cannot take
            // over the very moment a better mic is plugged in.
            track.WasPresent = true;
            track.LastVoice = now;
        }

        if (mic.Voice)
        {
            track.LastVoice = now;
            track.VoiceSince ??= now;
        }
        else
        {
            track.VoiceSince = null;
        }
    }

    private bool Sustained(DateTime now, MicInput mic) =>
        mic.Voice && TrackOf(mic.Id).VoiceSince is DateTime since && now - since >= _options.ReturnDelay;

    private bool QuietLongEnough(DateTime now, MicInput mic) =>
        !mic.Voice && TrackOf(mic.Id).LastVoice is DateTime last && now - last >= _options.SilenceTimeout;

    private (MicInput? Desired, string Reason) Decide(DateTime now, string? currentId, IReadOnlyList<MicInput> mics)
    {
        List<MicInput> normals = mics.Where(m => m.Present && !m.Fallback).ToList();
        List<MicInput> fallbacks = mics.Where(m => m.Present && m.Fallback).ToList();

        if (normals.Count == 0)
        {
            if (fallbacks.Count == 0)
            {
                return (null, "no microphones available");
            }

            return fallbacks.Any(m => m.Id == currentId)
                ? (null, string.Empty)
                : (fallbacks[0], $"no main microphone found, using {fallbacks[0].Name}");
        }

        if (currentId is null)
        {
            return (normals[0], $"{normals[0].Name} is available");
        }

        int index = normals.FindIndex(m => m.Id == currentId);
        if (index < 0)
        {
            // The default is a fallback mic but a normal one is back.
            return (normals[0], $"{normals[0].Name} is available again");
        }

        if (_options.Mode == SwitchMode.Manual)
        {
            return (null, string.Empty);
        }

        return _options.Mode == SwitchMode.Loudest
            ? DecideLoudest(now, normals, index)
            : DecidePriority(now, normals, index);
    }

    private (MicInput? Desired, string Reason) DecidePriority(DateTime now, List<MicInput> normals, int index)
    {
        for (int i = 0; i < index; i++)
        {
            if (Sustained(now, normals[i]))
            {
                return (normals[i], $"voice detected on {normals[i].Name}");
            }
        }

        for (int i = index + 1; i < normals.Count; i++)
        {
            if (!Sustained(now, normals[i]))
            {
                continue;
            }

            bool higherAllQuiet = true;
            for (int j = 0; j < i && higherAllQuiet; j++)
            {
                higherAllQuiet = QuietLongEnough(now, normals[j]);
            }

            if (higherAllQuiet)
            {
                return (normals[i], $"voice heard on {normals[i].Name} only");
            }
        }

        return (null, string.Empty);
    }

    private (MicInput? Desired, string Reason) DecideLoudest(DateTime now, List<MicInput> normals, int index)
    {
        MicInput current = normals[index];
        MicInput? best = null;
        foreach (MicInput mic in normals)
        {
            if (mic.Id != current.Id && Sustained(now, mic) && (best is null || mic.HeadroomDb > best.Value.HeadroomDb))
            {
                best = mic;
            }
        }

        if (best is null)
        {
            return (null, string.Empty);
        }

        bool takeOver = current.Voice
            ? best.Value.HeadroomDb >= current.HeadroomDb + _options.LoudestMarginDb
            : QuietLongEnough(now, current);

        return takeOver ? (best, $"{best.Value.Name} hears you best") : (null, string.Empty);
    }
}
