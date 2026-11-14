# Naming and data compatibility

Soltex was developed under an earlier working name, WaveSlate. A few identifiers from that name are still read by the
code because changing them would make existing per-user state unreadable. This document lists them and the rules that
protect them. `eng/verify-identity.ps1` fails CI if the old name appears anywhere else; the allowed places are listed
in `eng/identity-allowlist.json`.

## Data folder

`ProductDataRootResolver` (in `src/Soltex.Security`) picks exactly one data folder under `%LOCALAPPDATA%` and never
merges, copies or deletes state:

1. Normalize `%LOCALAPPDATA%` and reject a reparse point anywhere in it.
2. Look for `Soltex` and the legacy `WaveSlate` folder without creating either.
3. Neither exists: create and use `Soltex`.
4. Exactly one exists: use it. Using the legacy folder is a compatibility lookup, not a migration.
5. Both exist: stop with an error. Soltex will not choose by date, merge or delete.
6. Reject the selected folder, or a relevant child, if it is a reparse point or a file.

Security state, quarantine, the audit chain, release-sequence state, update-planning journals and the imports folder
all live under the one selected folder. The shell's own files (settings, change journals, audio profiles) live under
`%LOCALAPPDATA%\Soltex`.

There is no automatic folder move. One may be added only together with an authenticated migration journal, crash
recovery, conflict tests and a rollback path.

## Retained identifiers

| Identifier | Where | Why it stays |
| --- | --- | --- |
| `%LOCALAPPDATA%\WaveSlate` (and its `Security`, `Imports` children) | `ProductIdentity.LegacyStorageDirectoryName` | Used only when it is the sole existing data folder. |
| DPAPI description `WaveSlate authenticated state` | `ProductIdentity.LegacyDpapiDescription` | Embedded in existing DPAPI-protected keys. Changing it needs a versioned key migration with recovery tests. |
| Integrity-manifest schema 1 product `WaveSlate` | `ProductIdentity.LegacyIntegrityManifestProduct` | Accepted for schema 1 manifests only. New schema 1 output uses `Soltex`. |
| `WAVESLATE_RUN_EICAR` | `ProductIdentity.LegacyRunEicarEnvironmentVariable` | Test-only alias for `SOLTEX_RUN_EICAR`. |
| Bounded conflict message naming the legacy folder | `ProductDataRootResolver` | Tells the user which two folders conflict. |

Schema 2 signed releases use the product `Soltex` only, with no alias.

## Failure behaviour

- Both folders exist: fail before any store or monitor is created; report both paths; no automatic repair.
- Reparse point in a trusted path: fail before reading or writing.
- No folder: create only `Soltex` and its required children.
- Only the legacy folder: keep using it in place and label it as a compatibility selection in diagnostics.
- Torn or tampered authenticated files: the authenticated stores fail closed as usual.

## Removing an alias

A legacy identifier can be removed only when no supported release can still create it, downgrade and crash recovery
are documented, tests cover conflicts, tampering, reparse points and interrupted migration, and the release notes
announce the change.
