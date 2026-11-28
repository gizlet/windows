# Gizlet Launcher for Windows

Gizlet Launcher for Windows installs and runs apps from the [Gizlet Store](https://gizlet.io), keeps each local profile's saves and settings apart, and writes Gizlet OS cards for the R36S. It is a C#/.NET 10 WinUI 3 app in a Fluent side bar over Mica, in English and Simplified Chinese, light and dark. Windows 11 x64 is the platform it is accepted on.

| Page | Does |
| --- | --- |
| Apps | Favourites, then every installed app: open, favourite, update, uninstall, and import a local `.gzl` (Ctrl+O) |
| Store | Featured shelves, search (Ctrl+F) and a page per app, showing only apps with a package this PC runs, with Installed and Update badges |
| Flash a card | Writes Gizlet OS (the latest release or a local `.img`/`.img.gz`) over a whole R36S card |
| Settings | Profiles, language, appearance, Launcher updates and starting with Windows |

Ctrl+1, Ctrl+2, Ctrl+3 and Ctrl+, switch pages; F5 refreshes the current one.

## How it works

- **Data.** Everything lives in `%LOCALAPPDATA%\Gizlet` (`GIZLET_PREFIX` to choose another folder), laid out as on a Gizlet OS card. `device/` is shared by every profile: installed apps in `device/apps/<id>/`, which repository each Store install came from in `device/store/origins.json`, downloads, cached card images and the current profile. `profiles/<id>/` belongs to one profile: `settings.json` (language, appearance, favourites, recently opened) and each app's saves in `apps/<app-id>/`. Installing, updating and uninstalling the Launcher never touch this folder. [Layout](src/Gizlet.Core/DataLayout.cs)
- **Packages.** A `.gzl` is unpacked into a staging folder and checked before anything installed changes: canonical entry paths that stay inside the folder and do not collide by case or Unicode form, no links, devices or encryption, size limits, `CHECKSUMS` covering exactly the other files, an app manifest (schema 1 or 2, as [gizlet-toml.md](https://github.com/gizlet/gizlet/blob/main/docs/gizlet-toml.md) specifies) for a `*-pc-windows-*` target this PC runs, and an `entry` that is a PE executable of that target's machine. Only then is the installed folder swapped for it. A Store install records its repository and GitHub id; another repository never replaces it. Uninstalling removes the package and keeps every profile's saves. [Reader](src/Gizlet.Core/Packages/PackageReader.cs) · [Library](src/Gizlet.Core/Library/AppLibrary.cs)
- **Running apps.** An app starts in its folder with `GIZLET_APP_ID`, `GIZLET_APP_DIR`, `GIZLET_SAVE_DIR` (its folder in the current profile), `GIZLET_LANG` and `GIZLET_SWAP_AB`, as on Gizlet OS. Profiles cannot be switched while an app runs. [Runner](src/Gizlet.Launcher/Services/AppRunner.cs)
- **Store.** `https://gizlet.io/api/v1` (`GIZLET_STORE_API` for another). x64 PCs ask for `x86_64-pc-windows-*` packages; Arm64 PCs take `aarch64` packages first and x64 ones under emulation. Downloads are checked against the size and SHA-256 the Store gives. Store installs are checked for updates at start and on request. [Client](src/Gizlet.Core/Store/StoreClient.cs)
- **Flashing.** The Launcher runs as the user (`asInvoker`). It lists only removable media in USB, SD and MMC readers that report card changes, holding no Windows, boot, EFI, recovery or page-file volume and not the Launcher's own folders, with reader, size, volume names and the panel read from the card ([as gizlet/os's writer does](https://github.com/gizlet/os/blob/main/boards/gameconsole/r36s/README.md#display-variants); the user chooses when the card does not tell). The official image comes from the release `GET /api/v1/os/releases/latest` names, trusted only with a valid signature from the Gizlet OS release key, then checked against `SHA256SUMS`. The image is prepared before the confirmation, which names the card and asks for its name typed out; a card taken out, put back or swapped since then is refused. The Launcher then opens `Gizlet.FlashHelper.exe`, denying writes and renames until the helper has connected, checks its SHA-256 against the one `scripts/package.ps1` built into the Launcher (and, for a signed Launcher, its Authenticode publisher; [HelperTrust](src/Gizlet.Core/Flash/HelperTrust.cs)), and starts it (`requireAdministrator`) through UAC with the confirmed disk's identity and the image's path, size and SHA-256. The helper trusts none of it: it identifies the disk again (device path, size, serial, media change count, volumes) and applies the same rules, hashes the image from the handle it writes from, locks and dismounts every volume (`FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME`), checks the media change count and partition types once more on the open disk, writes the image with partition 3 and its ext4 filesystem grown to the card, and reads everything back from the media (unbuffered) and compares it. It never touches a filesystem, and it is one Native AOT executable that loads native libraries only from System32, so nothing beside it in the user-writable install folder can run elevated (`scripts/check-imports.ps1` checks its imports at build time). Afterwards the Launcher, as the user, selects the Panel 4 overlay when chosen, on the FAT `GIZLETBOOT` volume of the same device. Cancelling UAC or changing the card writes nothing. Progress comes back over a named pipe only the current user can open. [Helper](src/Gizlet.FlashHelper/Program.cs) · [Writer](src/Gizlet.Core/Flash/CardWriter.cs) · [Disks](src/Gizlet.Windows/DiskEnumerator.cs)
- **Updates.** At start and every 120 seconds the Launcher reads `https://gizlet.io/assets/windows/update.json` (`GIZLET_UPDATE_URL`), asking caches to revalidate. It holds a manifest and its `ssh-keygen -Y sign` signature (namespace `gizlet-windows-update`) made with the key whose public half is [devops/update-key.pub](devops/update-key.pub), built into the Launcher. A newer version is offered with its notes. On confirmation the installer is downloaded (resumably) and run only when its size and SHA-256 match the signed manifest. A Launcher that is Authenticode-signed also requires the installer's signature to be valid and from its own publisher, so once releases are signed they stay signed; an unsigned release (built before a code-signing certificate exists) relies on the signed manifest alone. A development build, not made by `scripts/package.ps1` (`-p:GizletPackaged=true`), never installs updates ([InstallerTrust](src/Gizlet.Core/Updates/InstallerTrust.cs)). While a card is being written the install waits. The installer waits for the Launcher to exit, replaces it and opens it again; any failure leaves the current version running. [Updates](src/Gizlet.Launcher/Services/LauncherUpdates.cs) · [Manifest](src/Gizlet.Core/Updates/UpdateManifest.cs)
- **Installer.** [Inno Setup 7](installer/GizletLauncher.iss), per user into `%LOCALAPPDATA%\Programs\Gizlet Launcher`, with no administrator rights, a Start menu shortcut and an uninstaller that keeps user data.

## Development

Requires the .NET 10 SDK. On any OS, the platform-independent library and its tests:

```sh
dotnet test --project tests/Gizlet.Core.Tests
```

On Windows, `scripts/ci.ps1` runs what CI runs: every test, an unsigned installer (installing Inno Setup 7 for the user if needed), a silent install, the Launcher's `--smoke` start and an uninstall that must keep user data. `scripts/package.ps1 -Version 1.2.3` builds an installer alone. On macOS and Linux the Windows projects compile with `dotnet build src/Gizlet.Launcher -p:WindowsAppSDKSelfContained=false` up to the Windows-only resource tools.

| Path | Holds |
| --- | --- |
| `src/Gizlet.Core/` | Platform-independent logic: packages, library, profiles, Store, signatures, updates, card images |
| `src/Gizlet.Windows/` | Win32: disks, raw disk access, volume locks, Authenticode, starting with Windows |
| `src/Gizlet.Launcher/` | The WinUI 3 app, built in code except `App.xaml`, which merges the Fluent control resources; `Strings.cs` holds every string in both languages |
| `src/Gizlet.FlashHelper/` | The elevated card writer |
| `tests/Gizlet.Core.Tests/` | Unit tests, including the ext4 grow against gizlet/os's layout and the release manifest the Launcher accepts |
| `tests/Gizlet.Windows.Tests/` | Tests against the real Windows APIs, skipped elsewhere |
| `installer/` | The Inno Setup script |
| `scripts/` | `ci.ps1`, `package.ps1`, `install-inno.ps1`, `check-imports.ps1`, `ui-check.ps1`, `update-rehearsal.ps1` |
| `tools/Gizlet.StoreCheck/` | The Store end-to-end check |
| `devops/` | The release script, signing setup, the update key's public half and the encrypted release secrets |
| `docs/releases/` | Each release's notes, `<tag>.en.md` and `<tag>.zh-CN.md`, shown in the Launcher's update banner |

## CI and code review

Follows the [gizlet/.github](https://github.com/gizlet/.github) setup. Pull requests to `main` must pass `CI` and `Codex Code Review` and are squash-merged.

| Workflow | Does |
| --- | --- |
| `triage.yml` | On every pull-request push, gizlet-bot comments `/ci` and `/codex-review` for the head, which starts the two workflows below. A writer's comment with either command runs it again. GitHub-hosted, never checks out PR code. |
| `codex-review.yml` | The `Codex Code Review` check, with review guidance for card-write targets, the UAC boundary, update verification and profile data. GitHub-hosted. |
| `ci.yml` | The `CI` check: `scripts/ci.ps1` at the tested commit, with a read-only token and no secrets. It installs the Launcher, opens its real window, walks every page in English and light and again in Simplified Chinese and dark (`scripts/ui-check.ps1`), and keeps the screenshots as the `ui-screenshots` artifact. |
| `release.yml` | Releases, below. |
| `update-rehearsal.yml` | On `main`, weekly and on demand, on a fresh GitHub-hosted runner: twice, unsigned and signed with a throwaway Authenticode certificate trusted only there, builds v0.0.1 and v0.0.2 with a throwaway update key (`package.ps1 -TestUpdateKey`, which releases never pass). It installs v0.0.1 with real data (The Little Prince from the Store, two profiles, a favourite and a save) and leaves it running. It then publishes v0.0.2 to a local server and waits for the banner in the next 120-second poll. It clicks "Update and restart" and checks that v0.0.2 reopens with the apps, profiles, settings and saves kept (`scripts/update-rehearsal.ps1`). No release credentials. Not a pull-request check. |
| `store-e2e.yml` | On `main`, daily and on demand: `tools/Gizlet.StoreCheck` searches the live Store, installs The Little Prince, opens it in one of two profiles with the Launcher's environment (Mesa's software OpenGL on the GPU-less runner), plays its saving replay, and checks that the saves and favourites stay with that profile through an uninstall and a reinstall. Not a pull-request check. |

Windows jobs run on GitHub-hosted `windows-2025` runners, a fresh VM for each run, so the Release job that decrypts the signing credentials never shares a machine with pull-request code. Comment-triggered workflows run their copy on `main`, so a pull request that changes them is tested by the old ones.

## Releasing

Add `docs/releases/vX.Y.Z.en.md` (and `.zh-CN.md`) on `main`, then push the tag `vX.Y.Z` on that commit. The Release workflow:

1. checks that the tag is `vMAJOR.MINOR.PATCH` on a commit in `main`, with notes;
2. on the Windows runner, tests, then builds the self-contained Launcher, the flash helper and the installer, Authenticode-signing and timestamping them (and the uninstaller) and verifying the signatures when code-signing credentials exist, or leaving them unsigned with a warning until then;
3. on a GitHub-hosted runner, verifies the installer's signature again (`release.py --unsigned` when the build was not signed), signs `update.json`, refuses a version that is not newer than the published one, uploads the installer to `windows/releases/<tag>/` in R2 without overwriting, downloads it through `gizlet.io/assets` to check its SHA-256, and only then uploads `windows/update.json` (`Cache-Control: no-cache`) and checks that it is served;
4. attaches the installer and `update.json` to the GitHub release.

Running Launchers find the new version within two minutes. Installers are at `https://gizlet.io/assets/windows/releases/<tag>/GizletSetup-<version>-x64.exe`; `https://gizlet.io/assets/windows/latest/x64` redirects to the newest.

Credentials are in `devops/secrets/release.enc.yaml`, SOPS-encrypted to the organization's age key (the `SOPS_AGE_KEY` secret, granted to this repository): `UPDATE_SIGNING_KEY` (the update key's private half), the R2 credentials, and the code-signing credentials, either `CODESIGN_PFX_BASE64` and `CODESIGN_PFX_PASSWORD` or Azure Artifact Signing's `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` ([signing.ps1](devops/scripts/signing.ps1)). Without code-signing credentials a release is published unsigned: Windows SmartScreen warns when the installer is opened, and updates are trusted through the signed manifest. Once credentials are added, every later release must be signed by the same publisher. Edit the file with `sops devops/secrets/release.enc.yaml`. Only the tag-triggered Release jobs decrypt it, and the Windows job decrypts only the code-signing fields.

## License

[Apache-2.0](LICENSE)
