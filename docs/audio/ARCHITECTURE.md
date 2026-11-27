# Audio architecture

The Audio page in `Soltex.exe` has four tabs: **Mixer** (Soltex channels, stream mix, processing), **Microphone**
(guided setup and processing), **Apps** (Windows sessions, per-app volume and output) and **Sonar** (shown only when
SteelSeries Sonar is installed). Windows sessions, app routing and the Soltex channel host do not depend on any vendor
software.

## Components

| Project | Role |
| --- | --- |
| `Soltex.Audio` | Core Audio endpoints, defaults, volume, mute and meters on one MTA worker; output priority policy. |
| `Soltex.Audio.Sessions` | App sessions with identity-checked volume and mute. |
| `Soltex.Audio.WindowsRouting` | Sends one app to a chosen output through the Windows audio policy interface, with a journal and selective undo. |
| `Soltex.Audio.Engine` | DSP and WASAPI bridge (`Soltex.Audio.Engine.Core.dll`); NAudio.Wasapi 2.3.0. `Soltex.Audio.Engine.Cli` builds the `Soltex.Audio.Engine.exe` command-line host. |
| `Soltex.Audio.Host` | Five-channel graph, stream mix, profiles and the pipe protocol. `Soltex.Audio.Host.Cli` builds `Soltex.Audio.Host.Service.exe`. |
| `Soltex.Audio.UI` | Mixer workspace, microphone setup, processing editor, record/replay window. |
| `Soltex.Audio.Sonar` | Optional adapter for an installed Sonar, using its local loopback HTTP interface. No SteelSeries file is bundled. |
| `Soltex.Audio.Tool` | Command-line endpoint snapshot, volume/mute journal and selective recovery tool. |
| `Soltex.Audio.App` | Standalone audio app (`Soltex.Audio.Desktop.exe`) hosting the same workspace. |
| `Soltex.Audio.Transport`, `Soltex.Audio.OwnedEndpoints`, `Soltex.Audio.Broker`, `native/Soltex.Audio.Driver` | Source for Soltex's own virtual endpoints. Unsigned, never installed by a build. |

## Channel host

`Soltex.Audio.Host` runs in `Soltex.Audio.Host.Service.exe`, an unelevated helper the UI starts on demand. The UI talks
to it over a current-user named pipe with a per-launch token, a 512 KiB frame limit and a fixed operation list. The
helper starts stopped, opens nothing until the user starts audio, and exits when its parent exits.

A profile has five channels (Game, Chat, Media, Aux, Microphone) and a stream-mix section. Loading, importing or
applying a profile while stopped opens nothing. The requested profile is kept apart from the confirmed state, and the
UI shows confirmed state.

Two running modes exclude each other:

- **Channel mode:** one capture → DSP → render path per enabled channel, with output priorities, exclusions, a manual
  pin, bounded retries and measured output peaks. Playback master and Game/Chat balance do not touch the microphone.
- **Stream mode:** one capture and DSP per source feeds two bounded queues with their own gain and mute, summed into
  separate **Personal** and **Stream** outputs with a final ceiling. Master and balance affect Personal only. Changing
  sources or outputs requires stopping first.

Switching outputs opens the new path muted, checks it, ramps the old path down, retires it, then ramps the new path in.
Known shared-cable identities and feedback loops are refused. Sonar endpoints are refused in this graph.

The microphone test is a separate, explicitly started 10-second capture that reports per-input peak and RMS; it does
not play back or save audio. `DspSettings.InputChannel` picks one hardware input and centres it.

Every capture-to-render path, and each send of the stream mix, converts sample rates with
`AdaptiveResamplingSampleProvider`: a 64-tap Kaiser-windowed sinc that also absorbs the drift between the capture and
render device clocks. A control loop trims the ratio by at most ±0.5 % to hold the bus between them at a small target
fill, so independent clocks no longer cause periodic dropouts or slowly growing delay; a real underrun is concealed
with a short fade and counted. Details and measurements are in `src/Soltex.Audio.Engine/README.md`.

## Endpoints

Soltex does not create audio devices by itself. Channels use endpoints that already exist: physical devices, or
virtual cables the user installed (for example VB-CABLE). Each independent bus needs its own transport; one cable
cannot carry several buses.

The owned-endpoint source (`native/Soltex.Audio.Driver`) defines six endpoints: Game, Chat, Media, Aux (render) and
Microphone, Stream (capture). It builds an unsigned driver that no script installs. Running it would also need the
broker service and production signing.

## Ownership and lifetime

- Core Audio objects live on one MTA worker per service; notification callbacks only enqueue work.
- Hiding the window or leaving the Audio page stops UI polling, not running audio.
- Quitting Soltex stops the channel host and every stream Soltex owns. An installed Sonar keeps running; Soltex never
  stops or reconfigures it except through explicit Sonar-tab controls.
- Profiles and presets are versioned JSON saved atomically under `%LOCALAPPDATA%\Soltex\Audio`. Device IDs are
  machine-specific; a missing device stays shown as unavailable.
- App routing changes and the endpoint tool's volume/mute commands write a journal first, and undo restores only
  values that still hold what Soltex wrote. Volume and mute set from the mixer are ordinary user settings and are not
  reverted on exit.
- Soltex never changes Windows default devices on its own.

## DSP

Original code: ten-band parametric EQ, gate, compressor, downward expander, stationary noise suppression (manual noise
floor, about 20–40 ms of added delay), stereo width/pan/crossfeed/mono/swap, gain, headroom and an output ceiling.
Two options are off by default: **Smooth background** switches noise suppression to a decision-directed gain rule that
leaves a steadier residual instead of isolated warbling, and the **Safety limiter** replaces the hard clamp at the
ceiling with a channel-linked lookahead limiter (about 1.5 ms, true-peak aware) that is an exact delay below the
ceiling. Noise suppression is not machine-learning speech isolation, and the stereo tools are not HRTF. Details are in
`src/Soltex.Audio.Engine/README.md`.

The Sonar tab's percentages map to Sonar's own controls; Soltex's DSP uses dB. The two engines are separate and their
settings are never combined.

## Limits

Not yet qualified on hardware: the owned driver, stream mode end to end, acoustic latency, long-run clock drift
between real devices (the drift correction is tested against simulated clocks), hot-plug of wireless devices, and
mixed-DPI layouts. Automated tests use fake devices, real local pipes and known PCM signals.
