# Threat model

## Scope

Soltex is a Windows desktop app (`src/Soltex.App`) that changes per-user Windows settings, makes administrator changes
through a short-lived elevated helper, restyles the taskbar through a helper loaded into Explorer, runs audio through
an unelevated helper process, shows Windows protection status, assesses files dropped into its imports folder, and can
start a separately installed RustDesk client. The update planner (`src/Soltex.Update`) is a library with no installer
behind it. The audio broker and driver are source only and are not installed by any build.

Assets: user files and quarantined payloads, the Windows settings Soltex changes and their journals, allow-list
decisions, the honesty of displayed protection health, local audit integrity, audio routes and app routing policy,
release and metadata signing keys, update policy and staged artifacts, the approved RustDesk fingerprint, and the
user's intent behind each remote session.

## Actors and inputs

- **Attacker-controlled:** files, archives, executables and presets that reach Soltex; file names, paths, reparse
  points and files changed between check and use; update descriptors, manifests, signatures, redirects and response
  bodies; peer IDs received through untrusted channels.
- **User-controlled:** which settings to change, administrator prompts, scan targets, quarantine restore/delete,
  allow-list entries, the RustDesk executable and each remote launch, audio devices and routes.
- **Developer-controlled:** source, signing keys, approved publishers, CI, release packaging.
- **Platform-controlled:** Windows Security Center, Defender, AMSI, Authenticode, DPAPI, UAC, Explorer, the Windows
  audio stack, file-system semantics and enterprise policy.

## Trust boundaries

1. **Files to intake.** `PathSafety`, `FileHashing`, `FileAssessmentService` and AMSI mediate files entering the
   imports folder.
2. **Soltex to Windows protection.** Security Center, Defender and AMSI results may be unavailable, managed by another
   provider, stale or limited by policy.
3. **Per-user Windows settings.** `Soltex.Customize` writes only `HKEY_CURRENT_USER` values from its catalogue,
   journals each previous value before writing, and undoes only values that still hold what it wrote.
4. **Unelevated app to elevated helper.** The request file names catalogue entries by id. The helper accepts it only
   under its fixed name in `%LOCALAPPDATA%\Soltex\System`, rejects redirected folders and unknown ids, and runs nothing
   else. UAC is the consent step for every elevated run.
5. **App to Explorer.** The taskbar helper is copied to the user's data folder under a hash-derived name and loaded
   into the user's own Explorer. It gains no privilege the user did not already have, takes settings only from
   `HKCU\Software\Soltex\Taskbar`, and keeps original values so it can restore them.
6. **UI to audio helper.** A current-user named pipe with a per-launch token, bounded frames, a strict schema and a
   fixed operation list. The helper watches its parent's PID and start time and exits with it.
7. **App routing.** `Soltex.Audio.WindowsRouting` uses an undocumented Windows policy interface, checks the target
   process identity immediately before each call, journals all prior values, and fails closed where the interface is
   unavailable.
8. **Detection to quarantine and restore.** Metadata is authenticated, payloads are re-hashed, and restore writes
   to the recorded path or a collision-safe sibling.
9. **Per-user state.** DPAPI and HMACs detect accidental or cross-account changes. They do not stop malware running
   as the same user.
10. **Soltex to RustDesk.** A one-way launch of a fingerprinted, Authenticode-trusted executable with fixed
    arguments. RustDesk and its network are a separate trust domain.
11. **Release infrastructure to update preview.** Descriptor signature quorum, pinned HTTPS and bounded acquisition
    before any byte is staged. No installer crosses this boundary.

## Invariants

- Never disable, weaken, exclude, replace or misreport the registered antivirus provider.
- Never put an attacker-controlled path into executable PowerShell text.
- Never trust a path before full normalization, root containment where it applies, and reparse-point rejection.
- Never let the elevated helper run anything not in `SystemCatalog`, or read its request from outside the app's data
  folder.
- Never write per-user settings outside the catalogue, and never undo a value that no longer holds what Soltex wrote.
- Never load the taskbar helper into any process other than the user's Explorer (or the test host).
- Never restore a quarantined payload before its metadata is authenticated and its SHA-256 matches.
- Never turn a clean AMSI result or an unknown executable into publisher trust.
- Never accept an allow-list rule broader than one exact SHA-256.
- Never report unknown provider health as healthy.
- Never treat a peer ID as command-line text, supply a remote password, configure unattended access, request
  elevation for RustDesk, or launch it after its fingerprint or signature check fails.
- Never fetch update artifacts before the descriptor meets the signature quorum, follow a redirect to an unsigned
  origin, or execute staged bytes.
- Never upload user data. Soltex has no telemetry.

