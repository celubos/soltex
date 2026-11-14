# Soltex

Run **Soltex.exe**. It lives in the notification area; closing the window hides it. Choose **Audio** in the sidebar
for the mixer, microphone setup and per-app controls, or run `Soltex.exe --audio` to open that page directly.
`Install-Shortcuts.ps1` adds a per-user Start menu shortcut (add `-Desktop` for a desktop one) that opens Soltex on
the Audio page.

## Audio page

- **Mixer:** Soltex channels (Game, Chat, Media, Aux, Microphone) with per-channel processing, output priorities and a
  separate stream mix. Nothing plays until you apply routing and choose **Start audio**. See
  [INDEPENDENT-HOST.md](INDEPENDENT-HOST.md).
- **Microphone:** guided setup, a 15-second input test, and processing for your voice.
- **Apps:** Windows app sessions for a playback device, with per-app volume and mute (read back from Windows) and
  **Route…** to send an app to another output. Windows default devices are never changed. System sounds and apps
  Windows restricts are read-only. An app appears once it has opened an audio session.
- **Sonar:** shown only when SteelSeries Sonar is installed. It controls Sonar's channels, outputs and ChatMix through
  Sonar's own local interface. No SteelSeries file is included.

Soltex does not create audio devices. Channels use devices you already have: physical devices or a virtual cable you
installed. Use headphones when sending a microphone to speakers to avoid feedback.

## Starting and quitting

- Closing the window hides Soltex. Reopen it from the tray icon or by launching `Soltex.exe` again.
- `Ctrl+Q`, **Quit Soltex** in the tray menu, or `Soltex.exe --quit` exits Soltex and stops the audio it started.
  An installed Sonar keeps running.
- `--tray` starts hidden. `--profile-dir <absolute path>` keeps all settings in a separate folder, for testing.
- Start at sign-in and starting audio after sign-in are off until you turn them on in Settings.
- Settings live in `%LOCALAPPDATA%\Soltex`; audio profiles and presets are in its `Audio` folder.

Nothing is installed: no driver, service or scheduled task. Soltex runs as your user and asks for administrator rights
only when you apply a change that needs them.

## This package

This is an unsigned Windows x64 build. Keep the extracted folder together. `BUILD.json` identifies the source
revision, `VERIFICATION.json` lists the checks run before packaging, and `SHA256.json` lists the hash of every file.

The package also contains the command-line tools `Soltex.Audio.Engine` and `Soltex.Audio.Tool` (each with a README),
and the audio broker service with its deployment checker (`OwnedAudioDeployment\`). The broker is for Soltex's own
virtual devices, which are not released yet; nothing registers or starts it.

Before changing default devices or app routes by hand, read `RECOVERY.md` in this folder. Never apply an old
snapshot over newer choices.

Build from source: <https://github.com/celubos/soltex>.
