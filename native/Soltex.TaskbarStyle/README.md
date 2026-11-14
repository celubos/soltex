# Taskbar helper

`SoltexTaskbar.dll` changes how the Windows 11 taskbar background is drawn: clear, acrylic, blur or solid, with a colour and tint. It can also hide the line above the taskbar. It works like TranslucentTB on Windows 11 but is an independent implementation and contains no TranslucentTB code.

How it works:
1. The app installs a `WH_GETMESSAGE` hook on the taskbar's thread for a moment. Windows loads the DLL into Explorer, and the hook calls `InitializeXamlDiagnosticsEx` from inside Explorer, on that thread.
2. Windows gives the DLL an `IXamlDiagnostics` site. A worker thread subscribes to visual-tree changes.
3. The DLL tracks `Rectangle#BackgroundFill` and `Rectangle#BackgroundStroke` under `Taskbar.TaskbarBackground`. On the XAML thread it sets their `Fill` to a brush built from XAML markup. It keeps a reference to the original brush so Windows default can put it back. Acrylic and Blur fall back to a solid colour when acrylic cannot render (Transparency effects off, Energy Saver): Acrylic to its tint colour, Blur to Windows' own taskbar colour for the current light or dark mode.
4. Style rules (Windhawk Taskbar Styler-class): `Styles` (REG_MULTI_SZ), one `under-type|type|name|Owner.Property|value` per line. Matching elements get the value as a local value. The previous local value is kept and restored exactly: cleared, re-bound, set again, or, for a template binding, the value it showed. When the rules change, everything is restored and the tree is walked again; a signal with the same rules leaves them in place.
5. Settings: `HKCU\Software\Soltex\Taskbar` (`Look`, `Color` 0xAARRGGBB, `Line`, `Styles`, `Request`). The app signals `Local\SoltexTaskbar2-<build>.<explorer pid>` after writing them. `<build>` is the first 16 hex digits of the DLL's SHA-256, so each build has its own event, and older builds still in Explorer are set back to Windows' look before a new one loads. After every pass the DLL writes `Status`: `targets= applied= look= hr= styled= styleApplied= styleHr= request=`.

The app loads the helper into its own process to find the hook, so the helper's architecture must match both the app and Explorer. An x64 build of the app running under emulation on an ARM64 PC therefore turns the look and details off.

## Build

`build.ps1` builds with the LLVM-MinGW release pinned in `toolchain.lock.json` (Windows PowerShell 5.1 or PowerShell 7). It outputs `artifacts/native/taskbar/{x64,arm64}/SoltexTaskbar.dll`, plus `test/TaskbarTestHost.exe`: a hidden XAML island with the same element names, used by `tests/Soltex.App.Tests` (`TaskbarChecks`). Every built file must import only the Windows DLLs on the script's allowlist, and the LLVM and mingw-w64 runtime notices are copied to `licenses/`.

The toolchain folder (the one holding `bin\clang++.exe`) is found in this order:
1. `-Toolchain <folder>`
2. the `SOLTEX_LLVM_MINGW` environment variable
3. `<CacheRoot>\llvm-mingw-<release>-ucrt-x86_64` (the `folder` in the lock file), where `-CacheRoot` defaults to `.tools` in the repository

`-Acquire` downloads the pinned archive into the cache when the toolchain is not there yet, checks its exact size and SHA-256 against the lock file (a mismatch deletes the file and stops the build), and extracts it with `tar`:

```powershell
.\native\Soltex.TaskbarStyle\build.ps1 -Acquire                                        # local: into .tools
.\native\Soltex.TaskbarStyle\build.ps1 -Acquire -CacheRoot "$env:RUNNER_TEMP\toolchains" # CI
.\native\Soltex.TaskbarStyle\build.ps1 -Toolchain C:\tools\llvm-mingw-20261104-ucrt-x86_64
```
