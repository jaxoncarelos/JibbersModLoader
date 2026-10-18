# Jibbers Mod Loader

A mod loader for the game **Jibbers** (Windows, Unity IL2CPP), an SDK for writing mods against it, and two example mods built on it: **Atmosphere** and **Reshred**.

- **Loader** (`crates/jibbers-hotload`): ships as `version.dll` next to the game executable. It waits for IL2CPP, hooks a few per-frame methods so mod work runs on Unity's main thread, loads every plugin DLL in `<game>\JibbersMods\`, and owns the shared **Options > Mods** tab. It fingerprints the game first and loads nothing on a build that isn't on the compatibility list (see [sdk/docs/GAME-BUILDS.md](sdk/docs/GAME-BUILDS.md)).
- **SDK** (`sdk/`): the crate mods link against, a runtime plugin with a signed auto-updater, the publish tool, a mod template and the docs for mod authors ([sdk/README.md](sdk/README.md), and [sdk/AGENTS.md](sdk/AGENTS.md) for AI agents).
- **Core** (`crates/jibbers-hotload-core`): the game-facing code of the example mods plus developer probes. It builds three ways: the full developer core, `--features atmosphere-only` and `--features reshred-only` (each one a plugin DLL).
- **Mods** (`mods/`): [Atmosphere](mods/JibbersAtmosphere) (weather presets with a prebuilt asset bundle and its Unity project), [Reshred](mods/JibbersReshred), and the player-facing release notes ([mods/RELEASE-README.txt](mods/RELEASE-README.txt)).

## Build and test

```powershell
cargo test -p jibbers-hotload --lib                       # loader
cargo test -p jibbers-hotload --lib --features release    # loader, player build
cargo test -p jibbers-hotload-core --lib                  # core (also with --features atmosphere-only / reshred-only)
cd sdk; cargo test --workspace                            # SDK, runtime, publish tool
```

## Package

```powershell
tools\package_mods.ps1                    # six player archives into dist\ (loader, each mod, complete, standalone)
tools\package_sdk.ps1 [-UpdateUrl <url>]  # dist\JibbersModSDK.zip for mod authors
tools\package_sdk.ps1 -Publish 0.1.0      # also a signed auto-update release in dist\sdk-release\0.1.0
```

Both scripts build from a fresh staging folder and verify the result offline (`tools/verify_mod_release.py`, `sdk-publish verify`). Neither reads or modifies a game install.

## Developer tools

A loader built without the `release` feature serves JSON requests on `127.0.0.1:47820` and loads `jibbers_hotload_core.dll` from the game folder if present. `tools/hotload.py` is a small client (`ping`, `status`, `raw '<json>'`), and the core's probes (`dump`, `inspect`, `objects`, `call`, ...) are documented in the header of [crates/jibbers-hotload-core/src/lib.rs](crates/jibbers-hotload-core/src/lib.rs). Set `JIBBERS_HOTLOAD_DISABLE=1` to start the game without the loader, and `JIBBERS_HOTLOAD_FORCE=1` to load on an unvalidated game build.

The loader's log is `jibbers_hotload.log` beside the game executable.
