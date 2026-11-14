# Soltex Audio endpoint tool

Independent diagnostic/recovery CLI. It does not start the Soltex suite, install a driver, or choose an audio graph backend. Build with the .NET 10 SDK pinned by global.json.

```
Soltex.Audio.Tool list
Soltex.Audio.Tool snapshot <new-baseline.json>
Soltex.Audio.Tool peak "<opaque Windows endpoint ID>"
Soltex.Audio.Tool watch <notification-watch.json> 90
Soltex.Audio.Tool set-volume "<ID>" 0.5 <new-volume-journal.json>
Soltex.Audio.Tool set-mute "<ID>" true <new-mute-journal.json>
Soltex.Audio.Tool restore <journal.json> --dry-run
Soltex.Audio.Tool restore <journal.json> --apply
Soltex.Audio.Tool restore-defaults <before.json> <after.json> --dry-run
Soltex.Audio.Tool restore-defaults <before.json> <after.json> --apply
```

Run the built `.exe`, or the built `.dll` through the SDK's `dotnet.exe`. Snapshot and journal paths must be new files in an existing directory. Both restore commands default to dry-run when their final option is omitted. The `set-*` commands deliberately apply a requested change; take an inventory snapshot first. Exit codes: 0 successful observation/confirmed command/safe recovery decision, 1 failed command or conflicting/unavailable recovery, 2 invalid input/file failure.

`watch <new.json> <seconds>` is read-only, accepts 1–180 seconds, and prints `Watching` after its initial snapshot is durably saved. Make the device or default change you want to observe only after that ready message. The callback only enqueues observations; it performs no file I/O and does not block. At completion the tool atomically replaces the initial receipt with initial/final inventories, the first 128 coalesced `SnapshotChanged` observations, total observed count and an explicit dropped count. These are inventory notifications rather than raw Windows callback counts. Zero events is valid observation data, not proof of notification delivery. An interrupted run retains its `Watching` receipt and must not be reported as completed. Exit 1 indicates unavailable inventory or notification registration; no audio settings are changed by this command.

## Endpoint controls and recovery

The change journal is flushed to disk before a command. It contains one endpoint identity, one property, the previous value, requested value and confirmed readback. Only an `Applied` or already `Restored` journal from this machine is eligible for automatic recovery. Interrupted `Prepared` and `Unconfirmed` journals require manual reconciliation; they cannot safely establish ownership of a change. Existing journals are never overwritten by a new command.

Recovery compares the current property with the recorded confirmed value. It restores only that property, and skips later changes, already restored values and changes with no delta. Volume comparison uses a 0.000001 scalar tolerance for Windows float readback. A mute command cannot restore volume or vice versa. A later user change to exactly the same value as our recorded value is inherently indistinguishable; Windows supplies no transaction ownership or atomic compare-and-set API. Keep recovery sessions short and inspect the preview.

`restore-defaults` imports the six `defaults` direction/role entries from the before and after snapshots, including an older lowercase JSON shape. Supplying the after file asserts those recorded differences belong to the intended recovery session. It compares every current role to the recorded after ID, confirms the original target is active in the correct direction, then rechecks immediately before a setter. A missing original device is reported without choosing a substitute. It restores neither endpoint enable states nor application assignments. The optional default setter uses the undocumented Windows PolicyConfig ABI in one isolated adapter and reports unsupported/error results. Dry-run checks eligibility and active targets; it does not activate/test the setter. Snapshot again after any applied recovery and retain its JSON output.

## Library contract

- `AudioEndpointProvider` stays read-only and keeps its existing bounds; its historical `IsDefault` and `DefaultRoles` describe Multimedia only. Existing positional `AudioEndpoint` construction remains source compatible. New ID/full-name fields are init-only.
- `AudioDeviceService` owns COM references on one MTA worker. Discovery reads all six defaults and all endpoint states, bounded at 512 per direction with an explicit partial result if exceeded. A bounded command queue rejects excess work; disposal drains accepted work and releases COM on its owning thread.
- `SnapshotChanged` reconciles MMDevice add/remove/state/default notifications and consumed friendly-name/device-format property changes, coalescing queued callbacks. Unrelated runtime/vendor property changes do not trigger full enumeration: interface activation can itself generate those notifications. Subscribers run on the worker and must dispatch UI work without blocking. This event is endpoint inventory notification, not a per-sample meter stream or a guarantee of external volume/mute notifications. Read effective endpoint state when needed.
- `SetVolumeAsync`/`SetMuteAsync` accept stable IDs, report Windows errors, and return confirmed effective state. They never change the default route. Volume acceptance uses a 0.002 scalar tolerance to accommodate hardware quantization; the exact confirmed value is retained in the journal.
- `ReadPeakAsync` reads actual `IAudioMeterInformation` endpoint peak. Null plus an error means unavailable. Zero is an observed silent meter, not proof of a working audio path. It does not represent DSP output, a logical bus or a fabricated animation.
- Meter and device COM references stay on the MTA worker in a 16-entry LRU cache. Device state/removal and format changes invalidate the affected identity before its next queued read; native read errors release the lease. Failed activation/read results retry after one second or immediately after a relevant device notification. Successful meters are read at the requested rate; readings themselves are never cached. Disposal releases all owned interfaces.
- `ReadPeaksAsync` submits up to 16 identities as one frame, reading each distinct endpoint once with one worker dispatch. The mixer uses this batch at its unchanged 100 ms cadence and discards a frame if the selected snapshot changes while it is being read.
- `OutputDevicePolicy` selects among configured active render IDs. It preserves missing priorities and manual pins, applies exclusions before selection, supports returning to a higher priority device or retaining the current eligible device, and never admits unknown devices. Selection itself does not change Windows or an audio graph. The caller owns preference persistence and backend switching.

## Validation

`tests/Soltex.Audio.Control.Tests` covers disconnect/reconnect, manual pin retention, priority return, exclusions, unknown/capture/disabled devices, strict selective recovery, legacy construction and worker lifetime. Its live tests only read devices, register notifications, read a real meter and submit inputs rejected before any write. Legacy `Soltex.Audio.Tests` remains unchanged. Hardware control success, delivery of a real device-change notification and nonzero audio transport need a separate live check.
