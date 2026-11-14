# Soltex Audio (standalone)

This package contains `Soltex.Audio.Desktop.exe`, a small audio controller that opens straight to a six-strip mixer
without the rest of Soltex. The mixer controls an installed SteelSeries Sonar. The separate `Soltex.Audio.Engine`
command-line host processes explicitly chosen Windows capture and playback devices without Sonar; it does not create
audio devices.

## Build

Windows x64 and the .NET 10 SDK are required. From the repository root:

```powershell
pwsh eng/build-audio.ps1 -ArtifactRoot <absolute directory outside the checkout> -Dotnet <path-to-dotnet.exe>
```

The script runs the default (device-free) tests, then writes a new self-contained package. It never removes earlier
packages or installs drivers. `-FrameworkDependent` makes a smaller package that needs the .NET 10 Desktop Runtime.
Tests that play tones or change channel settings are separate and opt-in.

## Mixer

Run `Soltex.Audio.Desktop.exe`. Sonar must already be running with its Game, Chat, Media, Aux and Mic channels
enabled. There is no account or network connection beyond Sonar's local interface.

The top selector switches all four playback channels. With **Link playback outputs** on, changing any playback strip
switches all four; Mic stays separate. Faders take arrow keys and typed values from 0–100; the mouse wheel does not move
them. Mute sends a real command. App menus and drag-and-drop onto a channel set per-app routing; if a running stream
does not move, the row says the app must restart playback.

Meters read Windows device peaks, not fader positions. The Master meter reads the Game channel's physical device and
can include other audio on that device. A dash means no reading; zero alone does not prove audio is flowing.

Closing the window hides it in the tray. Double-click the tray icon or launch the exe again to reopen it. The tray menu
has linked output selection, playback mute and **Quit**; `Ctrl+Q` or `Soltex.Audio.Desktop.exe --quit` also quits.
Settings chooses two favourite outputs and optional `Ctrl+Alt+1` / `Ctrl+Alt+2` shortcuts. Launch at sign-in is off
by default; when on, it adds a `SoltexAudio` value under this user's Run key and starts in the tray. Quitting leaves
Sonar running. Windows default devices are never changed automatically.

**Devices** sets ordered priorities and exclusions for linked playback. Automatic selection starts off and does not
compete with Sonar's own fallback. Manual and external choices stay pinned, and newly connected devices are not added
silently. **Processing** opens the Soltex DSP window, which does not edit Sonar's DSP. Pick explicit devices before
starting. Recording is an explicit, time-limited action that saves only to the file you choose; **Discard test** removes
only that unchanged test file.

## Engine command line

`Soltex.Audio.Engine\Soltex.Audio.Engine.exe --list` lists active devices without opening streams.
`--config <bridges.json>` starts the paths in the file; `Ctrl+C` stops them. See `Soltex.Audio.Engine\README.md`.

## Install and remove

Extract the package anywhere you can write to. It runs unelevated and installs no service, scheduled task, driver or
default-device change. The executables are not code-signed. Do not turn off Windows driver signature checks or Secure
Boot for it.

To remove: turn off launch at sign-in, quit from the tray, stop any engine processes you started, then delete the
package folder. Preferences are in `%LOCALAPPDATA%\SoltexAudio`; keep that folder if you want your settings. Sonar and
its settings are separate. For undoing device changes, see `RECOVERY.md` and the included `Soltex.Audio.Tool`.
