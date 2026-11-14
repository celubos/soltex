# Independent Soltex audio engine

This library and CLI implement PCM processing without GG, Sonar, vendor binaries, or vendor DSP. The WASAPI bridge uses explicitly supplied capture/render endpoint IDs. It does **not** install virtual endpoints or supply a signed driver.

## Processing contract

`PcmDspProcessor(sampleRate, channels, settings)` processes interleaved float samples in place via `Process(Span<float>)`. One audio thread owns each instance. `UpdateSettings` validates and copies controls from any control thread, applying the latest complete snapshot at the next block. Separate instances have separate channel/filter/dynamics history.

The chain is optional physical input selection → input sanitation → optional spectral noise suppression → ten independent EQ bands → linked-channel gate, downward expander and peak compressor → stereo image controls → smoothed gain/mute/headroom → hard output ceiling. EQ supports Peak, LowShelf, HighShelf, HighPass, LowPass, Notch, per-band frequency/gain/Q/bypass and global bypass. Frequencies must be below 0.49×sample rate. Gain applies to peaks/shelves; cut/notch types use frequency/Q. Shelf Q controls resonance. EQ changes crossfade old/new banks over 10 ms; rapid repeated updates may restart that transition. Gain/mute use a one-pole transition, default 20 ms. Headroom defaults to −6 dB and the output ceiling to 0.98. Ceiling clipping is a final guard, not a transparent lookahead limiter; avoid routine clipping with input/headroom controls.

Gate controls include opening threshold, closing hysteresis, attack, hold, release and closed gain. The compressor has threshold/ratio/attack/release/makeup with a linked peak detector. The accurately named **downward expander noise attenuation** has a user-set threshold, ratio, maximum attenuation and attack/release. It attenuates quiet passages; it cannot isolate speech from concurrent noise and is not AI noise cancellation.

`InputChannel` is null by default, preserving the original channel layout. Set 0 for hardware Input 1 or 1 for Input 2 on a stereo interface. The chosen sample is duplicated at unity to every processor channel before processing, centering a microphone connected to only one interface input. The actual capture format must contain the selected index; invalid selections are rejected, never silently substituted.

`NoiseSuppression` adds original frequency-selective suppression for a stationary noise floor, including noise concurrent with stronger signal components. It is **not AI, speech recognition, echo cancellation, or a reproduction of Sonar's proprietary algorithm**. Controls are `Enabled` (default false), `NoiseFloorDb` (RMS dBFS, −90 to −20, default −50), and `ReductionDb` (0 to36, default18). Set the floor from a quiet-room measurement; an excessively high floor removes quiet speech and can create artifacts. No automatic training or microphone calibration is implied. The processor uses a square-root Hann STFT, 50% overlap, smoothed bounded per-bin Wiener-like gains, and conjugate-symmetric reconstruction. Every channel has independent history. FFT sizes are the next power of two at least20ms long: algorithm delay is20–40ms depending on sample rate,1024frames/21.33ms at48kHz, exposed as `NoiseSuppressionLatencyFrames`. Device/buffer latency is additional. Enabling/disabling changes this delay; use while stopped for a seamless timeline. Floor/reduction and gain updates while enabled preserve buffered audio. Zero reduction reconstructs the delayed input. It adds no delay or FFT work when disabled.

`Stereo` adds width(0–2, default1), balance/pan(−1 left to+1 right, center unity), mono fold-down, channel swap, and headphone crossfeed(0–1). It is disabled by default and applies only to exactly two channels; other layouts remain unchanged. Width is mid/side scaling; mono averages both channels. Pan attenuates the opposite side without boosting. Crossfeed mixes the opposite channel through a700Hz one-pole low-pass and0.3ms delay, normalized against correlated low-frequency gain. Controls smooth over10ms. These are stereo/headphone presentation controls, **not HRTF, positional surround, head tracking, or multichannel virtualization**. Width above1 can raise peaks, so retain headroom.

All owned spectral arrays, FFT tables and stereo delay buffers are allocated during control-thread preparation, with no locks, file I/O or allocations in their sample processing. Synthetic tests measure stationary noise reduction and mixed tone/noise SNR, exact zero-reduction reconstruction and delay across8/16/44.1/48/96/192kHz, partition invariance, state retention after control updates, channel selection, stereo response, numeric recovery and allocation-free callbacks. The fixed seeded48kHz test measured9.43dB stationary noise reduction,8.99dB SNR improvement and0.001dB tone amplitude change; these are fixture results, not microphone speech-quality or Sonar-parity claims.

