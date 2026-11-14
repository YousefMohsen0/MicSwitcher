# MicSwitcher

**Automatically switch your default Windows microphone between 2 to 10 mics, based on which one you are actually
talking into.**

MicSwitcher is a small tray app for Windows 10/11. If you own a desk microphone *and* a headset (or several mics),
Windows can only use one input at a time, so you either dig through Sound settings or end up with the wrong mic live.
MicSwitcher listens to your mics and flips the default for you.

- Talk into your main mic and it is used. Put the headset on and talk into that, and it takes over.
- Talk into several mics at once and the one highest in your priority list wins (or the loudest, your choice).
- Stop talking and nothing changes; it never switches just because it is quiet.
- Unplug things and it falls back to whatever is still connected.

Everything runs locally. MicSwitcher only measures audio *levels* and *spectrum shape*; it never records, stores or
sends audio. The only network use is the optional update check.

## Contents

- [How it works](#how-it-works)
- [Features](#features)
- [Install](#install)
- [Quick start](#quick-start)
- [Settings explained](#settings-explained)
- [Shortcuts and voice commands](#shortcuts-and-voice-commands)
- [Languages](#languages)
- [FAQ](#faq)
- [Troubleshooting](#troubleshooting)
- [Build from source](#build-from-source)
- [Project layout](#project-layout)
- [Contributing](#contributing)
- [License](#license)

## How it works

You list your microphones in priority order (2 to 10). The first has the highest priority. Mark any mic as a
**fallback** (for example the laptop's built-in mic): it is only used when no normal mic is connected.

Three modes, set per **profile**:

| Mode | Behaviour |
|------|-----------|
| **Priority** (default) | The highest mic that hears a voice is used. A lower mic only takes over when every mic above it has been free of voice for a moment (default 2 s). Talking into two mics at once: the higher one wins. |
| **Loudest wins** | The mic that hears your voice clearly louder than the current one (by a margin) is used. |
| **Manual** | Nothing switches by itself; use the tray menu, a shortcut or a voice command. |

In every mode: silence changes nothing, a short minimum time between switches prevents flip-flopping, and a missing
default is replaced by the best connected mic.

Mics are monitored continuously, even when they are not the default (Windows lets several apps listen to a mic at
once). Because a headset mic also hears you when you talk into another mic, calibration measures how loudly each mic
hears you when you use the *others* and places the voice thresholds so they can be told apart.

## Features

- **2 to 10 microphones**, reorderable, each with your own **name and icon** (built-in icons or a picture).
- **Profiles**: named sets of rules (mode, timings, ignored mics, a fixed mic) you can switch between from the tray,
  a shortcut or by voice.
- **"Why did it switch?" timeline**: a coloured bar of the last 6 hours and a list of recent switches with the
  reason and what triggered them (automatic, tray, shortcut, voice).
- **Tray quick-switch menu**: pick a mic and keep it for 10 min, 30 min, 1 h or until you release it; pause; change
  profile.
- **Global shortcuts**: switch to a specific mic, next mic, pause/resume, back to automatic, switch profile.
- **Voice commands** (opt-in): "hey mic, switch to headset", "pause", "next mic"… See below.
- **Smart voice detection** (opt-in): besides loudness it checks that the sound has a speech-like spectrum, so
  keyboards, fans and bumps do not count as voice. Classic loudness detection stays the default.
- **Output switching**: per mic, choose the playback device (and its volume) that Windows should use when that mic
  becomes active, for example headset speakers when the headset mic is live.
- **Guided calibration** for any number of mics, with a live countdown.
- **Bleed protection**: sound from your speakers or headphones that leaks into a mic is not mistaken for you.
- **Switch notifications** (off by default).
- **First-run setup** guide, which you can run again from the settings.
- **Diagnostics export**: copy or save a report with your settings, devices and recent log to attach to a bug report.
- **Themes**: follow Windows, light, dark, or pure black (AMOLED).
- **Languages**: English and Egyptian Arabic (with right-to-left layout).
- Start with Windows, optional checksum-verified auto-update, small installer, no .NET runtime needed.

## Install

**Requirements:** Windows 10 or 11, 64-bit.

1. Download `MicSwitcher-Setup-<version>.exe` from the [Releases](https://github.com/YousefMohsen0/MicSwitcher/releases) page.
2. Run it. It installs for your user only (no administrator prompt) and offers to start MicSwitcher.

Uninstall from Windows **Settings → Apps**; this also removes the "Start with Windows" task.

The installer is not code-signed unless the maintainer has configured a certificate, so Windows SmartScreen may show
"Windows protected your PC" the first time. Choose **More info → Run anyway**, or [build it from source](#build-from-source).
(Maintainers: add the repository secrets `CODESIGN_PFX_BASE64` and `CODESIGN_PFX_PASSWORD` and the CI signs the app and
installer automatically.)

### Updates

Under **Updates** in the settings window you can turn automatic checks on or off, choose whether updates install
automatically, and check manually. Updates are downloaded only from this repository's releases and installed only if
their SHA-256 checksum matches. Everything is optional; with all switches off the app never contacts the network.

### Bleed protection

When someone else's voice or music comes out of your speakers or headphones, some of it is picked up by your mics.
MicSwitcher listens to what your default playback device is playing and learns how much of it reaches each mic, then
raises the voice threshold by that amount while sound is playing. Run **Start calibration** with your usual call or
music playing quietly in the background to tune it precisely. You can turn it off in the settings window.

## Quick start

1. Start MicSwitcher. On first run a short setup guide helps you pick language, theme and your microphones.
2. Optionally let it **calibrate** (about 15 seconds): stay quiet, then speak into each mic when asked.
3. Close the window. MicSwitcher keeps running in the tray; right-click the icon to keep a mic, pause or exit.

Check it works by talking into different mics: the activity log and the timeline show each switch and why.

## Settings explained

| Setting | What it does |
|---------|--------------|
| **Your microphones** | The mics to choose between, in priority order. Use the arrows to reorder, the pencil for name, icon, fallback and output. The slider is the voice level that counts as "talking". |
| **Profile / Mode** | See [How it works](#how-it-works). **Manage…** adds, renames and edits profiles. |
| **Wait before a lower mic takes over** | How long higher mics must be free of voice first. |
| **Voice must last before switching** | How long a voice must be heard before switching to that mic. |
| **Smart voice detection** | Also requires a speech-like sound (slightly more CPU). |
| **Ignore sound from my speakers** | Bleed protection. |
| **Calibration** | Measures noise and voice per mic and sets the thresholds; can adjust the first mic's Windows input volume. |
| **Also change the "communications" default** | Also sets the device Teams, Discord and Zoom use. |
| **Start with Windows** | Registers a logon task that starts MicSwitcher hidden in the tray (with administrator rights, no prompt at logon). |
| **Notify on switch** | Shows a toast when the mic changes. |

Settings live in `%AppData%\MicSwitcher\settings.json`, the switch history in `history.json` and the log in
`log.txt` in the same folder. Settings from older versions are migrated automatically.

## Shortcuts and voice commands

**Shortcuts** (Settings → Shortcuts and voice → Customize) are global: choose an action, pick a mic or profile if
needed, click the box and press the keys. Use Ctrl/Alt/Shift/Windows plus a key (F-keys work alone). If another
program already owns a shortcut, MicSwitcher tells you.

**Voice commands** are off by default. When on, MicSwitcher listens on your Windows default microphone with the
speech recogniser built into Windows (offline; nothing is sent anywhere) for a wake phrase (default "hey mic")
followed by a command: *switch to \<mic name\>*, *next mic*, *pause*, *resume*, *back to automatic*, *switch profile
to \<name\>*.

Limits you should know about: it uses the recogniser Windows has for your language (English works best), and only
mic and profile names written in the Latin alphabet are added to the vocabulary, so Arabic names are **not**
recognised. Accuracy depends on your microphone and room; it is a convenience, not a guarantee.

## Languages

English and Egyptian Arabic (العربية - مصر). Choose in the setup guide or in Settings → Appearance. More languages can be added in
`Strings.cs` and `Loc.cs`.

## FAQ

**Does it record my audio?**
No. It reads the loudness of each short audio block to decide whether someone is speaking, then discards it. Nothing is
written to disk or sent anywhere.

**Why does "Start with Windows" ask for administrator permission?**
It creates a Task Scheduler task set to *run with highest privileges*. That is the standard way to start an elevated
program at logon without a UAC prompt every time. Changing the default microphone itself does not need administrator
rights, and the app also runs fine without elevation if you just launch it by hand.

**How do I remove autostart?**
Untick **Start with Windows** in the app, or delete the `MicSwitcher` task in Task Scheduler.

**How many mics can I use?**
From 2 up to 10.

**My headset mic is always on. Is that a problem?**
No, that is exactly the case MicSwitcher is built for. Both mics are watched for voice; the headset only takes over
when the voice is on the headset and not on the pro mic.

**What happens when nobody is talking?**
Nothing. The current default stays as it is until a voice is heard.

**Does it detect "human voice" or just loudness?**
By default loudness against a calibrated threshold. Turn on **Smart voice detection** to also require a speech-like
spectrum; it is a lightweight check, not a neural model, so very loud speech-like noise can still fool it.

## Troubleshooting

**Nothing switches.**
Check that switching is not paused (status card and tray menu), that each mic is assigned in the drop-downs, and that
the badge next to each mic does not say *not found*. The activity log shows every decision and any error.

**It switches to the headset while I am talking into the pro mic.**
The pro mic threshold is too high for your speaking level. Run the calibration again and speak at your normal volume
and distance, or lower the pro mic threshold a few dB.

**It never switches to the headset when I talk into it.**
Either the pro mic also hears you above its threshold (your voice leaks into it), or the headset threshold is too high.
Recalibrate with the headset on, raise the pro mic threshold a few dB, move the pro mic further away, or lower the
pro mic's input volume.

**It switches back and forth while I talk.**
Raise **Voice needed before switching** or **Wait before a lower mic takes over for…**, and check on the meters that the threshold lines sit
clearly above the idle noise and below your speech.

**A mic shows no level.**
Another program may be using the mic in *exclusive mode*, which stops other apps (including MicSwitcher) from listening.
In Windows Sound settings → the device's properties → Advanced, untick "Allow applications to take exclusive control".

**"Start with Windows" fails.**
Accept the administrator prompt when it appears. It only works with the installed app, not when the app is started
through `dotnet run`.

**The wrong device is selected after unplugging and replugging.**
Devices are remembered by Windows' device ID. If a device appears under a new name (some USB ports do this), pick it again
in the drop-down.

## Build from source

Requires Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/YousefMohsen0/MicSwitcher.git
cd MicSwitcher

dotnet test                         # engine, voice detector, calibration and settings tests
dotnet run --project src/MicSwitcher.App

# installer (needs Inno Setup 6)
dotnet publish src/MicSwitcher.App -c Release -r win-x64 --self-contained true -o publish
iscc /DAppVersion=1.0.0 /DSourceDir=publish installer\MicSwitcher.iss
```

The core logic and its tests target plain .NET 8 and run on any OS; only the app project needs Windows.

## Project layout

```
src/MicSwitcher.Core/            Pure logic: switching engine, voice detector, calibration, settings (no Windows code)
src/MicSwitcher.App/             WPF tray app: NAudio capture, device switching, startup task, UI
tests/MicSwitcher.Core.Tests/    xUnit tests for everything in Core
.github/workflows/               CI: build, test, build the installer, create releases
installer/                       Inno Setup script for the compressed installer
```

How the pieces fit: audio capture (`CaptureMonitor`) produces a level per short block → `EnergyVoiceDetector` (and
optionally `SpeechAnalyzer`) decides if it is voice → `MicEngine` decides which mic should be the default →
`AudioDeviceService` sets it in Windows. `SwitchController` runs this loop and records `SwitchHistory`.

Notes for contributors: the default device is changed through the `IPolicyConfig` COM interface, which Windows itself
uses and tools like SoundSwitch rely on, but which has no official public documentation.

## Contributing

Issues and pull requests are welcome.

- **Bug reports:** please include your Windows version, which kinds of mics you use (USB, XLR via interface,
  Bluetooth, built-in) and the relevant part of the activity log (`%AppData%\MicSwitcher\log.txt`).
- **Code:** keep decision logic in `MicSwitcher.Core` with unit tests; run `dotnet test` before opening a pull request.
- **Ideas:** a neural voice-activity model, more languages, per-mic gain presets.

## Credits

- The placeholder application icon is based on the "microphone" icon from
  [Material Design Icons](https://pictogrammers.com/library/mdi/) by Pictogrammers, licensed under the
  [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0).
- UI built with [WPF UI](https://github.com/lepoco/wpfui) (MIT) and audio handled with
  [NAudio](https://github.com/naudio/NAudio) (MIT).

## License

[MIT](LICENSE)
