# Gizlet Launcher for Windows

Gizlet Launcher for Windows installs and runs apps from the [Gizlet Store](https://gizlet.io) on Windows, keeps each local profile's saves and settings apart, and writes Gizlet OS cards for the R36S. It is a C#/.NET 10 WinUI 3 app; Windows 11 x64 is the platform it is accepted on.

This repository is being set up. The Launcher, the card-writing helper, the installer and the release pipeline arrive in the following pull requests.

## Development

Requires the .NET 10 SDK. The platform-independent library builds and tests on any OS:

```sh
dotnet test --solution Gizlet.Windows.slnx
```

On Windows, `scripts/ci.ps1` builds and tests everything CI does.

| Path | Holds |
| --- | --- |
| `src/Gizlet.Core/` | Platform-independent logic |
| `tests/Gizlet.Core.Tests/` | Its tests (xunit v3 on Microsoft.Testing.Platform) |
| `scripts/ci.ps1` | The CI build |

## CI and code review

Follows the [gizlet/.github](https://github.com/gizlet/.github) setup. Pull requests to `main` must pass `CI` and `Codex Code Review` and are squash-merged.

| Workflow | Does |
| --- | --- |
| `triage.yml` | On every pull-request push, gizlet-bot comments `/ci` and `/codex-review` for the head, which starts the two workflows below. A writer's comment with either command runs it again. GitHub-hosted, never checks out PR code. |
| `codex-review.yml` | The `Codex Code Review` check, with review guidance for card-write targets, the UAC boundary, update verification and profile data. GitHub-hosted. |
| `ci.yml` | The `CI` check: `scripts/ci.ps1` at the tested commit, with a read-only token and no secrets. |

Builds run on the runner the `RUNNERS` organization variable maps to `windows-x64`, falling back to GitHub's `windows-2025` runner until that key exists. Comment-triggered workflows run their copy on `main`, so a pull request that changes them is tested by the old ones.

## License

[Apache-2.0](LICENSE)