`ChatMix.Gains(balance)` returns independent game/chat multipliers: −1 favors game, +1 favors chat, 0 leaves both at unity. A cosine attenuation curve reduces only the unfavored bus. It is a center-unity balance law, not an equal-power crossfade or a mixer. Apply multipliers to separate bus gains with sufficient summed headroom.

`BoundedFloatBus` implements NAudio's sample provider. Writes/reads are frame-aligned and synchronized; overflow discards oldest frames, underrun produces silence. Counters expose both. `DspSampleProvider` composes a bus and processor. `PcmDecoder` supports PCM16/24/32 and IEEE float32. A final output guard protects against resampler overshoot.

## Explicit device host

The reusable `Soltex.Audio.Engine` project builds `Soltex.Audio.Engine.Core.dll`. Its public namespace and processing API remain `Soltex.Audio.Engine`; desktop applications and tests reference this library. The separate `Soltex.Audio.Engine.Cli` project links the existing command-line entry point and builds the unchanged `Soltex.Audio.Engine.exe` name. It is packaged in the same `Soltex.Audio.Engine/` folder. Keeping executable runtime/apphost files out of the UI dependency graph permits self-contained WPF publishing without duplicate executable assets.

Run `Soltex.Audio.Engine --list` to enumerate active endpoints. `--config <path.json>` starts one to five explicit bridges; Ctrl+C closes owned streams. Defaults, volume policies, installed drivers and vendor services are never changed. Example configuration (replace both placeholders with actual opaque IDs):

```json
[
  {
    "CaptureEndpointId": "CAPTURE-SIDE-OF-YOUR-VIRTUAL-CABLE",
    "RenderEndpointId": "PHYSICAL-SPEAKERS-RENDER-ID",
    "LatencyMs": 40,
    "BufferMs": 200,
    "Dsp": { "GainDb": 0, "HeadroomDb": -6, "Mute": false }
  }
]
```

For the microphone path choose a physical microphone capture ID and a supplied cable's render ID; applications then capture from that cable's capture side. The host uses ordinary capture, not render loopback. It supports mono/stereo, rate conversion, shared-mode WASAPI and bounded queues. It must not be routed into its own input through another application. Each configured capture has one owner in this process. Several streams to one output are mixed by Windows; this host currently has no aggregate master limiter.

`WasapiBridge` owns all handles and workers, starts once, exposes `Processor` controls and `Status` queue/clipping/failure telemetry, and closes them on disposal. Construct and control on one MTA thread, without a synchronization context. Endpoints must already be active. There is no automatic route selection, reconnection, clock-drift PLL or sample-accurate cross-device synchronization. Clock mismatch is bounded by recorded drop/silence events; hardware qualification and recovery remain required. Latency values request buffer behavior and are not measured end-to-end latency claims. No live devices were opened by the deterministic tests.

## Personal and stream mixes

`WasapiStreamMixer` supplies an actual independent WASAPI fanout graph. Its `WasapiStreamMixConfiguration` contains one to five unique `StreamMixSourceConfiguration` records and two render endpoint IDs, Personal and Stream. Each source opens one capture, runs its shared `DspSettings` once, and publishes the resulting PCM into separate bounded Personal and Stream queues. Each send has its own smoothed `StreamMixSendSettings(GainDb, Mute)`; gains range from −80 to +12 dB. Render workers convert each send to that destination's mono/stereo format and sample rate, sum all sends, and apply an aggregate output ceiling. This lets a microphone be audible in Stream while muted in Personal, or a media bus be heard privately while excluded from Stream.

Construct and control the mixer on one MTA worker without a UI synchronization context. Call `Start()` explicitly. Use `UpdateSource(id, dsp, personal, stream)` for processing and send changes. Endpoint/source topology changes require stopping, disposing and constructing another mixer. `Stop()` does not authorize a second start on the same instance. `Status` reports actual selected endpoint IDs, processed input frame counts, independent queue drop/underrun counters, output peaks, aggregate clipping counts and stream failures. Stopping/disposal attempts all owned resources and surfaces cleanup failures. Profiles, automatic endpoint recovery and mutual exclusion with another host are the caller's responsibility.

The desktop host must stop its ordinary graph before owning these captures; duplicate capture IDs within this graph are rejected before native enumeration. The caller must also reject known cable feedback loops. A Stream render endpoint normally needs a separately supplied virtual cable whose capture side is selected by the broadcasting application. This feature neither creates that endpoint nor silently configures the broadcasting application or Windows defaults. Selecting the same hardware for both render outputs intentionally recombines both mixes at that device.

