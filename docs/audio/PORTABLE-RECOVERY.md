# Selective audio recovery

This package includes no machine-specific baseline or journal. Recovery must use snapshots and journals captured on your own computer. Do not copy another machine's endpoint IDs, import an entire app-routing registry export, or replace a live GG database.

Closing the Soltex window hides it in the tray. Use **Quit** in the tray menu (or Ctrl+Q) to exit; quitting stops the audio Soltex started. Closing the Processing window stops its streams; stop a separately launched engine command line with Ctrl+C. Quitting Soltex does not stop an installed Sonar.

## Capture your own baseline

Run PowerShell from the extracted package directory. Choose a new recovery folder on your own storage; the commands below do not overwrite existing snapshots.

```powershell
$tool = Join-Path (Get-Location) 'Soltex.Audio.Tool\Soltex.Audio.Tool.exe'
$recoveryRoot = Read-Host 'Enter an absolute path for a new recovery folder on your computer'
New-Item -ItemType Directory -Path $recoveryRoot -ErrorAction Stop | Out-Null
& $tool snapshot (Join-Path $recoveryRoot 'before.json')
```

Capture `before.json` **before** the deliberate change you may need to undo. After that change, capture its resulting state:

```powershell
& $tool snapshot (Join-Path $recoveryRoot 'after-owned-change.json')
```

Keep both files private. They contain device identities, default selections and volume/mute observations. Confirm that snapshots were obtained successfully before treating them as a baseline. A snapshot alone does not prove that every later difference belongs to your operation.

## Restore default devices selectively

If you enabled Sonar only for a while and now want the earlier Windows routes back, first turn off Sonar's routing in SteelSeries GG. Do not disable Sonar if you have since deliberately adopted it. An active external route manager can immediately replace restored Windows defaults.

Preview eligible changes from your own before/after pair:

```powershell
& $tool restore-defaults (Join-Path $recoveryRoot 'before.json') (Join-Path $recoveryRoot 'after-owned-change.json') --dry-run
```

Read the result for each render/capture Console, Multimedia and Communications role. Apply only when the recorded after-state represents your intended recovery session and the proposed changes are still yours:

```powershell
& $tool restore-defaults (Join-Path $recoveryRoot 'before.json') (Join-Path $recoveryRoot 'after-owned-change.json') --apply
& $tool snapshot (Join-Path $recoveryRoot 'after-recovery.json')
```

The tool compares current IDs with the recorded after IDs, checks that the original target remains active in the correct direction, and rechecks before each setter. It preserves later conflicting selections and reports unavailable devices instead of choosing a substitute. It does not restore app assignments, endpoint enabled states, Sonar channel settings, drivers or databases. Dry-run does not activate/test the undocumented Windows default setter; apply can report an unsupported/error result.

## Restore a tool-owned volume or mute change

`set-volume` and `set-mute` produce a separate change journal containing the exact property, original value and confirmed result. Use that actual journal, not an invented file or a plain snapshot:

```powershell
$journal = Read-Host 'Enter the absolute path to your existing volume or mute change journal'
& $tool restore $journal --dry-run
# After reviewing the eligible change:
& $tool restore $journal --apply
```

Recovery changes only the journaled property if the current value still matches the confirmed value owned by that change. Interrupted/unconfirmed journals require manual reconciliation. A volume journal does not restore mute or routing. For controls changed through Sonar or another application, compare your saved settings and restore only the specific values you intended to undo in that application's UI.

## When no suitable baseline exists

Use Windows Sound settings to select your currently intended physical output and microphone from the devices actually connected to your computer. Check communications defaults in More sound settings if necessary. Do not guess historical values or treat an old snapshot as current intent.

For one application's saved route, use that application's output selector or Windows app volume/device settings to choose its intended endpoint or System default. Restart playback or the application if its active stream does not move. Do not reset all applications to fix one assignment. In Soltex, **Audio → Apps** keeps a history of its own route changes and can undo them one at a time.

No driver uninstall or trust bypass is needed to remove Soltex. If you changed the taskbar look, set it to **Windows default** and turn off the taskbar details first; otherwise the taskbar keeps its look until you sign out or restart. Turn off the optional start-at-sign-in setting, quit Soltex, then delete the extracted folder (if you used setup.exe, uninstall from **Settings › Apps › Installed apps** instead). Preferences are in `%LOCALAPPDATA%\Soltex` and `HKCU\Software\Soltex` (standalone audio app: `%LOCALAPPDATA%\SoltexAudio`); keep them if you want your settings. To delete `%LOCALAPPDATA%\Soltex`, sign out or restart first, because the taskbar helper stays loaded in File Explorer until then. The history of Full De-Windows (administrator) changes stays in `%ProgramData%\Soltex\System`; deleting it needs administrator rights and means Soltex can no longer undo those changes. An installed Sonar is separate.
