# Supply-chain security

Code: `src/Soltex.Security`, `src/Soltex.Update`. The update planner installs nothing, and the app has no updater or
activation step. Releases do ship an installer: an unsigned, per-user Inno Setup `setup.exe` built in CI from the
release zip (section 8). It does not use the planner.

## 1. Scope

This document describes the user-mode supply-chain controls in Soltex. Their purpose is to make package, plugin,
preset, installer and update work fail closed at explicit trust boundaries.

The implemented components are:

- `AuthenticodeVerifier` and `AuthenticodePublisherVerifier`;
- `PublisherPolicy`;
- `SignedReleaseManifestVerifier`;
- `ReleaseSequenceStore`;
- `BoundedArchiveStager`;
- `UpdateDescriptorVerifier`, `UpdateTrustTransitionEvaluator`, and `PinnedHttpsTransport`;
- `BoundedHttpsAcquirer`, `SoltexUpdatePlanner`, and `UpdatePlanningJournal`;
- `Soltex.Security.SupplyChain.Tests`, `Soltex.Security.Hardening.Tests`, and `Soltex.Update.Tests`.

These components are not a release service. Given an already configured signed descriptor and trust policy, the planner can authenticate and download bounded inert artifacts into private staging, verify them, and produce a user-visible plan. It does not install, execute, elevate, activate, repair, roll back, or uninstall software. No production Soltex certificate, metadata/release public key, TLS pin, signed trust policy, descriptor source or updater is configured, and the planner is not connected to the release installer.

## 2. Threats addressed

The current primitives are designed to reject or expose:

- an unsigned or Windows-untrusted executable;
- an executable signed by a trusted but unapproved publisher;
- a same-subject certificate with a different public key;
- a signer certificate without explicit code-signing usage;
- ambiguous multi-signature PE files at the publisher-authorization boundary;
- a file changed while its signature and publisher identity are being checked;
- a detached release manifest with an invalid signature, unknown schema fields, wrong product identifier, invalid sequence, non-UTC publication time, noncanonical path, duplicate path, reparse traversal, length mismatch, or SHA-256 mismatch;
- a lower release sequence;
- a different signed manifest reusing an accepted sequence;
- ordinary local mutation of release-sequence state;
- concurrent Soltex processes racing the same release-sequence state;
- ZIP traversal, alternate-data-stream syntax, Windows device names, case collisions, file/directory collisions, reparse/symbolic links, unsupported types, excessive entry count, excessive expansion, excessive compression ratio, and incomplete failure cleanup.

The controls are deliberately narrow. They do not establish that content is benign, high quality, compatible, licensed, or appropriate to execute.

## 3. Publisher authorization

### 3.1 Windows trust first

Soltex asks `WinVerifyTrust` to apply the Windows software-publisher trust policy to an existing non-reparse file. The publisher-authorization path requests embedded signature index `0` through `WINTRUST_SIGNATURE_SETTINGS` with `WSS_VERIFY_SPECIFIC`, requests the number of secondary signatures with `WSS_GET_SECONDARY_SIG_COUNT`, and rejects a file that reports any secondary signature.

Microsoft documents `WinVerifyTrust` as the API through which an application asks a registered trust provider to verify that an object satisfies a specified trust operation. Microsoft also documents `WINTRUST_SIGNATURE_SETTINGS` as the structure used to choose a signature index and retrieve the secondary-signature count.

Official references:

- [WinVerifyTrust function](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust)
- [WINTRUST_SIGNATURE_SETTINGS structure](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_signature_settings)
- [Microsoft WinVerifyTrust signature-verification sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/winverifytrust-signiture-verification/)

### 3.2 Why one embedded signature is required

After Windows reports success for the explicitly requested signature index, Soltex reads the embedded signer certificate to derive the publisher identity. Requiring zero secondary signatures makes that certificate selection unambiguous for this bounded implementation.

The observed `dwVerifiedSigIndex` is recorded, but it is not used as the authorization condition: on GitHub-hosted Windows runners `WinVerifyTrust` validated the requested index `0` while reporting an inconsistent observed index. The enforced conditions are therefore:

1. requested signature index is exactly `0`;
2. `WinVerifyTrust` succeeds;
3. reported secondary-signature count is exactly `0`.

Support for deliberately multi-signed release files would require a different implementation that enumerates each embedded signature and binds the selected certificate evidence to the corresponding Windows trust result. That is not implemented.

