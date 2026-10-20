namespace MicSwitcher.Core;

/// <summary>The three logical microphones the app manages, in priority order.</summary>
public enum MicRole
{
    /// <summary>Main professional microphone. Preferred whenever someone is speaking into it.</summary>
    Pro,

    /// <summary>Microphone attached to the headset. Always on, used when the pro mic hears no voice.</summary>
    Headset,

    /// <summary>Built-in laptop microphone. Only used when neither of the others is available.</summary>
    Laptop,
}

/// <summary>Snapshot of one microphone as seen by the switching engine.</summary>
/// <param name="Present">The device is plugged in / active in Windows.</param>
/// <param name="Voice">Voice activity is currently detected (only relevant for the pro mic).</param>
public readonly record struct MicState(bool Present, bool Voice);

/// <summary>Result of one engine evaluation.</summary>
/// <param name="Target">The mic that should become the Windows default, or null for "no change".</param>
/// <param name="Reason">Human readable explanation, used for the log.</param>
public readonly record struct SwitchDecision(MicRole? Target, string Reason)
{
    public bool ShouldSwitch => Target.HasValue;

    public static SwitchDecision NoChange() => new(null, string.Empty);
}
