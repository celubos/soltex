# Desktop UI regression checks

Run on Windows with the .NET 10 SDK and an interactive desktop session. This console runner exits nonzero on failure and writes a JSON result to a unique owned directory. GG is not required. There are no network calls, device routing changes, capture, playback, recordings or default endpoint changes. No fixture activates its window or sends keyboard/mouse input.

```powershell
dotnet run --project tests/Soltex.Audio.App.Tests --artifacts-path <build-dir> -- --work-dir <work-dir>
```

For CI, omit `--work-dir` to use the platform temporary directory. Direct build output and NuGet/TEMP caches to the CI workspace. Local runs can use any absolute directory. The work root must be absolute; the runner creates a new uniquely named child and never reads personal preferences. Small fixture files and the result remain there for failure diagnosis.

Images are optional. Add `--render-dir <dir>` to write PNGs into another unique child directory. These are rendered client areas at **simulated logical window sizes**, not screenshots proving OS display scaling. No captures are generated without that explicit argument.

The project references the desktop app and reusable `Soltex.Audio.UI` library. Engine controls instantiate the actual `EngineControlWindow`, using its preference-path override. Its normal read-only endpoint enumeration runs; no endpoints are selected, and all audio objects remain null. Tests exercise focused numeric edits, invalid-value rejection without overwriting valid preferences, gain/mute/EQ/dynamics persistence, reset/reopen, future-version/malformed file preservation, explicit recovery, and MTA worker shutdown.

The mixer fixture hosts the actual `MixerWorkspace` UserControl without calling `InitializeAsync`; its constructor does not open native services or contact GG. Synthetic model values exercise the production bindings. Layout assertions cover usable faders, automation selector geometry, header overlap, visible scrollbar thumbs, footer reachability and revealing the final channel across 1240×810, 1000×680, 960×540, 800×600 and 640×420 DIP. No production source or action handlers are copied or compile-linked into this project.

The embedded-host check verifies that themes do not alter unrelated host controls, activation does not initialize backend services, deactivation stops presentation polling, Processing has the real host Window as owner, repeated creation reuses its single worker, and asynchronous workspace shutdown waits for that child to close. An internal production creation seam lets the fixture position the child offscreen before showing it; the public Processing action uses that same method. All preferences use the test's unique owned directory. The test-only friend assembly grants access to model types and this creation seam.

The narrow reflection seams observe private worker/audio lifetime fields and invoke the production preset decoder; band edits set the actual editor row's public property. Renames fail explicitly. Logical focus is tested without native window activation. File dialogs, real keyboard navigation, real DPI changes, GG behavior and audio hardware are separate integration acceptance checks.
