# Gizlet Launcher for Windows

Read `README.md` for the layout, build commands and design decisions before changing code.

- Keep platform-independent logic (packages, Store, profiles, updates, card images) in `src/Gizlet.Core` with tests in `tests/Gizlet.Core.Tests`; it builds and tests on any OS with `dotnet test --solution Gizlet.Windows.slnx`.
- The Launcher never runs elevated. Only the flash helper requests administrator rights, and it must treat every input it receives from the Launcher as untrusted.
- Never write to a disk without re-identifying it in the helper, and never relax the checks that keep system, boot and fixed disks out of reach.
- Updates install only after the manifest's SSH signature, the installer's SHA-256 and its Authenticode publisher all verify.
- Profile data (saves, favourites, settings) must survive installs, updates and uninstalls of apps and of the Launcher.
- Signing keys and R2 credentials are used only by the tag-triggered Release workflow, never by CI that runs pull-request code.
- Documentation is in English; UI strings are in English and Simplified Chinese.
