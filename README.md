# MicSwitcher

**Automatically switch your default Windows microphone between a main mic, a headset mic and a built-in mic, based
on whether anyone is actually talking into the main one.**

MicSwitcher is a small tray app for Windows 10/11. If you own a good desk microphone *and* a headset, you have probably
run into this: Windows can only use one input at a time, so you either keep digging through Sound settings, or you end
up with the wrong mic live. MicSwitcher listens to your main mic and flips the default for you.

- Talk into your desk mic and it is used.
- Walk away, put the headset on and speak into that instead, and the headset mic takes over automatically.
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
2. **Pro mic connected and a voice is heard:** the pro mic is the default.
3. **No voice on the pro mic for a few seconds** (default 3 s): the headset mic becomes the default.
4. **Voice returns on the pro mic** and lasts a moment (default 0.3 s): the pro mic is the default again.
5. A short minimum time between switches prevents flip-flopping, and the laptop mic is never chosen while the pro or
   headset mic is available.

The pro mic is monitored continuously, even while it is not the default (Windows lets several apps listen to a mic at
once). The headset and laptop mics are only metered while the settings window is open, so Windows' "microphone in use"
indicator is not lit more than necessary.

## Features

- Runs quietly in the **system tray**. The icon colour shows the active mic: green = pro, amber = headset,
  red = laptop, grey = paused or none.
- **Settings window** with mic selection, live level meters, a "voice" indicator and an activity log.
- **Calibration**: measures your room noise and your speaking level, sets the voice threshold, and can optionally
  adjust the Windows input volume of the pro mic so your voice lands at a good level.
- Adjustable **silence timeout** and **switch-back delay**, plus a **pause** switch.
- Optionally changes the **default communications device** too (the one Teams, Discord and Zoom use).
- **Start with Windows**, running in the background with administrator rights and no UAC prompt at each logon.
- Single self-contained executable, no installer and no .NET runtime required.

## Install

**Requirements:** Windows 10 or 11, 64-bit.

1. Download `MicSwitcher.exe` from the [Releases](https://github.com/YousefMohsen0/MicSwitcher/releases) page.
2. Put it somewhere permanent (for example `C:\Program Files\MicSwitcher\` or a folder in your user profile) and run it.
   If you plan to use "Start with Windows", do not move it afterwards.

Because the executable is not code-signed yet, Windows SmartScreen may show "Windows protected your PC" the first time.
Choose **More info → Run anyway**. If you prefer, you can [build it from source](#build-from-source) instead.

## Quick start

1. Run `MicSwitcher.exe`. The settings window opens.
2. In **Microphones**, choose which device is your **pro**, **headset** and **laptop** mic.
3. Click **Calibrate pro mic…**. Stay quiet for 3 seconds, then speak normally, close to the mic, for 6 seconds.
4. Optional: tick **Start with Windows**. Windows asks for administrator permission once.
5. Close the window. MicSwitcher keeps running in the tray; right-click the tray icon to pause or exit.

To check it works, speak into the pro mic, then stay silent for a few seconds: the activity log shows each switch and
why it happened.

## Settings explained

| Setting | What it does |
|---------|--------------|
| **Pause automatic switching** | Keeps monitoring but never changes the Windows default. Also available from the tray menu. |
| **Also adjust the Windows input volume** | Lets calibration change the pro mic's Windows input volume so speech lands at a good level. |
| **Voice threshold** | Level (dB) the pro mic must exceed to count as "someone is talking". Set by calibration; lower = more sensitive. |
| **Switch to headset after silence** | How long the pro mic must be free of voice before the headset mic takes over (1 to 30 s). |
| **Switch back after voice for** | How long a voice must be heard on the pro mic before it is taken back (100 to 1500 ms). |
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
No, that is exactly the case MicSwitcher is built for. Only the pro mic is analysed for voice; the headset mic is
simply switched to when the pro mic hears nobody.

**Does it detect "human voice" or just loudness?**
Currently loudness against a calibrated threshold, with smoothing to ignore clicks and bridge pauses between words. A
loud non-voice sound right next to the pro mic can therefore count as voice. See [Contributing](#contributing) if you
would like to help add a real voice-activity model.

## Troubleshooting

**Nothing switches.**
Check that switching is not paused (status card and tray menu), that each mic is assigned in the drop-downs, and that
the badge next to each mic does not say *not found*. The activity log shows every decision and any error.

**It switches to the headset while I am talking into the pro mic.**
The voice threshold is too high for your speaking level. Run **Calibrate pro mic…** again and speak at your normal volume
and distance, or lower the threshold slider a few dB.

**It never switches to the headset.**
The threshold is too low, so background noise counts as voice. Recalibrate in a quiet moment, raise the threshold, or
shorten the silence timeout.

**The pro mic shows no level.**
Another program may be using the mic in *exclusive mode*, which stops other apps (including MicSwitcher) from listening.
In Windows Sound settings → the device's properties → Advanced, untick "Allow applications to take exclusive control".

**"Start with Windows" fails.**
Accept the administrator prompt when it appears. It also only works with the downloaded or published `MicSwitcher.exe`,
not when the app is started through `dotnet run`.

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

# single-file executable
dotnet publish src/MicSwitcher.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The core logic and its tests target plain .NET 8 and run on any OS; only the app project needs Windows.

## Project layout

```
src/MicSwitcher.Core/            Pure logic: switching engine, voice detector, calibration, settings (no Windows code)
src/MicSwitcher.App/             WPF tray app: NAudio capture, device switching, startup task, UI
tests/MicSwitcher.Core.Tests/    xUnit tests for everything in Core
.github/workflows/               CI: build, test, publish the exe, create releases from v* tags
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

## License

[MIT](LICENSE)