`StreamMixPcmSource` and `StreamMixSumProvider` are the same offline-testable PCM path used by the WASAPI adapter. Tests measure independent send gains/mutes, Game/Mic isolation between outputs, clipping after summation, exactly one shared DSP pass, bounded behavior when one output stops consuming, zero allocations in the owned processing/publish/mix callbacks, and duplicate-capture rejection. The existing bounded queues use short locks, and external NAudio/resampler/driver code is outside the owned allocation claim. Device clocks are independent: queued drops and inserted silence are reported, not disguised as sample-accurate synchronization. Real microphone-return transport, multiple-device clock drift, hotplug and physical latency still require live qualification.

## Independent desktop workspace and microphone check

`EngineControlWindow` in the desktop project provides a separately owned MTA control worker, explicit endpoint selectors, start/stop, the ten-band editor, a graph calculated from these exact coefficients, gain/mute and dynamics. It starts stopped with no endpoint selected. Closing the window stops and disposes owned streams before joining the worker. A version1 JSON preset contains DSP controls only; save/load/reset never starts capture. Last valid controls are atomically persisted under `%LOCALAPPDATA%\SoltexAudio\engine-dsp-v1.json`. This is normal small application state, not a capture location. Tests supply a separate preference path.

The explicit **Record input · 10s** action asks for a new WAV destination and records the selected capture endpoint through this DSP without monitoring. `MicrophoneTestRecorder` enforces a frame bound; the workspace also applies a ten-second wall-clock stop. The output is a local float WAV. `MicrophoneTestReplay` plays that recording only after a separate click, only to the selected render endpoint, with fixed −12 dB attenuation and a 0.25 sample ceiling. Neither action changes system volume or defaults. The user still controls physical output volume. Stop and window close terminate these sessions. The workspace prevents overlapping bridge, recorder and replay sessions.

The recording callback only copies complete raw PCM frames into a preallocated two-second single-producer ring and signals its consumer. A dedicated worker decodes, processes and writes the WAV; overflow stops acceptance with an explicit failure instead of silently dropping recording frames. The final input packet is trimmed to the exact duration. Stop first joins capture and its final stopped handler, then drains accepted audio, joins the writer and finalizes the owned file. No callback joins a worker or performs file I/O. Construction/control requires a worker without a synchronization context, preventing stopped-event dispatch from waiting on the UI. A stalled filesystem can still delay writer shutdown; this is not a bounded shutdown-time guarantee.

Replay loads at most 50 MiB before starting audio and closes the source file; render callbacks read the owned memory buffer. The bridge caches its capture format and uses a preallocated decode block. The existing live bridge FIFO still uses a short synchronization lock, and NAudio/Windows may allocate internally; these are managed shared-mode audio paths, not a hard real-time guarantee. Offline tests cover queue order/wrap, exact duration, slow-disk overflow, explicit stop, worker failure/closure, replay preload limits and allocation-free owned publish/gain-update paths.

Capture files must be placed on a local drive, without junction/symlink traversal; existing files are preserved. `SOLTEX_AUDIO_RECORDINGS` sets the file dialog's starting directory (default: the user's Music folder). The recording path is not silently saved in presets. The UI displays the retained file's location and explains retention. **Discard test** deletes only the current session's file after checking the completed recording's SHA256 and rejecting path reparse points; changed files are retained. Recordings are not uploaded or automatically deleted on close. Offline tests prove WAV frame limits, duration, channel ordering and finalization; microphone capture, replay audibility and driver failure recovery need a live test on real hardware.

## Build and verification

Build with the .NET 10 SDK pinned by global.json, then run `dotnet run --project tests/Soltex.Audio.Engine.Tests`. Tests measure known sine response, dynamics thresholds and time behavior, isolation, partition invariance, hostile numeric samples, PCM conversion and buffer ordering. They do not stand in for hardware audio acceptance.

For source CLI execution use `dotnet run --project src/Soltex.Audio.Engine.Cli -- --list`; publish the CLI project for the executable, or use `eng/build-audio.ps1` to package it alongside a desktop host.

## Sources and license

- NAudio.Wasapi **2.3.0**, NuGet-pinned (NAudio.Core transitive), MIT: https://www.nuget.org/packages/NAudio.Wasapi/2.3.0 . This deliberately uses the qualified 2.x API, not a claim that it is the latest major version.
- Versioned WASAPI implementation: https://github.com/naudio/NAudio/tree/v2.3.0/NAudio.Wasapi . No NAudio source is vendored; its redistribution notice is retained in THIRD-PARTY-NOTICES.txt.
- Biquad coefficient equations: Robert Bristow-Johnson/W3C Audio EQ Cookbook, https://www.w3.org/TR/audio-eq-cookbook/ . The engine's implementation and DSP tests are independent code.

Soltex's own signed virtual endpoints are not available yet, so a full multi-bus setup still depends on separately installed endpoints. The optional Sonar adapter is separate from this engine.
