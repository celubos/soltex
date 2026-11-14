# Owned audio deployment: verifier and plan

`owned-audio-deployment.ps1` inventories a bounded package, checks exact owned INF identity, matches every payload SHA256 against the checked-in release allowlist, and verifies an approved release's catalog and broker signatures. It implements **Inspect / Plan / Verify only**. It does not install, uninstall, create a service/device, request elevation, change audio defaults or reboot. The current unsigned prototype is intentionally rejected. `owned-audio-release-allowlist.json` contains no approved production releases.

Example read-only invocation (use a new receipt filename):

```powershell
pwsh -File eng/owned-audio-deployment.ps1 -Mode Plan `
  -PackagePath <reviewed package folder> `
  -OutputPath <new receipt>.json
```

A complete package has `SoltexAudio.inf`, `SoltexAudio.sys`, `SoltexAudio.cat`, and a `Broker` directory containing the published `Soltex.Audio.Broker.Service.exe` and its dependencies. The future reviewed allowlist entry must pin **every** relative file/hash, a unique ReleaseId, the Microsoft kernel catalog signer certificate thumbprint, and the broker publisher certificate thumbprint plus exact subject. No catalog/publisher identity is invented from the current unsigned build. Extra files, changed bytes, traversal/alternate-stream paths and reparse points are rejected. Approved entries are trusted source changes requiring review, not package-provided manifests.

For a genuinely approved release, `-Mode Verify -ReleaseId <reviewed-id> -SignToolPath <installed-SDK-signtool.exe>` requires valid pinned signatures and executes Microsoft SignTool `verify /kp /v /c` separately for the INF and SYS. The tool itself must have a valid Microsoft signature. This checks kernel-policy catalog membership, not just an EXE Authenticode signature. Payloads are rehashed afterward. Verification exit code is 2 when blocked. A successful signature result still leaves `DeploymentReady=false` because the mutation/recovery executor is not implemented.

## Explicit remaining apply gap

The emitted plan names the exact `ROOT\SoltexAudio` device, `SoltexAudio` driver service and `SoltexAudioBroker` SYSTEM broker. A future administrator-only executor must register a root devnode through `SetupDiCreateDeviceInfoW` / `SPDRP_HARDWAREID` / `DIF_REGISTERDEVICE` before installing the pinned INF. `pnputil /add-driver /install` alone does not create this root device. It must capture defaults and exact ownership, stage into protected storage, revalidate there, journal each mutation, retain the exact instance/OEM INF, and handle partial rollback/reboot-required state without an automatic reboot. Broker configuration is a quoted protected executable path followed by **only `--service`**, own-process LocalSystem, with protected SCM/file ACLs; no automatic service or audio start is included in the plan.

Uninstallation must match a protected installation receipt against the current instance, hardware ID, package hash and service identity, stop only the owned broker gracefully, use exact-instance `DiUninstallDevice`, and remove the exact driver-store package only when no other instance uses it. Vendor devices and later user changes must be preserved. Neither this transactional executor nor its Windows lab acceptance is supplied here; do not call this script a complete installer.

Run inert checks with `pwsh -File eng/test-owned-audio-deployment.ps1 -ArtifactDirectory <scratch directory>`. The fixtures stay there for review. These tests establish planning and rejection behavior; they do not substitute for a genuinely Microsoft-signed catalog, administrator installation or native audio acceptance.

The plan follows Microsoft's [root device creation API](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdicreatedeviceinfoa), [DIF_REGISTERDEVICE contract](https://learn.microsoft.com/en-us/previous-versions/windows/drivers/install/dif-registerdevice), [driver update API](https://learn.microsoft.com/en-us/windows/win32/api/newdev/nf-newdev-updatedriverforplugandplaydevicesw), [device/package removal guidance](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-devices-and-driver-packages-are-uninstalled), [SignTool kernel verification](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/signtool), and [service access rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights).
