# Jibbers Mod Loader

A mod loader for the game **Jibbers** (Windows, Unity IL2CPP), a **C# SDK** for writing mods against it, and example mods built on it.

**Mod in C#:** start with [docs/guides/getting-started.md](docs/guides/getting-started.md). The [`docs/`](docs/) folder is the SDK's website (GitHub Pages): guides and a searchable reference of every SDK feature, game class and game event.

- **Loader** (`crates/jibbers-hotload`): ships as `version.dll` next to the game executable. It waits for IL2CPP, hooks a few per-frame methods so mod work runs on Unity's main thread, loads every plugin DLL in `<game>\JibbersMods\`, and owns the shared **Options > Mods** tab. It fingerprints the game first and loads nothing on a build that isn't on the compatibility list (see [sdk/docs/GAME-BUILDS.md](sdk/docs/GAME-BUILDS.md)).
- **C# SDK** (`sdk/dotnet/`): `Jibbers.Sdk.dll`, with mods, settings and the Mods tab, events, game settings, input, hooks and detours, IL2CPP and Unity. It includes generated bindings for every game class (stable `Jibbers.Sdk.Game.*` names and raw `Jibbers.Interop.*`), the template, examples, the bindings/docs generator and tests ([sdk/README.md](sdk/README.md), [sdk/AGENTS.md](sdk/AGENTS.md) for AI agents).
- **.NET bridge** (`sdk/crates/jibbers-dotnet-bridge`): `JibbersMods\jibbers_dotnet.dll` starts .NET inside the game and registers every C# mod with the loader.
- **Native SDK** (`sdk/crates/`): the plugin ABI the loader speaks, the runtime plugin with a signed auto-updater, the symbol catalogue (`symbols.json`) and maintainer tools. The original mods are Rust plugins on it.
- **Core** (`crates/jibbers-hotload-core`): the game-facing code of the example mods plus developer probes. It builds three ways: the full developer core, `--features atmosphere-only` and `--features reshred-only` (each one a plugin DLL).
- **C# mods** (`mods/cs/`): C# ports of Atmosphere, Reshred, FastLoad, CollisionStream, Filmer and SkiSmooth, built on the SDK, with their Rust tests carried over ([coverage](docs/guides/coverage.md)).
- **Mods** (`mods/`): [Atmosphere](mods/JibbersAtmosphere) (weather presets with a prebuilt asset bundle and its Unity project), [Reshred](mods/JibbersReshred), and the player-facing release notes ([mods/RELEASE-README.txt](mods/RELEASE-README.txt)).

Mod settings are available in **Options > Mods** from both the main menu and the in-game phone.
The tab is recreated when the scene changes, and opening it reloads current settings so changes
made through mod hotkeys appear in the controls.

The new [public mod manager and update channel](docs/DISTRIBUTION.md) uses GitHub Actions and
Releases to build, sign and distribute the loader and public mods. Install the small bootstrap once;
then select mods in **Options > Mods > Install and update mods** and receive updates at startup.
The workflow is prepared in `.github/workflows/mod-channel.yml`; publishing requires the protected
environment's signing secret. Original project code is licensed under [GPL-3.0-only](LICENSE).
Third-party components retain their own licenses.

## Build and test

```powershell
cargo test -p jibbers-hotload --lib                       # loader
cargo test -p jibbers-hotload --lib --features release    # loader, player build
cargo test -p jibbers-hotload-core --lib                  # core (also with --features atmosphere-only / reshred-only)
cd sdk; cargo test --workspace                            # SDK, runtime, publish tool
```

## C# SDK

```powershell
cd sdk\dotnet
dotnet build Jibbers.Sdk -c Release
dotnet run --project Jibbers.Sdk.Tests -c Release                   # tests
dotnet run --project Jibbers.Sdk.Generator -c Release -- resolve --dump <dump.json>
tools\package_dotnet.ps1 -BundleRuntime                             # dist\JibbersCSharp.zip, dist\JibbersCSharpSDK.zip
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
