# Sonar transport checks

This console harness checks the optional Sonar adapter against real audio. It uses NAudio.Wasapi 2.3.0 to play four
quiet tones into Sonar's Game, Chat, Media and Aux devices and measures them on two physical outputs through WASAPI
loopback. It never routes other apps, changes Windows default devices or master volume, installs a driver, or records
audio. Playing directly into a device does not prove per-app routing.

## Commands

Build:

```powershell
dotnet build tests/Soltex.Audio.Transport.Tests --artifacts-path <build-dir>
```

Device-free checks (signal detector, PCM and tone source):

```powershell
dotnet <build-dir>\bin\Soltex.Audio.Transport.Tests\debug\Soltex.Audio.Transport.Tests.dll --self-test
```

Read endpoint identities, readiness and formats without changing anything: `--inspect`, `--inspect-formats`.

The live modes change Sonar channel volume, mute, outputs or ChatMix while they run, so any other sound on those
channels is affected. Run them only when no other app or test is changing these settings. Master must be unmuted and at
least 0.1, all four Sonar devices and both physical outputs must be active, and the microphone is never touched. Use a
new journal path for every attempt.

```powershell
# Volume, mute and linked output switching
dotnet <build-dir>\bin\Soltex.Audio.Transport.Tests\debug\Soltex.Audio.Transport.Tests.dll `
  --run-live --allow-channel-state-changes `
  --output-id '<physical output endpoint ID>' `
  --alternate-id '<second physical output endpoint ID>' `
  --journal '<new-journal>.jsonl' `
  --amplitude 0.003

# ChatMix balance only
... --run-chatmix --allow-channel-state-changes --output-id '<physical output endpoint ID>' --journal '<new-journal>.jsonl'

# 20 linked output switches
... --run-switches --allow-channel-state-changes --output-id '<ID>' --alternate-id '<ID>' --journal '<new-journal>.jsonl'
```

## What is measured

- Tones are 997, 1451, 1999 and 2557 Hz at a default peak of 0.003 (allowed 0.0001–0.01). Channel levels are capped at
  the lower of the current master and 0.5. Two silent streams keep loopback clocks running; missing capture data is a
  failure, never silence.
- A Hann-windowed detector measures each tone in 100 ms windows. Baselines must be at least 20 dB below the tones.
- Halving a channel must attenuate its tone by more than 1 dB; muting must suppress it by more than 26 dB; other tones
  must stay within 2 dB.
- A linked output switch must move all four tones to the second output (20 dB above baseline) and drop them by 26 dB
  at the first. The reacquisition time on the new output must be under 1500 ms; this is not a latency measurement.
- ChatMix mode steps the balance through centre, +0.5, +1, centre, −0.5, −1, centre and compares Game/Chat levels with
  Sonar's measured balance curve within 2 dB.
- Switch mode alternates outputs 20 times, ending on the original.

## Journal and restoration

The journal is created exclusively and flushed after every record: initial state, the six Windows defaults, each
intended write, readbacks, amplitude windows, failures, restoration and final state. It holds device and app metadata
but no audio; keep it out of the repository.

On exit the tones stop first. Only changed fields are restored, and only if they still hold the harness's value or the
original; anything changed by someone else is kept and reported. Restoration has its own 40-second budget, and an
incomplete restore fails the run. `Ctrl+C` cancels and then restores; do not force-terminate. After a crash, use the
journal's first record to restore by hand.

This harness does not test the microphone, transport without Sonar, acoustic playback, latency or app routing.
