# Taskbar helper

`SoltexTaskbar.dll` changes how the Windows 11 taskbar background is drawn: clear, acrylic, blur or solid, with a colour and tint. It can also hide the line above the taskbar. It works like TranslucentTB on Windows 11 but is an independent implementation and contains no TranslucentTB code.

How it works:
1. The app installs a `WH_GETMESSAGE` hook on the taskbar's thread for a moment. Windows loads the DLL into Explorer, and the hook calls `InitializeXamlDiagnosticsEx` from inside Explorer, on that thread.
2. Windows gives the DLL an `IXamlDiagnostics` site. A worker thread subscribes to visual-tree changes.
3. The DLL tracks `Rectangle#BackgroundFill` and `Rectangle#BackgroundStroke` under `Taskbar.TaskbarBackground`. On the XAML thread it sets their `Fill` to a brush built from XAML markup. It keeps a reference to the original brush so Windows default can put it back. Acrylic and Blur fall back to a solid colour when acrylic cannot render (Transparency effects off, Energy Saver): Acrylic to its tint colour, Blur to Windows' own taskbar colour for the current light or dark mode.
4. Style rules (Windhawk Taskbar Styler-class): `Styles` (REG_MULTI_SZ), one `under-type|type|name|Owner.Property|value` per line. Matching elements get the value as a local value. The previous local value is kept and restored exactly: cleared, re-bound, set again, or, for a template binding, the value it showed. When the rules change, everything is restored and the tree is walked again; a signal with the same rules leaves them in place.
5. Settings: `HKCU\Software\Soltex\Taskbar` (`Look`, `Color` 0xAARRGGBB, `Line`, `Styles`, `Taskbars`, `Request`, `Owner`, `OwnerStart`, `RestLook`, `RestColor`, `RestLine`). The app signals `Local\SoltexTaskbar2-<build>.<explorer pid>` after writing them. `<build>` is the first 16 hex digits of the DLL's SHA-256, so each build has its own event, and older builds still in Explorer are set back to Windows' look before a new one loads. After every pass the DLL writes `Status`: `targets= applied= look= hr= styled= styleApplied= styleHr= request= mapped= resting=`.
6. A look per taskbar: `Taskbars` (REG_MULTI_SZ, optional), one `hwnd=<decimal HWND>;look=<0-4>;color=<decimal ARGB>;line=<0|1>` per taskbar window. Unknown fields are ignored and malformed entries skipped. Each taskbar is its own XAML island whose tree root is a `DesktopWindowXamlSource`: the DLL traces a background rectangle up to that root, asks it for its window (`IDesktopWindowXamlSourceNative::get_WindowHandle`) and takes that window's top-level ancestor, the `Shell_TrayWnd`/`Shell_SecondaryTrayWnd` it is drawn in. So two displays with the same taskbar size are told apart. Only without an island root does it compare sizes: the rectangle's `Taskbar.TaskbarFrame` (or, without one, its topmost known `FrameworkElement`) against the one taskbar window whose size in DIPs (window size × 96 / its DPI) is within 2 DIPs; no match or several matches leave the global look. That taskbar shows the entry's look; a taskbar with no entry or no match shows `Look`/`Color`/`Line`. `mapped=` counts the rectangles (fill and line) that showed an entry's look, so the app can tell when a taskbar was not matched (it then writes the strongest state's look as the global one).
7. Resting without the app: `Owner` is the app's process id and `OwnerStart` its creation time (FILETIME, REG_QWORD). The DLL waits on that process (`SYNCHRONIZE`); when it ends, by quitting or crashing, or when the number now belongs to a process started at another time, every pass shows `RestLook`/`RestColor`/`RestLine` (the Desktop look) with no `Taskbars` entries, and `Status` says `resting=1`, until the settings name another owner. A look meant only while the app watches (Start open, a maximized window) never sticks. Style rules stay as they are.
8. Tree changes: system XAML reports a removal for every element of a branch that leaves the tree, and reports a branch put back child first. A background rectangle added before its parent waits until its branch is complete; one that comes back gets back the original brush saved when it left (XAML keeps the Fill it showed, possibly the helper's own). Links whose parent is gone are swept out before the parent table counts as full.

The app loads the helper into its own process to find the hook, so the helper's architecture must match both the app and Explorer. An x64 build of the app running under emulation on an ARM64 PC therefore turns the look and details off.

## Build

`build.ps1` builds with the LLVM-MinGW release pinned in `toolchain.lock.json` (Windows PowerShell 5.1 or PowerShell 7). It outputs `artifacts/native/taskbar/{x64,arm64}/SoltexTaskbar.dll`, plus `test/TaskbarTestHost.exe`: two hidden XAML islands of different sizes (stand-ins for two taskbars) with the same element names, used by `tests/Soltex.App.Tests` (`TaskbarChecks`). Every built file must import only the Windows DLLs on the script's allowlist, and the LLVM and mingw-w64 runtime notices are copied to `licenses/`.

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
