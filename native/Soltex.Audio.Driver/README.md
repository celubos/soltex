# Soltex audio driver (prototype)

This directory contains actual ACX 1.1/KMDF 1.31 kernel source, an INF, a bounded PCM transport ABI, portable native tests, and a reproducible local build. It builds an **unsigned x64 SYS**. It has not been installed or exercised by Windows Audio, Driver Verifier, HLK, or HVCI. Successful compilation and INF validation are not runtime certification.

The six endpoints are four render buses (**Game, Chat, Media, Aux**) and two capture returns (**Microphone, Stream**). Each is currently fixed at 48 kHz, stereo, signed PCM16. The managed boundary converts to/from float DSP samples. Higher channel counts, alternate rates/native float formats, additional exclusive-mode streams and hardware-clock drift correction are not claimed by this prototype.

There is no dependency on GG, Sonar, VB-CABLE or a third-party audio driver in this source. The installed, signed Soltex driver and privileged Soltex broker are prerequisites for its runtime path. Existing cable routes remain a separate compatibility option in the application.

## Data and lifetime

`driver.cpp` creates an ACX circuit for each bus. Windows render packets are copied into that bus's nonpaged ring at the timer's packet boundary. Capture packets consume broker-injected PCM; shortages are zero-filled. No sample tone generator, WAV saving engine or file I/O is present. WaveRT buffers are page-rounded, zero-initialized and described by MDLs. One Windows stream per bus is accepted; ordinary Windows shared-mode audio mixes applications upstream.

Each ring holds 9,600 frames (200 ms). Writes drop the oldest complete frames on overflow and count the loss. Read deficits and driver render underruns have separate observed counters. IOCTLs copy at most 4,800 frames and never wait for audio. Stream/lease reset increments a generation and clears queued audio. Capture writes require both the expected producer sequence and generation under the same spin lock **before** publishing PCM. Process handle cleanup flushes all rings.

The fixed `\\.\SoltexAudioTransport` control device is exclusive and restricted to SYSTEM/Administrators. It accepts only three METHOD_BUFFERED commands. It does not grant ordinary users direct access to audio from other users. `Soltex.Audio.Broker` supplies the authenticated current-session service boundary; service installation is separate and is not performed by this build.

`Soltex.Audio.Transport` implements the ABI with a two-second overlapped I/O bound and exact cancellation. Pending buffers remain alive until kernel completion. In a pathological wait-registration allocation failure, cancelled native storage is quarantined rather than freed while the kernel might own it. That exceptional path can retain memory until process exit. Native cancellation against a loaded driver still requires a lab test.

`Soltex.Audio.OwnedEndpoints` connects transport to the real host: one read per render bus fans identical PCM into transition paths; microphone and Stream each have one selected producer. Capture injection refills against the driver's reported queued frames, not an accumulating user-mode timer estimate. Its WASAPI adapters run the existing DSP, channel conversion, resampling and final output ceiling. Its IWaveIn/IWavePlayer adapters support the personal/Stream mixer. A synthetic endpoint is active only when its matching real Windows endpoint is active.

The INF sets a custom endpoint property `{f7c49f60-0d6c-4f42-9a51-9277315aa100},1` to the exact bus name. The backend uses that identity, rather than a friendly-name heuristic, to pair real and synthetic endpoints and reject feedback routes.

## ABI v1

All integers are little-endian; no pointer is transported. Bus IDs are Game=0, Chat=1, Media=2, Aux=3, Microphone=4, Stream=5.

| Structure | Fields |
| --- | --- |
| Request, 32 bytes | u32 Version=1, Size=32, Bus, Frames; u64 Sequence, ExpectedGeneration |
| Response, 56 bytes | u32 Version=1, Size=56, Bus, Frames; u64 ProducerSequence, Generation, DroppedFrames, UnderrunFrames; u32 Active, BufferedFrames |
| QUERY `0x22e000` | All buses; zero frames/sequence/generation; header response |
| READ `0x22e004` | Buses 0–3; up to 4,800 requested frames; response header followed by returned PCM16 stereo frames |
| WRITE `0x22e008` | Buses 4–5; header followed by PCM16 stereo frames; matching sequence and generation required; header response |

`Active` means the Windows endpoint stream is running, not merely that the control interface exists. `BufferedFrames` is bounded by 9,600. A QUERY or READ has no payload in its request. Unknown command/version/bus, oversized or misaligned transfers, wrong direction and stale writes are rejected.

## Build and verification

`acquire-kit.ps1` downloads the official NuGet packages pinned in `toolchain.lock.json`, checks their size and SHA-256, and expands them into `artifacts/native/driver-kit` (or `-Destination`). `build.ps1` reads the kit from the same place (or `-KitRoot`), finds the MSVC toolset with vswhere (or `-MsvcRoot`; the expected version is in `toolchain.lock.json`), and writes to `artifacts/native/driver` (or `-Output`). Neither script repairs Visual Studio, installs a WDK globally, adds certificates, signs drivers, creates device nodes, changes audio policy or restarts Windows.

```powershell
./native/Soltex.Audio.Driver/acquire-kit.ps1
./native/Soltex.Audio.Driver/build.ps1
```

`build-receipt.json` in the output folder records tool versions, SYS/INF hashes, six-endpoint count and the explicit unsigned/not-installed/not-runtime-tested status. The build runs `/W4 /WX`, the WDK's `InfVerif /w`, and the same portable ring/packet-validation code used by the driver against a reference FIFO across 12,000 randomized operations, plus generation rejection, EOS/preroll, rollover, overflow/underrun and bus-isolation cases.

Managed offline tests are `tests/Soltex.Audio.OwnedTransport.Tests` and `tests/Soltex.Audio.OwnedEndpoints.Tests`. They use fake device boundaries and do not open a driver or real audio endpoints. The existing `tests/Soltex.Audio.Transport.Tests` is a different, earlier live-audio harness.

Before release: select the legal publisher, complete the supported Microsoft signing/catalog process, verify catalog membership, install only on a dedicated test machine with a recovery plan, validate endpoint enumeration and custom properties, run actual multibus PCM/clock/transition/microphone/Stream tests, suspend/resume/unplug/process-death tests, Driver Verifier, applicable HLK and HVCI tests, then package the signed driver and service lifecycle. Do not disable driver signature enforcement or enable test signing to try it.

## Provenance

The implementation is original Soltex source informed by the official ACX lifecycle/API documentation and the Microsoft AudioCodec sample at commit `d49bb7d72f3651952b2b6e4ad28d14fe2247eb69`. The sample's MS-PL license is retained as `MICROSOFT-SAMPLES-LICENSE.txt`; no sample tone or file transport was reused. The WDK/SDK packages are build inputs, not redistributed source; their own package license terms apply (the acquired WDK package labels its terms pre-release). `toolchain.lock.json` pins their source URLs, sizes and SHA-256 hashes.

References: [ACX sample](https://github.com/microsoft/Windows-driver-samples/tree/d49bb7d72f3651952b2b6e4ad28d14fe2247eb69/audio/Acx/Samples), [official NuGet WDK workflow](https://learn.microsoft.com/en-us/windows-hardware/drivers/install-the-wdk-using-nuget), [render packet contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/acxstreams/nc-acxstreams-evt_acx_stream_set_render_packet), [presentation position contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/acxstreams/nc-acxstreams-evt_acx_stream_get_presentation_position), [IOCTL security](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/security-issues-for-i-o-control-codes).
