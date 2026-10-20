# MicSwitcher

A small Windows tray app that automatically switches your **default microphone** between a main pro mic,
a headset mic and a laptop mic, based on whether anyone is actually talking into the pro mic.

> **Status: v0.1, early.** The switching logic is unit tested and the project builds in CI, but the Windows audio
> parts (device switching, live metering, startup task) still need real-world testing on your hardware.
> Please open an issue if something misbehaves; the activity log inside the app helps a lot.

## The problem it solves

You have three microphones:

| Role | Example | Behaviour |
|------|---------|-----------|
| **Pro mic** | Studio/USB/XLR mic | Preferred. Default whenever a voice is heard on it. |
| **Headset mic** | Mic on your headset (always on) | Takes over when the pro mic hears no voice. |
| **Laptop mic** | Built-in | Last resort, only when the other two are not connected. |

Windows can only have one default input, and the headset mic is always live. MicSwitcher listens to the pro mic and
flips the Windows default for you.

## How it decides

1. **Pro mic missing** → headset mic; if that is missing too → laptop mic.
2. **Pro mic present and a voice is heard** → pro mic is the default.
3. **No voice on the pro mic for N seconds** (default 3) → headset mic becomes the default.
4. **Voice returns on the pro mic** and lasts a moment (default 300 ms) → pro mic is the default again.
5. A minimum time between switches (default 1 s) prevents flapping. The laptop mic is never picked while the pro or
   headset mic is available.

The pro mic is monitored continuously, even while it is not the default (Windows lets several apps read a mic at the
same time). The headset and laptop mics are only metered while the settings window is open, so Windows' "microphone in
use" indicator is not lit unnecessarily.

## Features

- Runs in the **system tray**; closing the window keeps it running. The tray icon colour shows the active mic
  (green pro, amber headset, red laptop, grey paused/none).
- **Settings window**: pick each mic, live level meters, voice indicator, activity log.
- **Calibration** for the pro mic: measures your room noise and speaking level, sets the voice threshold and (optionally)
  adjusts the Windows input volume so your voice lands at a good level.
- Tunable **silence timeout** and **switch-back delay**, and a **pause** switch.
- Optionally also switches the **default communications device** (what Teams, Discord and Zoom use).
- **Start with Windows** through a Task Scheduler task with *highest privileges*, so it starts at logon without a UAC
  prompt.
- Settings are stored in `%AppData%\MicSwitcher\settings.json`, the log in `%AppData%\MicSwitcher\log.txt`.

## Getting started

### Download

Grab `MicSwitcher.exe` from the [Releases](../../releases) page (or from the latest CI run's artifacts). It is a single
self-contained file; no .NET install needed.

### First run

1. Start `MicSwitcher.exe`. The settings window opens.
2. Choose your **pro**, **headset** and **laptop** mic in the three drop-downs.
3. Click **Calibrate pro mic…**, stay quiet for 3 seconds, then speak normally for 6 seconds.
4. Tick **Start with Windows** (one administrator prompt) if you want it to run at every logon.
5. Close the window. MicSwitcher keeps running in the tray.

### Build from source

Requires Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/YousefMohsen0/MicSwitcher.git
cd MicSwitcher
dotnet test                       # engine, voice detector, calibration and settings tests
dotnet run --project src/MicSwitcher.App

# single-file executable
dotnet publish src/MicSwitcher.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

> "Start with Windows" needs the published `MicSwitcher.exe`; it is not available when running through `dotnet run`.

## Why "high privileges" is done with a scheduled task

Changing the default microphone does not strictly require administrator rights, but a process that is *not* elevated
cannot reliably interact with elevated apps and a registry "Run" entry cannot start an elevated program without a UAC
prompt. A Task Scheduler logon task set to *run with highest privileges* is the standard way to start an elevated
program silently at logon. The app itself is built to run fine unelevated too (`asInvoker`), so you can try it without
installing anything.

To remove autostart, untick the option in the app (or delete the `MicSwitcher` task in Task Scheduler).

## Project layout

```
src/MicSwitcher.Core/       Pure logic: switching engine, voice detector, calibration, settings (no Windows code)
src/MicSwitcher.App/        WPF tray app: NAudio capture, IPolicyConfig device switching, startup task, UI
tests/MicSwitcher.Core.Tests/  xUnit tests for everything in Core, runnable on any OS
.github/workflows/          CI: build, test, publish single-file exe, create releases from v* tags
```

How the pieces fit: `MicMonitor` (WASAPI capture) → level per audio block → `EnergyVoiceDetector` → `SwitchingEngine`
decides → `AudioDeviceService` sets the Windows default. `SwitchController` ties these together on a 100 ms tick.

## Limitations and ideas

- Voice detection is **level based** (calibrated threshold + onset/hangover smoothing), not a true speech model, so a
  loud non-voice sound near the pro mic counts as "voice". The detector sits behind a tiny API so WebRTC VAD or a small
  neural VAD can replace it.
- Setting the default device uses the undocumented-but-long-stable `IPolicyConfig` COM interface (the same one tools
  like SoundSwitch use); there is no official public API for it.
- Bluetooth headsets may change audio quality when their mic becomes active; this is a Windows/Bluetooth behaviour.
- Possible next steps: hotkey to force a mic, per-mic gain presets, per-app overrides, signed installer.

## Contributing

Issues and pull requests are welcome. Please keep new logic in `MicSwitcher.Core` with unit tests where possible.

## License

[MIT](LICENSE)
