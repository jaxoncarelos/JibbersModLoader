# Game builds, validation and updates

## What the loader checks

The mods hook obfuscated game code. If the game updates and the code moves, a hook can crash inside Unity before any
Rust code can react. So before it touches IL2CPP or hooks anything, the loader fingerprints the game:

- size of `GameAssembly.dll`
- a hash of its PE headers (includes the link timestamp)
- the Unity version string of `UnityPlayer.dll` (for example `6000.3.12f1 (fca03ac9b0d5)`)

All three must match a build in the compatibility list. The list is `compat/compat.json`, compiled into the loader and
the runtime. A newer copy installed by the SDK updater (`JibbersMods\sdk\compat.json`) takes over when its `sdkVersion` is
higher, so a game update can be validated without a new loader.

## When the build is not in the list

The loader hooks nothing and loads no plugin except the SDK runtime (which hooks nothing, so its updater can still fetch a
new list). It writes one line to `jibbers_hotload.log`:

```
game build GameAssembly 114143232 bytes, header ..., Unity ... not validated; mods disabled (loader built for ...)
```

and creates `JibbersMods\MODS-DISABLED.txt`. The Mods tab is not shown (the loader does not touch the game's UI on an
unknown build). There is no retry. The file is removed on the next launch on a validated build. If the build cannot be
identified at all the loader fails closed the same way.

A loader and plugin from different releases (ABI mismatch) are handled the same way for that plugin: one log line, skipped,
no retry.

## Forcing it (testing only)

Set the environment variable `JIBBERS_HOTLOAD_FORCE=1` (any value except empty or `0`) before starting the game to load
the mods on an unvalidated build. The log says so. Use it to test your mod on a new game build before validating it. A
crash on a forced build is not a bug in the mod or the loader.

## Validating a new game build (maintainer)

1. Update the game, load your mods with `JIBBERS_HOTLOAD_FORCE=1`, and test them.
2. Record the build, offline (reads `GameAssembly.dll` and `UnityPlayer.dll`; nothing is started):

   ```powershell
   cargo run -p jibbers-sdk-publish -- validate-build --game-dir "<game folder>" --compat compat\compat.json --label "<name of the build>"
   ```
3. Publish (below). Every release re-publishes the list, so installed SDKs learn the new build on their next start, and
   restart the game once to turn mods on. The loader in `bin\` also embeds the list at build time; rebuild it
   (`tools\package_sdk.ps1`) so a fresh install knows the build without updating.

`tools\verify_mod_release.py` fails a release if the list is invalid, if the loader does not carry the current list, or if a
release README does not name the validated builds.

## Auto-updates

At game start the runtime waits about 15 s, then fetches `manifest.json` and `manifest.json.sig` from the update URL. It
installs a release only if the Ed25519 signature verifies against a key compiled into the runtime (`trust.rs`) and its
`sdkVersion` is greater than the installed one. Files are downloaded to a staging folder, checked (size, SHA-256) and swapped in
with rollback. The runtime DLL itself is replaced in `JibbersMods\`, so new runtime features load at the next launch.
State is in `JibbersMods\sdk\installed.json`. `version.dll` and other mods are never touched.

The update URL is compiled in (`tools\package_sdk.ps1 -UpdateUrl <https folder>`; until set, the updater is idle).

## Publishing an SDK release (maintainer)

```powershell
cargo run -p jibbers-sdk-publish -- keygen          # once; back the key up. Paste the public key into trust.rs
tools\package_sdk.ps1 -UpdateUrl https://github.com/<owner>/<repo>/releases/latest/download -Publish 0.2.0
```

This produces `dist\JibbersModSDK.zip` (for new users) and `dist\sdk-release\0.2.0\` (manifest, signature, assets). Upload every
file of the second folder as assets of one GitHub release. Bump the version in `Cargo.toml` (`[workspace.package]`) as well.