### 3.3 Approved identity

A trusted signature is necessary but not sufficient. `PublisherPolicy` also requires:

- the code-signing enhanced-key-usage OID `1.3.6.1.5.5.7.3.3`;
- SHA-256 of the exact encoded X.500 subject name;
- SHA-256 of the certificate's SubjectPublicKeyInfo;
- a match against an explicit approved-publisher entry.

This avoids relying on display-name string parsing or a certificate thumbprint alone. The public-key pin remains stable across a normal certificate renewal only when the same key is deliberately reused; a new key requires an explicit policy update.

The file is SHA-256 hashed before and after trust and identity evaluation. A mismatch produces `FileChangedDuringVerification` rather than an approval. A later installer must still bind approval to the exact bytes it activates; this primitive does not reserve the path against subsequent same-user replacement.

### 3.4 Production configuration gap

No Soltex production publisher entry exists, and release builds are not code-signed. The automated test observes the trusted signer on the installed Microsoft `.NET` host and constructs a temporary in-memory policy for that exact test file.

Before production use, release engineering must define:

- certificate source and legal publisher name;
- hardware-backed or managed private-key custody;
- authorized signing operators and build identities;
- backup and disaster recovery;
- revocation and compromise response;
- timestamping policy;
- approved subject-name and SPKI hashes;
- overlapping pin rotation and retirement;
- evidence showing the shipped installer and binaries match the approved identity.

Private keys, certificate passwords, signing tokens, and recovery secrets must not enter source control, logs, shared storage, or ordinary application settings.

## 4. Signed release manifest

`SignedReleaseManifestVerifier` verifies a detached RSA-PSS/SHA-256 signature over the exact manifest bytes with a caller-supplied public key. It then deserializes strict, case-sensitive JSON with unknown members rejected.

The supported schema is version `2` with exact product identifier `Soltex`. A release includes:

- channel;
- positive sequence;
- version label;
- explicit UTC publication timestamp;
- one or more file entries containing canonical relative path, byte length, and SHA-256.

### 4.1 Canonical file paths

A manifest file path must:

- use forward slashes only;
- be relative and nonempty;
- contain no backslash, root, drive/colon, control, empty segment, `.` or `..` segment;
- contain no trailing space or dot in any segment;
- avoid Windows reserved device names;
- remain within path-length and depth bounds;
- remain exact after normalization;
- be unique under Windows case-insensitive comparison.

This prevents signed aliases such as `./plugin.dll` from resolving to the same file under a different manifest identity.

The content root and every existing path component are checked for reparse points. Each file must match the signed byte length and SHA-256.

### 4.2 Successful-result integrity

`SignedReleaseVerificationResult` has an internal constructor. Ordinary external callers can inspect a result but cannot directly instantiate a successful result through the public API. `ReleaseSequenceStore` accepts only a successful result with a valid manifest and manifest SHA-256.

This is an API-hardening boundary, not a protection against reflection, arbitrary code execution inside the process, or a compromised runtime.

### 4.3 Unimplemented release-key policy

The verifier receives a public key from its caller. The repository does not yet contain the production Soltex release public key, a signed key-rotation document, a trust-root bootstrap, or a feed-selection policy.

A later release design must distinguish:

- code-signing certificate identity;
- release-manifest signing identity;
- update-transport authentication;
- pinned trust-root rotation;
- emergency revocation and recovery.

One compromised identity must not silently mint replacements for all other identities without an explicit recovery policy.

## 5. Local anti-rollback state

`ReleaseSequenceStore` maintains the highest accepted sequence and manifest SHA-256 for each bounded release channel.

Accepted outcomes:

- first verified release for a channel;
- higher verified sequence;
- exact replay of the same accepted manifest.

Rejected outcomes:

- unverified input;
- lower sequence;
- same sequence with a different manifest hash.

The state is serialized through the existing authenticated per-user JSON store. Its authentication key is protected using the existing Windows per-user DPAPI boundary. The store validates schema, unique channels, sequence, version, hash, UTC acceptance time, and reparse-free state path.

### 5.1 Cross-process serialization

The in-process semaphore is supplemented by a state-directory lock file opened with `FileShare.None`. Each read or read-modify-write operation:

