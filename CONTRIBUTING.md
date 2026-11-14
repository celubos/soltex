# Contributing

Thanks for your interest in Soltex. It is released under the [MIT License](LICENSE.txt), and by contributing you agree that your contributions are released under it too.

## Issues

- Search existing issues first.
- For a bug, include your Windows version and build, the Soltex version, what you did, what you expected and what
  happened. A screenshot helps for UI problems; crash reports are in `%LOCALAPPDATA%\Soltex\logs\crash`.
- Report security problems privately as described in [SECURITY.md](SECURITY.md), not in an issue.

## Pull requests

- Keep each pull request to one change, and open an issue first for anything large.
- Build and run the relevant tests before opening it. [docs/BUILDING.md](docs/BUILDING.md) has the commands.
- Describe what changed and how you tested it. Mention anything you could not test (for example, a Windows version or
  audio hardware you do not have).
- CI must pass: the Release build, the test suites, `eng/verify-identity.ps1` and `eng/verify-design-tokens.ps1`.

## Code style

- Warnings are errors (`Directory.Build.props`). Nullable reference types are on.
- Match the style of the surrounding code; keep comments short and about why, not what.
- Every change Soltex makes to Windows must be reversible: record the previous value before writing, and undo only
  what still holds the value Soltex wrote. Add new settings to the catalogues in `src/Soltex.Customize`.
- New features are off by default and do nothing in the background until the user turns them on.
- No telemetry, analytics or network calls that the user did not ask for.
- Colours go in `src/Soltex.App/Themes/Tokens.xaml` only. See [docs/DESIGN_SYSTEM.md](docs/DESIGN_SYSTEM.md).
- Treat files, paths and anything read from disk or the network as untrusted. See
  [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).

Architecture notes: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
