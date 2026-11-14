# Audio control tests

The default run executes 13 deterministic tests without creating `AudioDeviceService`, opening audio devices, registering device notifications, or reading native meters. It covers priority/exclusion/manual-pin policy, stable IDs, selective recovery rules, legacy model compatibility and fake-resource cache ownership. This is the CI/package-build mode.

```powershell
dotnet run --project tests/Soltex.Audio.Control.Tests/Soltex.Audio.Control.Tests.csproj --artifacts-path <build-dir>
```

Use the explicit hardware mode on a Windows machine with active audio endpoints:

```powershell
dotnet run --project tests/Soltex.Audio.Control.Tests/Soltex.Audio.Control.Tests.csproj --artifacts-path <build-dir> -- --live-read-only
```

This runs all 16 tests, including the unchanged strict checks for an active endpoint, actual meter support, notification registration, cache invalidation/retry and native worker disposal with pending reads. Missing devices or meter support fail this explicit mode; they are not silently counted as passing. The probes submit only inputs that must be rejected before writes and do not apply volume, mute, route or default changes. They establish observations/lifecycle behavior, not audible transport.

`--help` prints the modes; unknown options fail. When invoking it directly you can point TEMP/TMP, DOTNET_CLI_HOME and NUGET_PACKAGES at an isolated build directory; `eng/build-audio.ps1` already sets isolated locations.