1. acquires the instance semaphore;
2. attempts to acquire the cross-process file lock;
3. retries at 50-millisecond intervals;
4. honors caller cancellation;
5. fails after a bounded ten seconds;
6. rejects a reparse-point lock file;
7. holds the lock through authenticated load, validation, and save.

The operating system releases the file handle when a process exits. The lock file contains no credentials or state; it is only a serialization primitive.

The focused regression initializes accepted state, holds the lock with a separate file handle, confirms that another store operation cancels, releases the handle, and confirms that the same store instance can read the accepted sequence afterward.

### 5.2 Anti-rollback nonclaim

This is local authenticated state, not a TPM monotonic counter, secure boot measurement, remote transparency log, or server-enforced feed sequence. It detects ordinary mutation and feed rollback against the state currently present on disk and prevents cooperating processes from racing a read-modify-write operation.

It does not prove resistance to a fully compromised same-user account. Such an account can deny service by holding or replacing user-owned files and may be able to restore an older authenticated state file together with corresponding DPAPI-protected key material. Stronger rollback resistance would require a separately threat-modeled hardware, server, or transparency-log anchor.

## 6. Bounded ZIP staging

`BoundedArchiveStager` treats every ZIP field and payload as untrusted. It opens the archive read-only, iterates entries manually, and writes regular files into a random private staging directory with `FileMode.CreateNew`.

Microsoft's current .NET archive guidance recommends manual iteration for untrusted input, explicit destination-path validation, entry and expanded-size limits, overwrite prevention, and deterministic disposal.

Official reference:

- [Best practices for ZIP and TAR archives](https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices)

### 6.1 Enforced limits

Default limits are explicit values in `ArchiveStagingLimits`:

- maximum archive bytes: 256 MiB;
- maximum entries: 4,096;
- maximum expanded bytes per entry: 256 MiB;
- maximum total expanded bytes: 1 GiB;
- maximum compression ratio: 200:1;
- maximum relative path length: 240 characters;
- maximum path depth: 32 segments.

Callers may supply stricter valid limits. The stager checks both declared metadata and actual streamed bytes.

### 6.2 Path and type rejection

The stager normalizes backslashes to forward slashes before path registration, so slash/backslash aliases collide under one canonical archive path. It then rejects:

- rooted paths and traversal;
- alternate-data-stream syntax and invalid Windows characters;
- controls, empty segments, trailing spaces/dots, and reserved device names;
- case-insensitive duplicates;
- file-versus-directory collisions;
- DOS reparse attributes;
- Unix symbolic-link mode;
- non-regular/non-directory Unix entry types;
- reparse points that appear in the staging path.

Each successfully staged file receives an incremental SHA-256 record.

### 6.3 Cleanup and inertness

A rejected or cancelled staging operation attempts to remove its private tree. A successful `StagedArchive` owns the tree and removes it on disposal. Cleanup deliberately avoids recursively following a reparse point.

Successful staging is not approval. The stager does not:

- recursively unpack nested archives;
- invoke AMSI as an installation decision;
- load an assembly or plugin;
- launch an executable or script;
- copy files into an application directory;
- grant permissions;
- accept a release sequence;
- verify a manifest or publisher automatically.

Those steps require an explicit orchestrator and separate evidence.

## 7. Implemented non-installing composition

`SoltexUpdatePlanner` produces a typed, user-visible plan in this order:

1. identify channel and currently accepted sequence;
2. acquire bounded metadata and package bytes over authenticated transport;
3. place downloads in a private, reparse-free location;
4. verify the release-manifest signature against the pinned release trust root;
5. stage the archive with strict limits;
6. bind every staged file to the canonical signed manifest;
7. apply Authenticode publisher authorization to every executable file required by policy;
8. evaluate the release-sequence decision without mutating installation state;
9. show exact publisher, version, sequence, files, hashes, permissions, disk impact, and recovery plan;
10. issue an exact, expiring confirmation challenge for a later installer/elevation boundary.

Acquisition begins only after the exact descriptor bytes satisfy the active metadata-signature quorum. `PinnedHttpsTransport` exposes only HTTPS responses whose observed TLS SubjectPublicKeyInfo hash belongs to the active signed pin set. `BoundedHttpsAcquirer` rejects unsigned redirect origins, excess redirects, missing or mismatched lengths, excess bytes, hash mismatch, timeouts, cancellation, and reparse/collision conditions; it cleans partial private staging on failure.

