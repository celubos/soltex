# Building Soltex

## Prerequisites

- Windows 10 (2004 or later) or Windows 11, x64.
- The .NET 10 SDK, 10.0.3xx (see `global.json`).
- PowerShell 7 (`pwsh`) for the scripts under `eng/`. `eng/make-release.ps1` also runs in Windows PowerShell 5.1.
- Optional: LLVM-MinGW for the native taskbar helper. You do not need to install it yourself; the scripts can fetch
  the exact release pinned in `native/Soltex.TaskbarStyle/toolchain.lock.json` (see below).
- Optional: Inno Setup 6, only for the installer built by `eng/publish-release.ps1`. CI and releases use 6.7.1
  (pinned in `eng/installer/get-inno-setup.ps1`).

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

## RNNoise library

```powershell
.\native\Soltex.Rnnoise\build.ps1            # add -Acquire the first time, as for the taskbar helper
```

Builds `artifacts\native\rnnoise\x64\soltex-rnnoise.dll` (Xiph RNNoise 0.2, used by the audio engine's neural noise
removal) from the unmodified sources in `native\third_party\rnnoise`, after checking every file against
`native\Soltex.Rnnoise\source.lock.json`. It uses the same pinned LLVM-MinGW and finds it the same way. The engine
project copies the DLL next to `Soltex.Audio.Engine.Core.dll` in every output that contains the engine when it exists;
without it neural noise removal reports itself unavailable and `Soltex.Audio.Engine.Tests` fails. `eng/verify.ps1`,
`eng/verify-integrated.ps1`, `eng/publish-release.ps1` and `eng/build-audio.ps1` (both modes) build it themselves. See
`native/Soltex.Rnnoise/README.md`.

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

`eng/publish-release.ps1 [-Acquire] [-Iscc <path to ISCC.exe>]` publishes the self-contained app into
`artifacts\publish\win-x64` (same layout as the release zip) and compiles a per-user installer from `eng/soltex.iss`
into `artifacts\installer`. Without `-Iscc` it looks in the usual Inno Setup 6 folders and on `PATH`. Pass
`-SkipInstaller` if Inno Setup is not installed.

To compile `eng/soltex.iss` by hand, `AppVersion` is required (the script stops with an error without it):

```powershell
ISCC.exe /DPublishDir=<app folder> /DAppVersion=1.2.3 /DOutputDir=<dir> eng\soltex.iss
```

Add `/DNumericVersion=1.2.3` when `AppVersion` has a suffix such as `-beta`.

The installer quits a running Soltex before it replaces or removes files, through `Soltex.exe --quit`. That switch
shows nothing, waits up to 20 seconds for the running copy to exit and returns 0 (exited), 1 (not running), 2 (quitting
was cancelled in Soltex, for example at the question it asks while audio plays) or 3 (did not exit in time). On 2 or 3,
setup stops on its Preparing to Install page (exit code 7 when silent) and the uninstaller stops before removing
anything (a non-zero exit code when silent). The sign-in task is offered on a first install only; upgrades leave the
Run value to the app.

`eng/installer/test-installer.ps1 -Setup <setup.exe>` installs, upgrades and uninstalls a setup for the current Windows
account and checks each step, including upgrading and uninstalling while Soltex runs. It is meant for CI runners and
refuses to run elsewhere unless you pass `-AllowOnThisPc`.

## CI

| Workflow | Runs on | What it does |
| --- | --- | --- |
| `windows.yml` | every push and pull request | Identity and design-token checks, taskbar helper, Release build, the test suites, renders of every page |
| `audio.yml` | pull requests that touch the app, tests or packaging | `eng/build-audio.ps1 -Integrated` (self-contained, full test matrix), then `eng/make-release.ps1`; uploads the zip and `SHA256SUMS.txt` |
| `package-smoke.yml` | changes to the app, packaging or installer | `eng/publish-release.ps1` with the pinned Inno Setup, a render from the published app, then `eng/installer/test-installer.ps1` |
| `release.yml` | a published release, or by hand for a tag | Builds and checks `setup.exe` from the release zip and adds it to the release (see below) |

## Releasing

1. Set `<Version>` in `Directory.Build.props`, commit and push. Wait until CI passes for that commit: `windows.yml`
   and `package-smoke.yml` run on push; `audio.yml` runs on the pull request, or start it by hand from the Actions tab.
2. Tag the commit `v<version>` (for example `v1.2.3`) and push the tag.
3. In a clean checkout of exactly that commit (`git status` shows nothing), build the integrated package with the full
   test matrix. Do not pass `-SkipTests` or `-FrameworkDependent`:

   ```powershell
   pwsh eng/build-audio.ps1 -Integrated -ArtifactRoot <dir> -Acquire
   ```

4. Make the release zip and checksums from that package:

   ```powershell
   pwsh eng/make-release.ps1 -PackageDirectory <dir>\package-<run id> -OutputDirectory <release dir>
   ```

   If it warns that the package was built with uncommitted changes or without tests, start again from step 3: the
   release workflow refuses such a zip.
5. Create the GitHub release for the tag with both files, and publish it:

   ```powershell
   gh release create v1.2.3 <release dir>\Soltex-1.2.3-win-x64.zip <release dir>\SHA256SUMS.txt --title "Soltex 1.2.3" --notes-file <notes>
   ```

   With `--draft`, nothing more happens until you publish the draft.
6. Publishing starts `release.yml`. It checks the zip against `SHA256SUMS.txt`, reads `BUILD.json` from the zip and
   stops unless it names the tagged commit, the tag's version, a clean tree and a passed test run. It then installs the
   pinned Inno Setup, builds `Soltex-<version>-win-x64-setup.exe` from the app folder in the zip, installs, upgrades and
   uninstalls it on the runner, uploads it to the release and adds its line to `SHA256SUMS.txt`.
7. Check that the release has the zip, the setup and a `SHA256SUMS.txt` with two lines, and that both files match it
   (`sha256sum -c SHA256SUMS.txt`, or `Get-FileHash` in PowerShell).

If `release.yml` fails:

- **"... is not the release build of v1.2.3"**: the zip came from another commit, from uncommitted changes or from a
  run with `-SkipTests`, or the tag was moved. Rebuild from a clean checkout of the tag (steps 3 and 4), replace both
  files on the release (`gh release upload v1.2.3 <zip> <release dir>\SHA256SUMS.txt --clobber`) and run it again.
- **SHA-256 mismatch**: the zip and `SHA256SUMS.txt` on the release come from different `make-release.ps1` runs.
  Upload both from the same run and run it again.
- **A download or Chocolatey error**: run the failed job again from the Actions tab.
- **ISCC or an install, upgrade or uninstall check fails**: the workflow uses `eng/soltex.iss` and `eng/installer/`
  from the tagged commit, so a fix needs a new commit, version and tag. The zip on the release stays usable meanwhile.

To run it again by hand, open **Actions › release › Run workflow** and enter the tag, or run
`gh workflow run release.yml -f tag=v1.2.3`. A manual run takes the workflow file from the branch you start it on
(normally `main`) and everything else from the tag. It replaces a setup that is already on the release, and its line
in `SHA256SUMS.txt`.

## Native audio driver (optional)

`native/Soltex.Audio.Driver` holds the source of Soltex's own virtual audio endpoints. It needs the WDK packages and
the MSVC toolchain listed in its `toolchain.lock.json`, builds an unsigned driver, and is not part of the normal build
or package. See its README.
