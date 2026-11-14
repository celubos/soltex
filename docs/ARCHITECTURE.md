# Architecture

Soltex is a .NET 10 WPF desktop app for Windows 10 (2004 or later) and Windows 11, x64. It runs as the signed-in
user, lives in the notification area, and makes per-user Windows changes that it records and can undo. A few features
need code outside the main process; each of those is a separate, narrow helper described below.

## Processes

```text
Soltex.exe (WPF, unelevated, one instance per user)
  |
  +-- Soltex.exe --system <request>          elevated helper, started through UAC per change, then exits
  +-- SoltexTaskbar.dll in explorer.exe      native taskbar helper, loaded when a taskbar look or style rule is turned on
  +-- Soltex.Audio.Host.Service.exe          audio helper, started by the Audio page, exits with Soltex.exe
  +-- RustDesk.exe (external)                launched only from Tools > Remote help, after confirmation
```

`Soltex.exe` takes one lock (`Local\` mutex plus a current-user pipe) so a second launch shows the open window instead
of starting another copy. `--tray` starts hidden, `--audio` opens the Audio page, `--quit` asks the running copy to exit
and `--profile-dir <absolute path>` uses a separate data folder (useful for testing). Closing the window hides it; Quit
from the tray or `Ctrl+Q` exits.

## Shell and pages

`src/Soltex.App` holds the shell window (`Shell/ShellWindow.xaml`), the tray icon, search, and the pages. Pages do no
background work until they are shown. Every page registers search entries, so the search box can jump to a single
setting.

| Page | What it does | Main code |
| --- | --- | --- |
| Home | De-Windows profile, protection status, processor/memory/storage/network use, busy apps, Windows version | `Pages/MyPcPage`, `Pages/DeWindowsPanel`, `Pages/SystemPanel`, `Soltex.Monitoring`, `Soltex.Security` |
| Taskbar | Taskbar options, taskbar look (clear, acrylic, blur, solid) and style rules | `Pages/CustomizePage`, `Pages/TaskbarLookPanel`, `Shell/TaskbarLook.cs`, native helper |
| Start | Start layout, recommendations and search options | `Pages/CustomizePage`, `Soltex.Customize` |
| File Explorer | Extensions, context menus, navigation pane | `Pages/CustomizePage`, `Soltex.Customize` |
| Appearance | Dark mode, transparency, accent colour, theme schedule | `Pages/CustomizePage`, `Personalize/` |
| Multitasking | Snap, Alt+Tab and mouse options | `Pages/CustomizePage`, `Soltex.Customize` |
| Desktop | Desktop groups, widgets, desktop icons, video wallpaper | `Pages/DesktopPage`, `Desktop/`, `Personalize/` |
| Privacy | Ads, tips, suggestions and tracking options | `Pages/CustomizePage`, `Soltex.Customize` |
| Audio | Mixer, microphone setup and processing, per-app volume and output, optional Sonar view | `Pages/AudioPage`, `Soltex.Audio.*` |
| Tools | Text extractor, window zones, batch rename, key remapping, startup apps, remote help, Defender scans, protected imports and quarantine | `Pages/ToolsPage`, `Tools/`, `Soltex.RemoteAssist`, `Soltex.Security` |
| History | Every per-user change Soltex made, with undo (administrator changes are undone from Home > Full De-Windows) | `Pages/HistoryPage`, `Soltex.Customize` |
| Settings | App theme, size, density, motion, start-up, visible pages, shortcuts, data folder, recent events | `Pages/SettingsPage`, `Settings/` |

## Changing Windows settings

`src/Soltex.Customize` owns every change Soltex makes to Windows.

- **Per-user settings** (`SettingCatalog`, `TweakCatalog`, `ChangeEngine`). Each setting is a list of
  `HKEY_CURRENT_USER` values that Windows Settings or File Explorer also write. Before a write, the previous value is
  appended to a JSON-lines journal and flushed; after the write, the value is read back. Undo restores a value only if
  it still holds what Soltex wrote, so a later change by the user or another tool is kept. A journal entry without a
  completion record (for example after a crash) is reconciled on the next start. `WindowsSettingsNotifier` broadcasts
  `WM_SETTINGCHANGE` so Explorer picks up the change.
- **Administrator changes** (`SystemCatalog`, `SystemEngine`, `WindowsSystemHost`). Policies, service start types,
  scheduled tasks, firewall block rules and app removals need administrator rights. The app writes a request that
  names catalogue entries by id (never commands or paths) to `%LOCALAPPDATA%\Soltex\System\`, then starts
  `Soltex.exe --system <request>` with the `runas` verb. The helper accepts only a request file with the expected name
  inside that folder, rejects redirected folders, runs only catalogue entries, journals each previous value, writes a
  result file and exits. App removals cannot be undone from Soltex; the item says how to get the app back.
- **Drift.** `Shell/DriftWatcher.cs` notices when Windows turns a setting back on and reports it.

Everything is off until the user turns it on. The De-Windows profile on Home is a named selection of per-user catalogue
rows; **Full De-Windows** adds the administrator catalogue and is applied and undone through the elevated helper.

## Taskbar helper

`native/Soltex.TaskbarStyle` builds `SoltexTaskbar.dll` (x64 and ARM64) with a pinned LLVM-MinGW toolchain. To apply a
taskbar look, the app copies the DLL to `%LOCALAPPDATA%\Soltex\Taskbar\` under a content-hashed name, installs a
`WH_GETMESSAGE` hook on the taskbar thread for a moment so Explorer loads it, and the DLL attaches through the XAML
diagnostics API. It recolours the taskbar background rectangles and applies style rules, keeping each original value so
it can be restored exactly. Settings travel through `HKCU\Software\Soltex\Taskbar` and a named event per helper build.
Choosing the Windows default look with no rules restores everything; the DLL stays loaded until Explorer restarts. See
`native/Soltex.TaskbarStyle/README.md`.

## Audio

The Audio page hosts `Soltex.Audio.UI`. Audio work runs out of the UI thread and, for streams, out of the UI process:

- `Soltex.Audio` reads endpoints, defaults, volume, mute and meters through Core Audio on one MTA worker.
- `Soltex.Audio.Sessions` lists app sessions and sets per-app volume and mute.
- `Soltex.Audio.WindowsRouting` sends one app to a chosen output, with a journal and selective undo.
- `Soltex.Audio.Engine` is the DSP (EQ, gate, compressor, expander, noise suppression, stereo controls) and the WASAPI
  bridge, built on NAudio.
- `Soltex.Audio.Host` plus `Soltex.Audio.Host.Cli` (`Soltex.Audio.Host.Service.exe`) run the five-channel graph and the
  personal/stream mix in an unelevated helper, controlled over an authenticated current-user pipe. The helper starts
  stopped and exits when Soltex exits.
- `Soltex.Audio.Sonar` is an optional adapter for an already installed SteelSeries Sonar. It is shown only when Sonar is
  present.
- `Soltex.Audio.Transport`, `Soltex.Audio.OwnedEndpoints`, `Soltex.Audio.Broker` and `native/Soltex.Audio.Driver` are
  source for Soltex's own virtual endpoints. The driver is unsigned and is not installed or started by any build.

Details: [audio/ARCHITECTURE.md](audio/ARCHITECTURE.md).

## Libraries

| Project | Role |
| --- | --- |
| `Soltex.Security` | Windows Security Center health, Defender status/scans/updates through fixed PowerShell commands, AMSI intake, Authenticode, quarantine, allow list, authenticated local state, audit log |
| `Soltex.Monitoring` | Bounded CPU, memory, process, volume and network sampling for Home and desktop widgets |
| `Soltex.Customize` | Setting catalogues, change engines and journals described above |
| `Soltex.RemoteAssist` | Locates and launches a separately installed RustDesk client |
| `Soltex.Update` | Signed update descriptors, pinned HTTPS acquisition and a non-installing update planner. Not wired to the app; Soltex does not update itself |
| `Soltex.DeviceFabric` | Local device observation and a non-executing capability policy model. Library and tests only |
| `src/Shared` | Small shared source files (atomic file replace) |

## Data

All state is per-user under `%LOCALAPPDATA%\Soltex` (or `--profile-dir`):

| Path | Contents |
| --- | --- |
| `Settings\app-settings-v1.json` | App preferences (never secrets or Windows state) |
| `Customize\journal-v1.jsonl` | Per-user change journal (History) |
| `System\` | Elevated helper request, result and journal |
| `Taskbar\` | Staged taskbar helper and its log |
| `Audio\` | Audio profiles, presets and routing journals |
| `Security\`, `Imports\` | Security state, quarantine, audit log, protected imports folder |
| `logs\crash\` | Local crash reports |

Nothing is uploaded. The app has no telemetry and no account. Start at sign-in is an opt-in value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Compatibility rules for the data folder are in
[NAMING_AND_COMPATIBILITY.md](NAMING_AND_COMPATIBILITY.md).

## Performance rules

- Pages do recurring work (meters, sampling) only while shown; the shell stops it when the page changes or the window
  is hidden or minimized.
- One protection query per minute plus refreshes on Windows Security Center change signals, with bounded backoff.
- One `FileSystemWatcher`, limited to the `Imports` folder, with a bounded queue and a single worker.
- The key-remapping hook runs only while a remap exists; window zones and the theme schedule attach only when enabled.
- Audio callbacks do no file I/O and no security work.

## Related documents

- [BUILDING.md](BUILDING.md): build, test and package.
- [SECURITY_ARCHITECTURE.md](SECURITY_ARCHITECTURE.md), [THREAT_MODEL.md](THREAT_MODEL.md),
  [SUPPLY_CHAIN_SECURITY.md](SUPPLY_CHAIN_SECURITY.md).
- [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md): themes and tokens.
