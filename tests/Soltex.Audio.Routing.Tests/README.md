# App routing probe

This project builds `Soltex.OwnedAudioRouteProbe.exe`. It tests the Windows per-app audio policy used by
`src/Soltex.Audio.WindowsRouting` (see that README for the product API), but only on child processes the probe starts
itself. It never changes another app's routing or a Windows default device.

Always run the built `Soltex.OwnedAudioRouteProbe.exe`, not `dotnet <assembly>`: a shared `dotnet.exe` host identity
would break the isolation and persistence checks. Journal paths must be new files on a local drive.

## Commands

```powershell
$probe = '<build-dir>\bin\Soltex.Audio.Routing.Tests\debug_win-x64\Soltex.OwnedAudioRouteProbe.exe'
& $probe --self-test
& $probe --read-only
```

`--self-test` runs the deterministic routing, journal, undo, identity and profile checks against a fake policy; it
opens no device or native policy interface. `--read-only` opens a silent stream for its own process and reads its six
policy values.

Silent set-and-clear check (a new journal each time):

```powershell
& $probe --verify-clear --endpoint-id '<render endpoint ID>' --journal '<new-journal>.jsonl'
```

Tone routing and restart check:

```powershell
& $probe --run-owned-routing --allow-owned-route-changes `
  --game-id '<first render endpoint ID>' `
  --chat-id '<second render endpoint ID>' `
  --physical-id '<physical output endpoint ID>' `
  --journal '<new-journal>.jsonl'
```

The child plays a 1877 Hz tone at 0.002 peak to the default device. The parent routes it to the first endpoint, then
the second, and checks the policy readback and the active session's endpoint. If the stream does not move within
2.5 seconds, the run records that and reopens only its own stream; it does not count that as a seamless move. It then
restarts the child at the same path and checks that the route persisted for the new process.

Loopback probes measure the tone on both endpoints and, when given, the physical output. The selected endpoint must be
20 dB above its baseline and the other at least 26 dB below it. Only amplitude windows are saved, never raw audio.

At the end the probe restores the two render roles to their exact saved values (including "unset"), checks all six
policies and all six defaults, and stops its child. Unexpected values are kept and reported. Journals are created
exclusively and flushed on every write.

Recovery after an interrupted run, for this probe and its own journal only:

```powershell
$digest = (Get-FileHash -LiteralPath $probe -Algorithm SHA256).Hash
& $probe --restore-from-journal '<interrupted-journal>.jsonl' `
  --expected-executable-sha256 $digest `
  --journal '<new-journal>.jsonl'
```

Recovery accepts only the same executable path and only values the original journal itself wrote.

## Interface

The policy interface is `Windows.Media.Internal.AudioPolicyConfig` (IID `ab3d4648-e242-459f-b02f-541c70306324`).
Only `SetPersistedDefaultAudioEndpoint` and `GetPersistedDefaultAudioEndpoint` (vtable slots 25 and 26, after the three
`IUnknown`, three `IInspectable` and 19 preceding methods) are called, with a PID, data-flow and role enums and an
`HSTRING`; every `HRESULT` is checked. The same layout is declared publicly by
[EarTrumpet](https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/Interop/MMDeviceAPI/IAudioPolicyConfigFactoryVariantFor21H2.cs).

This is undocumented and can change between Windows releases, so every caller checks the capability first and fails
closed. A process must have an audio session before the getters succeed.

## Findings

- Windows persists the route by app identity (executable path), not by PID: a restarted process at the same path keeps
  it. Never store a PID-to-endpoint mapping.
- A WASAPI stream that is already open may not move until the app reopens it. The UI should say so instead of claiming
  a live move.
- Setting a null value clears a route; the probe restores "unset" exactly.

This does not prove acoustic playback, or automatic moves for every app. Packaged apps and app upgrades need their own
testing.
