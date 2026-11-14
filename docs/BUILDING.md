# Building Soltex

## Prerequisites

- Windows 10 (2004 or later) or Windows 11, x64.
- The .NET 10 SDK, 10.0.3xx (see `global.json`).
- PowerShell 7 (`pwsh`) for the scripts under `eng/`. `eng/make-release.ps1` also runs in Windows PowerShell 5.1.
- Optional: LLVM-MinGW for the native taskbar helper. You do not need to install it yourself; the scripts can fetch
  the exact release pinned in `native/Soltex.TaskbarStyle/toolchain.lock.json` (see below).
- Optional: Inno Setup 6, only for the installer built by `eng/publish-release.ps1`.

The app builds and runs without the taskbar helper; changing the taskbar look then reports that the helper is missing,
and the `TaskbarChecks` in `tests/Soltex.App.Tests` fail until it is built.

## Build

```powershell
dotnet build Soltex.sln -c Release
```

Warnings are errors (`Directory.Build.props`), so a clean build has zero warnings.

## Taskbar helper

```powershell
# First time: download the pinned LLVM-MinGW into .tools\ (size and SHA-256 are checked against the lock file).
.\native\Soltex.TaskbarStyle\build.ps1 -Acquire

# Later builds reuse the cached toolchain.
.\native\Soltex.TaskbarStyle\build.ps1
```

The script looks for LLVM-MinGW in this order: `-Toolchain <folder>`, the `SOLTEX_LLVM_MINGW` environment variable,
then `.tools\<folder named in the lock file>` (change the cache location with `-CacheRoot`). It writes
`artifacts\native\taskbar\{x64,arm64}\SoltexTaskbar.dll` and a small XAML test host. The app project copies the DLLs
into its output when they exist, so build the helper before building the app if you want it included.

`eng/verify.ps1`, `eng/publish-release.ps1` and `eng/build-audio.ps1 -Integrated` build the helper themselves and pass
`-Acquire` through.

## Tests

Each test project is a console runner that exits non-zero on failure:

```powershell
dotnet run --project tests/Soltex.Customize.Tests -c Release
dotnet run --project tests/Soltex.App.Tests -c Release
```

The suites CI runs on every push are `Soltex.Security.Tests`, `Soltex.Security.SupplyChain.Tests`,
`Soltex.Security.Hardening.Tests`, `Soltex.Update.Tests`, `Soltex.DeviceFabric.Tests`, `Soltex.Customize.Tests`,
`Soltex.Monitoring.Tests`, `Soltex.Audio.Tests` and `Soltex.App.Tests`. The audio suites (`tests/Soltex.Audio.*`) run
by default without opening audio devices; modes that read or change live audio state are opt-in flags described in each
suite's README.

Two repository checks also run in CI:

```powershell
pwsh eng/verify-identity.ps1        # legacy product-name policy
pwsh eng/verify-design-tokens.ps1   # colour literals only in Themes/Tokens.xaml
```

`eng/verify.ps1 [-Acquire]` runs the identity check, builds the taskbar helper, does a Release build and runs the
Security, Monitoring and App suites in one go.

## Package

`eng/build-audio.ps1 -Integrated` builds the taskbar helper, runs the full test matrix, and publishes a self-contained
x64 package (`Soltex.exe`, the audio helpers and tools, the taskbar helper, guides, `BUILD.json`, `VERIFICATION.json`
and `SHA256.json`):

```powershell
pwsh eng/build-audio.ps1 -Integrated -ArtifactRoot <dir> [-Acquire]
```

`<dir>` must be an absolute path on a local fixed drive, outside the checkout. The package is written to
`<dir>\package-<run id>`. Other switches: `-Dotnet <path>` to choose the SDK, `-SkipTests`, and `-FrameworkDependent`
for a smaller package that needs the .NET 10 Desktop Runtime. Without `-Integrated` the script builds the standalone
audio app (`Soltex.Audio.Desktop.exe`) instead.

## Release zip

```powershell
pwsh eng/make-release.ps1 -PackageDirectory <dir>\package-<run id> -OutputDirectory <release dir>
```

This writes `Soltex-<version>-win-x64.zip` and `SHA256SUMS.txt`. The zip holds one `Soltex-<version>-win-x64` folder
with the app, the taskbar and audio helpers, licences, and `README.txt` (from `eng/package/README.txt`); developer
tools and the Markdown guides are left out. Every file is checked against the package's `SHA256.json` first, and
existing outputs are never overwritten. Release builds are not code-signed.

## Installer (optional)

`eng/publish-release.ps1 [-Acquire]` publishes the self-contained app into `artifacts\publish\win-x64` (same layout as
the release zip) and compiles a per-user installer from `eng/soltex.iss` into `artifacts\installer`. Pass
`-SkipInstaller` if Inno Setup is not installed.

## Native audio driver (optional)

`native/Soltex.Audio.Driver` holds the source of Soltex's own virtual audio endpoints. It needs the WDK packages and
the MSVC toolchain listed in its `toolchain.lock.json`, builds an unsigned driver, and is not part of the normal build
or package. See its README.
