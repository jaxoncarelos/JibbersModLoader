# Automatic upgrade from the old loader

SDK release 0.1.2 bridges installations of the old `version.dll` to the new phone UI and mod manager.
It uses the **existing signed SDK updater**, without changing the install-path rules understood by older
runtimes. The SDK manifest delivers a new runtime and these files under `JibbersMods/sdk/loader/`:

- `version.dll`: stable bootstrap, with the phone UI in its updateable implementation.
- `jibbers_loader.dll`: the new implementation.
- `jibbers-migrate.exe`: an installer helper.
- `install.json` and `.sig`: a second signed envelope authenticating those three exact files.

When the updated runtime runs, it checks the signature, size and hashes before starting a private copy
of the helper. The helper waits for the launching Jibbers process to exit, verifies the payload again,
installs the implementation, backs up the old loader, and atomically replaces `version.dll`.
It never kills or restarts the game. Player settings and local-only mods are untouched.

The new phone UI is active on the next launch after migration. Clients still running SDK 0.1.1 first
download the runtime update; the migration code runs when that updated runtime is loaded on a later
launch. This is automatic, but does require normal game exits/restarts for the loaded DLLs to change.
Installs with no SDK updater cannot receive this retroactively; the bootstrap ZIP is available for them.

After migration, routine loader and public mod features update through the signed GitHub mod channel.
The old SDK migration seed never overwrites a newer implementation installed by that channel.

For maintainers, `tools/package_loader_bridge.ps1` prepares an unsigned bundle while retaining the
previous SDK release's source/doc files. The Actions publishing job signs it with the existing SDK key
and publishes its SDK manifest as the latest SDK release. No change to `trust.rs` is needed.
