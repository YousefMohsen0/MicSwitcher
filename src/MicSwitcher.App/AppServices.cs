using System;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Everything the windows need from the application, so they do not depend on the App class itself.</summary>
public sealed class AppServices
{
    public required AppSettings Settings { get; init; }
    public required SwitchController Controller { get; init; }
    public required AudioDeviceService Devices { get; init; }
    public required UpdateService Updates { get; init; }

    /// <summary>Saves the settings and tells the controller, tray, hotkeys… to pick them up.</summary>
    public required Action Persist { get; init; }

    public required Action<bool> SetPaused { get; init; }
    public required Action<string> SwitchProfile { get; init; }

    /// <summary>Called after the language changed: the windows are rebuilt in the new language.</summary>
    public required Action LanguageChanged { get; init; }

    /// <summary>Re-registers global hotkeys; returns the gestures Windows refused.</summary>
    public required Func<System.Collections.Generic.IReadOnlyList<string>> ApplyHotkeys { get; init; }

    /// <summary>Starts or stops voice commands according to the settings; returns an error text or null.</summary>
    public required Func<string?> ApplyVoiceCommands { get; init; }

    public required Action RerunSetup { get; init; }
}
