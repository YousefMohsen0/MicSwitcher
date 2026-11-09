# MicSwitcher

**Automatically switch your default Windows microphone between a main mic, a headset mic and a built-in mic, based
on whether anyone is actually talking into the main one.**

MicSwitcher is a small tray app for Windows 10/11. If you own a good desk microphone *and* a headset, you have probably
run into this: Windows can only use one input at a time, so you either keep digging through Sound settings, or you end
up with the wrong mic live. MicSwitcher listens to your mics and flips the default for you.

- Talk into your desk mic and it is used.
- Put the headset on and speak into that instead, and the headset mic takes over automatically.
- Stop talking and nothing changes; it never switches just because it is quiet.
- Unplug things and it falls back to whatever is still connected.

Everything runs locally. MicSwitcher only measures audio *levels*; it never records, stores or sends audio, and it
does not use the network.

> **Status:** early release (v0.x). Bug reports and feedback are very welcome; see [Contributing](#contributing).

## Contents

- [How it works](#how-it-works)
- [Features](#features)
- [Install](#install)
- [Quick start](#quick-start)
- [Settings explained](#settings-explained)
- [FAQ](#faq)
- [Troubleshooting](#troubleshooting)
- [Build from source](#build-from-source)
- [Project layout](#project-layout)
- [Contributing](#contributing)
- [License](#license)

## How it works

You assign up to three microphones to roles, in priority order:

| Role | Typical device | Used when |
|------|----------------|-----------|
| **Pro mic** | Desk / studio / USB / XLR mic | Whenever a voice is heard on it. |
| **Headset mic** | Mic on a headset (often always on) | The pro mic is quiet, or not connected. |
| **Laptop mic** | Built-in microphone | Only when neither of the others is connected. |

You do not need all three. Leave a role unset and it is simply skipped.

The rules:

1. **Pro mic not connected:** use the headset mic; if that is missing too, use the laptop mic.
2. **Pro mic connected:** it is the default. The headset mic only takes over when a voice is heard **on the headset
   mic and not on the pro mic** (the pro mic has been free of voice for a couple of seconds, default 2 s).
3. **Silence changes nothing.** If nobody is talking, the current mic stays the default, whichever it is.
4. **Back to the pro mic:** while the headset is the default, it stays the default until a voice is heard on the pro
   mic for a moment (default 0.3 s).
5. A short minimum time between switches prevents flip-flopping, and the laptop mic is never chosen while the pro or
   headset mic is available.

The pro and headset mics are monitored continuously, even while they are not the default (Windows lets several apps
listen to a mic at once). The laptop mic is only metered while the settings window is open.

Because the headset mic also hears you when you talk into the pro mic, calibration measures how loudly each mic hears
you when you use the *other* one, and places the voice thresholds so the two can be told apart.

## Features

- Runs quietly in the **system tray**. The icon colour shows the active mic: green = pro, amber = headset,
  red = laptop, grey = paused or none.
- **Modern settings window** (follows your Windows light/dark theme) with mic selection, live level meters that show
  the voice threshold as a marker line, "voice / quiet" indicators and an activity log.
- **Guided calibration** with a live countdown: measures your room noise and how loud you are on each mic, sets both
  voice thresholds, and can optionally adjust the Windows input volume of the pro mic so your voice lands at a good
  level.
- Adjustable thresholds, **pro-quiet time** and **switch delay**, plus a **pause** switch.
- Optionally changes the **default communications device** too (the one Teams, Discord and Zoom use).
- **Start with Windows**, running in the background with administrator rights and no UAC prompt at each logon.
- **Bleed protection**: sound from your speakers or headphones (a Discord call, music) that leaks into a mic is
  recognised and not mistaken for you talking.
- **Optional auto-update** from GitHub releases (checksum-verified), and a small **installer**; no .NET runtime needed.
- Light on resources: it only wakes ~10 times per second, trims its memory when the window is closed, and builds the
  window on demand.

## Install

**Requirements:** Windows 10 or 11, 64-bit.

1. Download `MicSwitcher-Setup-<version>.exe` from the [Releases](https://github.com/YousefMohsen0/MicSwitcher/releases) page.
2. Run it. It installs for your user only (no administrator prompt) and offers to start MicSwitcher.

Uninstall from Windows **Settings → Apps**; this also removes the "Start with Windows" task.

Because the installer is not code-signed yet, Windows SmartScreen may show "Windows protected your PC" the first time.
Choose **More info → Run anyway**. If you prefer, you can [build it from source](#build-from-source) instead.

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

1. Start MicSwitcher from the Start menu. The settings window opens.
2. In **Microphones**, choose which device is your **pro**, **headset** and **laptop** mic.
3. Put your headset on and click **Start calibration**. Follow the countdown: stay quiet for 3 seconds, speak into the
   pro mic for 5 seconds, then speak into the headset mic for 5 seconds.
4. Optional: tick **Start with Windows**. Windows asks for administrator permission once.
5. Close the window. MicSwitcher keeps running in the tray; right-click the tray icon to pause or exit.

To check it works, talk into the pro mic, then talk into the headset: the activity log shows each switch and why it
happened.

## Settings explained

| Setting | What it does |
|---------|--------------|
| **Pause automatic switching** | Keeps monitoring but never changes the Windows default. Also available from the tray menu. |
| **Also adjust the pro mic's Windows input volume** | Lets calibration change the pro mic's Windows input volume so speech lands at a good level. |
| **Pro mic voice threshold** | Level (dB) the pro mic must exceed to count as "someone is talking". Set by calibration; lower = more sensitive. Shown as a line on the meter. |
| **Headset mic voice threshold** | The same for the headset mic. |
| **Pro mic quiet for…** | How long the pro mic must be free of voice before a voice on the headset is allowed to take over (0.5 to 15 s). |
| **Voice needed before switching** | How long a voice must be heard on a mic before switching to it (100 to 1500 ms). |
| **Also change the default communications device** | Sets the "default communications device" too, which chat apps like Teams, Discord and Zoom use. |
| **Start with Windows** | Registers a Task Scheduler logon task that starts MicSwitcher hidden in the tray with highest privileges. |

Settings are stored in `%AppData%\MicSwitcher\settings.json` and the log in `%AppData%\MicSwitcher\log.txt`.

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

**Can I use only two mics?**
Yes. Leave the role you do not have set to *(not set)*.

**My headset mic is always on. Is that a problem?**
No, that is exactly the case MicSwitcher is built for. Both mics are watched for voice; the headset only takes over
when the voice is on the headset and not on the pro mic.

**What happens when nobody is talking?**
Nothing. The current default stays as it is until a voice is heard.

**Does it detect "human voice" or just loudness?**
Currently loudness against a calibrated threshold, with smoothing to ignore clicks and bridge pauses between words. A
loud non-voice sound right next to the pro mic can therefore count as voice. See [Contributing](#contributing) if you
would like to help add a real voice-activity model.

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
Raise **Voice needed before switching** or **Pro mic quiet for…**, and check on the meters that the threshold lines sit
clearly above the idle noise and below your speech.

**The pro mic shows no level.**
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
iscc /DAppVersion=0.3.0 /DSourceDir=publish installer\MicSwitcher.iss
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

How the pieces fit: audio capture (`MicMonitor`) produces a level per short block → `EnergyVoiceDetector` decides if it
is voice → `SwitchingEngine` decides which mic should be the default → `AudioDeviceService` sets it in Windows.
`SwitchController` runs this loop ten times per second.

Notes for contributors: the default device is changed through the `IPolicyConfig` COM interface, which Windows itself
uses and tools like SoundSwitch rely on, but which has no official public documentation.

## Contributing

Issues and pull requests are welcome.

- **Bug reports:** please include your Windows version, which kinds of mics you use (USB, XLR via interface,
  Bluetooth, built-in) and the relevant part of the activity log (`%AppData%\MicSwitcher\log.txt`).
- **Code:** keep decision logic in `MicSwitcher.Core` with unit tests; run `dotnet test` before opening a pull request.
- **Ideas:** a real voice-activity detector (WebRTC VAD or a small neural model), a hotkey to force a mic, per-mic
  gain presets, a signed installer.

## Credits

- The placeholder application icon is based on the "microphone" icon from
  [Material Design Icons](https://pictogrammers.com/library/mdi/) by Pictogrammers, licensed under the
  [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0).
- UI built with [WPF UI](https://github.com/lepoco/wpfui) (MIT) and audio handled with
  [NAudio](https://github.com/naudio/NAudio) (MIT).

## License

[MIT](LICENSE)