## Attack surface and mitigations

### Defender process boundary

A scan path may contain quotes or PowerShell syntax. `PowerShellDefenderClient` uses fixed scripts and passes the
normalized path through `SOLTEX_SCAN_PATH`, where it is read as data. The child starts in its own directory, imports
system modules by full path, and uses module-qualified cmdlets, so a same-named command elsewhere cannot win. Output is
bounded while read and parsed as JSON; raw stderr is replaced with an exit-code message because it can echo the path.

### Native library loading

The security assembly resolves its P/Invokes (AMSI, Security Center, WinTrust, Crypt32, Kernel32) only from System32,
so a DLL in the app or current directory cannot satisfy them.

### Defender events

Event XML can contain file and process paths. The query is fixed to documented event IDs and bounded by look-back,
count, a 512 KiB output ceiling and a timeout. The parser builds descriptions from allow-listed non-path fields only.

### File intake

An attacker may supply a large, malformed or rapidly changing file. Soltex rejects reparse points, hashes streams,
limits AMSI buffers to 16 MiB and sends larger files to a Defender custom scan. A file can still be swapped between
assessment and later use; consumers must use the validated handle or re-check.

### Settings changes and the elevated helper

A same-user attacker can write the request file, but it can only name catalogue entries and still needs the user to
accept the UAC prompt; a redirected request folder is refused. The journals are plain per-user files: a same-user
attacker can edit them to make undo skip a value, but undo still writes only catalogue values and only where the
current value matches the journal. App removals are not reversible from Soltex and say so before running.

### Taskbar helper

The staged DLL sits in a user-writable folder. A same-user attacker could replace it, but that attacker can already run
code as the user, including in Explorer. The app compares the staged file with the bundled one by SHA-256 before
loading it. The helper uses only Windows system DLLs, makes no network connections, and writes only its log and an
optional diagnostic dump of the taskbar's visual tree.

### Audio helper and routing

Only the parent's pipe client with the per-launch token is served. Requests are size-limited and schema-checked;
a timed-out mutation is reported as unknown and never replayed. Routing calls re-check the target process (PID,
start time, path, session) before each native call, but Windows accepts a PID, so a narrow PID-reuse race remains.
Every routing change is journaled and undo restores only values that still match.

### Manifests, publishers and updates

Covered in [SUPPLY_CHAIN_SECURITY.md](SUPPLY_CHAIN_SECURITY.md): exact-byte signatures, publisher subject and key
pins, anti-rollback sequence state, bounded ZIP staging, pinned HTTPS and a non-installing planner. Production keys,
pins and a release source are not configured.

### Quarantine and allow list

Metadata is authenticated with a DPAPI-protected key, payloads are re-hashed, restore never overwrites and refuses
paths inside quarantine, and cross-volume moves are copy-verify-delete. A same-user attacker can still delete state.

### Imports watcher and audit log

The watcher queue holds at most 128 paths and drops the oldest under flood; Windows real-time protection remains the
primary control. Audit entries store HMACs of paths, are chained and rotate at 4 MiB. Local deletion or truncation
remains possible and is not described as tamper-proof logging.

### Remote help

The user may be persuaded to pick a renamed or replaced executable or to connect to the wrong peer. Soltex requires
the exact file name, rejects reparse points, fingerprints the bytes, checks Authenticode, repeats both before each
launch, limits peer IDs to a strict character set passed as one argument, and asks for confirmation. It cannot prove
the remote party's identity or that the file is an official RustDesk release.

## Out of scope

A kernel attacker, a compromised Windows trust provider, a stolen signing key, a malicious administrator, same-user
malware with unrestricted process injection, boot-time malware, raw-disk modification, and supply-chain compromise
before signing.

## Severity guide

**Critical**
- An update or release path runs attacker code without a valid approved signature.
- The elevated helper can be made to run something outside its catalogue.
- Soltex silently disables or weakens the registered antivirus provider.

**High**
- Scan-path injection reaches PowerShell execution.
- Archive or manifest handling writes outside its staging root.
- Quarantine restore overwrites application or start-up binaries without authenticated metadata and user intent.
- The remote-help path supplies credentials, skips consent or launches attacker-chosen code.
- The taskbar helper can be made to load into another user's or an elevated process.

**Medium**
- A file swap between assessment and use lets unvalidated content through.
- Same-user tampering produces false health, forged allow entries, misleading audit data or a wrong undo.
- Raw paths or file contents reach logs.
- An app-routing change lands on the wrong process through PID reuse.

**Low**
- Malformed but non-executed input causes bounded UI degradation.
- Audit rotation drops old non-critical history.
- Wording is stale or confusing but does not change a protection or trust decision.