The planner's deterministic preview binds product, channel, current/target version, target sequence, descriptor/manifest/package hashes, release key, descriptor signers, approved publishers, exact file changes, disk impact, warnings, recovery prerequisites, and confirmation to one plan SHA-256. `PreparedSoltexUpdate` keeps the underlying inert artifacts locked and removes them on disposal.

`UpdatePlanningJournal` retains at most 128 sanitized phase records in authenticated current/last-known-good state. Recovery inspection reports unfinished attempts and existing Soltex-owned private staging tokens without displaying the token or a raw path in the WPF surface. Cleanup is explicit and bounded to tokens already authenticated in the journal.

A later update-installing phase must then provide transactional activation, rollback, repair, uninstall, crash recovery, disk-full handling, locked-file handling, reboot behavior, and retained evidence. It must not execute directly from the archive or staging directory. (The release `setup.exe` is a separate, ordinary Inno Setup installer; it is not that phase.)

## 8. Build inputs

- NuGet: the only third-party package is NAudio.Wasapi 2.3.0 (and its NAudio.Core dependency), pinned by exact version.
  Its MIT notice ships in `src/Soltex.Audio.Engine/THIRD-PARTY-NOTICES.txt`.
- Native toolchains: `native/Soltex.TaskbarStyle/toolchain.lock.json` and `native/Soltex.Audio.Driver/toolchain.lock.json`
  pin each download by URL, byte size and SHA-256. The taskbar build script refuses an archive that does not match and
  checks that the built DLL imports only Windows system libraries.
- CI: every GitHub Action is pinned to a full commit SHA. Checkout never keeps credentials.
- Installer: `eng/soltex.iss` is compiled with Inno Setup 6.7.1. CI gets it through `eng/installer/get-inno-setup.ps1`,
  which installs exactly that Chocolatey package version when it is missing (Chocolatey checks the Inno Setup download
  against the SHA-256 recorded in that package version) and fails unless an `ISCC.exe` under Program Files or
  Program Files (x86) reports that file version. To move to a newer Inno Setup, change the version in that script.
  The installer is not code-signed.
- Release workflow (`.github/workflows/release.yml`): runs when a release is published, or by hand for a tag. It
  checks the zip against `SHA256SUMS.txt`, then reads `BUILD.json` from the zip and stops unless `SourceCommit` is the
  tag's commit, `Version` equals the tag without its `v`, `SourceDirty` and `TestsSkipped` are false and
  `Verification.Status` is `Passed`. The job has `contents: write`, but the token is passed only to the two steps that
  run `gh` (download and upload); the steps that install Inno Setup, compile and run the installer do not get it.
  Before uploading, it installs, upgrades and uninstalls the new `setup.exe`, the last two while Soltex runs
  (`eng/installer/test-installer.ps1`). It rewrites `SHA256SUMS.txt` with LF line endings and no byte order mark, so
  `sha256sum -c` works.
- Release tooling also runs before a release: `package-smoke.yml` compiles the installer and runs the same install,
  upgrade and uninstall checks; `audio.yml` builds the self-contained integrated package and makes the release zip
  from it with `eng/make-release.ps1`.
- The test suites cover subject/key policy matching, code-signing EKU, primary-signature verification, release
  sequence upgrade/replay/rollback/equivocation, authenticated-state tampering, cross-process locking, manifest path
  canonicalization, archive traversal/collision/link/size/ratio limits, descriptor quorum and expiry, redirects, trust
  rotation, exact acquisition bytes, cleanup and expiring confirmation (`tests/Soltex.Security.SupplyChain.Tests`,
  `tests/Soltex.Security.Hardening.Tests`, `tests/Soltex.Update.Tests`).

## 9. Not claimed

This implementation is not described as:

- a complete signed release system;
- an installing updater or release service (the per-user `setup.exe` is a plain, unsigned Inno Setup installer);
- production-ready;
- resistant to a fully compromised user account;
- resistant to compromised release infrastructure;
- a transparency log;
- a malware detector;
- a registered antivirus provider;
- a substitute for Defender, another registered provider, SmartScreen, or Windows trust policy;
- proof that staged content is safe to execute;
- proof that an approved publisher's software is benign.

Minifilter, ELAM, PPL/protected service, Windows Security Center provider registration, cloud reputation, detection research, MVI participation, certification, and efficacy claims remain separate future programs.
