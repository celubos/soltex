# Security architecture

Soltex is a security **companion**, not an antivirus engine. It uses the protection already registered with Windows,
shows its state honestly, and puts its own controls around the places where Soltex reads files, changes Windows, or
starts other programs. It adds no file-system filter, resident scanner or cloud service.

- Microsoft Defender, or another provider registered with Windows, supplies real-time protection.
- Windows Security Center supplies provider-neutral health.
- Defender's PowerShell module supplies Defender status, quick and custom scans, and intelligence updates.
- AMSI lets Soltex check content before it uses it.
- Soltex checks its own state and inputs with hashes, signatures, strict paths and authenticated metadata.

The user sees this on **Home** (protection status) and **Tools** (scans, recent Defender events, protected imports,
quarantine, remote help).

## Components

All in `src/Soltex.Security` unless noted.

| Component | File | Behaviour |
| --- | --- | --- |
| Provider health | `WindowsSecurityCenter.cs` | Calls `WscGetSecurityProviderHealth` for antivirus health. |
| Native import policy | `NativeImportPolicy.cs` | Restricts the assembly's P/Invoke loads to System32 to prevent DLL preloading. |
| Change signal | `WindowsSecurityChangeMonitor.cs` | `WscRegisterForChanges`; disposal unregisters the callback. |
| Protection monitor | `ProtectionMonitor.cs` | Serializes refreshes, coalesces signals, keeps last-known-good state, backs off after failures and reports recovery. |
| Defender commands | `PowerShellDefenderClient.cs` | Starts the System32 PowerShell host, imports the Defender, Diagnostics and Utility modules by full `$PSHOME` path, runs fixed module-qualified commands, passes a scan path only through an environment variable, and bounds output while reading it. |
| Defender events | `PowerShellDefenderClient.cs`, `DefenderEventLogParser.cs` | Reads an allow-list of Operational event IDs under count, time and byte limits; never exposes file, process or scan-resource paths. |
| AMSI | `AmsiContentScanner.cs` | Maps clean, policy-blocked, malware, unavailable and error results. |
| Intake | `FileAssessmentService.cs` | Rejects reparse points, hashes with SHA-256, sends at most 16 MiB to AMSI, flags executables for signature review. |
| Authenticode | `AuthenticodeVerifier.cs` | `WinVerifyTrust` with no UI, whole-chain policy, cached revocation, MD2/MD4 rejected. |
| Publisher policy | `PublisherPolicy.cs` | Binds Windows trust to an approved subject and public key on an immutable snapshot of the file. |
| Manifests | `IntegrityManifestVerifier.cs`, `SignedReleaseManifestVerifier.cs` | RSA-PSS/SHA-256 over exact bytes, then schema, containment, size and hash checks. |
| Allow list | `AllowListStore.cs` | Up to 512 exact SHA-256 entries in authenticated state. |
| Quarantine | `QuarantineStore.cs` | Moves or verified-copies payloads, authenticates the index, re-hashes before restore, never overwrites on restore. |
| Audit log | `SecurityAuditLog.cs` | Stores an HMAC of each path (not the path), chains entries, bounds fields, rotates at 4 MiB. |
| Imports watcher | `ImportFolderMonitor.cs` | Watches only the `Imports` folder with a bounded queue and debounce. |
| Data root | `ProductDataRootResolver.cs`, `Dpapi.cs` | Selects one data folder without merging; DPAPI protects the per-user HMAC key. |
| Update planner | `src/Soltex.Update` | Signed descriptors, pinned HTTPS, bounded acquisition and a non-installing plan. See [SUPPLY_CHAIN_SECURITY.md](SUPPLY_CHAIN_SECURITY.md). |

## Windows and Defender boundaries

Soltex reads Defender's status; it does not reproduce Defender's detection. The supported commands used are
`Get-MpComputerStatus`, `Get-MpPreference`, `Start-MpScan` and `Update-MpSignature`.

A clean AMSI result means the installed provider did not flag that buffer. It is not a trust decision, so executables
stay **review recommended** until signature policy also passes.

Windows Security Center health is authoritative when it is available: `Good` can mean another provider is active,
and `Poor`, `Snoozed` and `NotMonitored` are never overridden by Defender's own flags. Only when Security Center is
unavailable may an active Defender in `Normal` mode stand in, and the UI says so. Passive Defender never stands in.
Calling these APIs does not register Soltex as an antivirus provider, and Soltex does not query SmartScreen.

