# Cable transport checks

This harness exercises Soltex's real `WasapiBridge` and `PcmDspProcessor` through a separately installed virtual cable
(VB-CABLE), using direct Core Audio observation and an isolated recorder child process. It has no Sonar dependency.
VB-CABLE is third-party software; it is not a Soltex driver and does not prove independent virtual buses.

The harness never installs a driver, requests a restart, or changes Windows volume, mute, default devices or app routes.

## Build and default checks

```powershell
dotnet run --project tests/Soltex.Audio.Cable.Tests --artifacts-path <build-dir>
```

The default run (also `--self-test`) opens no audio device. It checks PCM and frequency measurement, DSP smoothing,
gain and mute, argument validation, correlation and lag recovery, and rejection of silence, unrelated or wrong-gain
signals and excessive drift.

`--inspect <new.json>` saves read-only endpoint, session and format observations without opening a stream.
Both render pins of the base cable are checked for other sessions; picking the unused pin does not isolate their shared
capture side.

## Mode 1: cable → Soltex DSP → physical output

Before running: finish any driver installation, record a fresh baseline with `Soltex.Audio.Tool snapshot`, note the
exact active endpoint IDs, and exit Sonar (undoing any routing it owns). The harness requires explicit VB-Audio cable
names, a physical output that is unmuted and above 5 %, compatible formats, and no other app playing into the cable.

Run the built executable directly (not through `dotnet`), with a new receipt path:

```powershell
& <build-dir>\bin\Soltex.Audio.Cable.Tests\debug\Soltex.Audio.Cable.Tests.exe `
  --allow-cable-transport `
  --cable-render '<CABLE playback endpoint ID>' `
  --cable-capture '<CABLE recording endpoint ID>' `
  --physical-output '<physical playback endpoint ID>' `
  --receipt '<new-receipt>.json'
```

The run takes about eight seconds. It plays a quiet 1619 Hz tone (amplitude 0.002) into the cable; the Soltex bridge
captures the cable and renders to the physical device, and a separate child records physical loopback at unity gain.
DSP steps go unity → half → mute → unity. Passing requires −6.02 ± 1 dB at half gain, at least 40 dB suppression when
muted (or down to the measured background), a stable cable input, recovered output within 1 dB, and no bridge failures
or dropped frames.

At the end it closes every stream and child it opened and compares endpoint state, volume, mute and all six default
roles with the start. `Ctrl+C` cancels and then cleans up; do not force-terminate during cleanup. Exit codes: 0 pass,
1 fail, 3 inconclusive. Receipts contain device and process details; keep them out of the repository.

This proves one digital loopback path. It does not prove app routing, acoustic output, five isolated buses, hot-plug,
latency or long-term clock stability.

## Mode 2: microphone return

```powershell
Soltex.Audio.Cable.Tests.exe --allow-microphone-return --microphone <ID> --cable-render <ID> --cable-capture <ID> --receipt <new-receipt>.json
```

Run after mode 1. The bridge sends microphone → DSP → cable while the harness captures the microphone directly as a
reference and the cable as the return. Nothing is monitored or saved as audio; samples stay in bounded memory and the
receipt holds only fitted gains, correlations, formats and cleanup results. Speak for about nine seconds. Sonar must be
stopped, the cable must not be a default device, and both cable render pins must be free of other sessions.

Each of the two gain phases (0.5 and 0.25) needs three aligned windows with correlation ≥ 0.85, fitted gain within
1 dB, enough reference level, a lag inside ±500 ms and lag variation ≤ 5 ms. The muted phase must fall to the noise
floor. Quiet input, ambiguous correlation or missing frames give an inconclusive result. The alignment code is so far
qualified only on synthetic signals.