## Privilege boundaries

| Boundary | Design |
| --- | --- |
| Main app | Runs as the user, unelevated. Per-user changes go only to `HKEY_CURRENT_USER`. |
| Elevated helper (`Soltex.exe --system`) | Started through UAC for each administrator change. Reads a request from `%LOCALAPPDATA%\Soltex\System` that names catalogue entries by id; unknown ids, unexpected file names and redirected folders are rejected. It runs no commands or paths taken from the request, journals previous values, writes a result and exits. |
| Taskbar helper (`SoltexTaskbar.dll`) | Loaded into the user's own Explorer through a temporary thread hook and the XAML diagnostics API. It runs with the user's rights only, imports only Windows system DLLs, reads its settings from `HKCU\Software\Soltex\Taskbar`, and restores every value it changed when the look is set back to the Windows default. |
| Audio helper (`Soltex.Audio.Host.Service.exe`) | Unelevated. Controlled over a current-user named pipe with a per-launch token, a frame size limit and a fixed operation list; exits when its parent exits. |
| Audio broker and driver | Source only. The broker would run as a LocalSystem service and the driver is unsigned; no build installs, registers or starts either. See `src/Soltex.Audio.Broker/README.md`. |

## Remote help (RustDesk)

Tools > Remote help starts a separately installed RustDesk client. RustDesk is licensed under AGPL-3.0; Soltex does
not copy, link or bundle its code, binaries or assets. RustDesk owns transport, authentication, consent, updates and the
session itself.

`src/Soltex.RemoteAssist` and the Tools page:

- find `RustDesk\RustDesk.exe` only under the two Program Files roots, or accept a file the user picks with that exact
  name that is not a reparse point;
- record a SHA-256 fingerprint of the chosen file and require Windows Authenticode trust; both are checked again
  before every launch, and a changed file must be picked again;
- start sharing with no arguments, and start a connection with exactly `--connect <peer id>`, where the peer ID is
  3–64 ASCII letters, digits, `-` or `_`, passed through `ProcessStartInfo.ArgumentList` with `UseShellExecute=false`;
- ask for confirmation before each launch and write an audit entry without the peer ID.

Soltex never downloads or updates RustDesk, passes a password, configures unattended access, requests elevation,
installs a service, opens firewall ports or hides the RustDesk window. Authenticode trust shows that Windows trusts
the signature chain; it does not prove the file is an official RustDesk release. A short time-of-check/time-of-use
window remains between the last check and process creation.

## False positives

- An AMSI malware or administrator-policy block can stop an import.
- An unknown or unsigned executable is **review required**, not malware.
- The allow list accepts only an exact SHA-256.
- Restoring from quarantine shows a warning and never adds an allow-list entry.
- Soltex never adds Defender exclusions and has no automatic high-impact remediation.

## Privacy

- Soltex uploads no file, path, hash or sample, and has no telemetry. Defender may use Microsoft's cloud protection
  according to the user's Windows settings; Soltex only reports whether that layer is on.
- The audit log keeps an HMAC of paths rather than raw paths.
- PowerShell output is size-limited while it is read; overflow is discarded and rejected.

## What Soltex is not

Soltex does not provide, and must not claim: a file-system minifilter, process/memory/exploit/web/ransomware sensors,
an ELAM driver or protected service, Windows Security Center provider registration, signatures or cloud reputation,
vendor-grade tamper resistance, or measured detection rates.

## References

- [Microsoft Defender Antivirus protection layers](https://learn.microsoft.com/en-us/windows/security/book/operating-system-security-virus-and-threat-protection)
- [Defender PowerShell module](https://learn.microsoft.com/en-us/powershell/module/defender/)
- [Antimalware Scan Interface](https://learn.microsoft.com/en-us/windows/win32/amsi/antimalware-scan-interface-reference)
- [WscGetSecurityProviderHealth](https://learn.microsoft.com/en-us/windows/win32/api/wscapi/nf-wscapi-wscgetsecurityproviderhealth)
- [WscRegisterForChanges](https://learn.microsoft.com/en-us/windows/win32/api/wscapi/nf-wscapi-wscregisterforchanges)
- [Defender Operational event IDs](https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-microsoft-defender-antivirus)
- [WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data)
- [RustDesk](https://github.com/rustdesk/rustdesk) and its [license](https://github.com/rustdesk/rustdesk/blob/master/LICENCE)
