# Jibbers Mod Loader

A mod loader for the game **Jibbers** (Windows, Unity IL2CPP), a **C# SDK** for writing mods against it, and the mods built on it.

**Mod in C#:** start with [docs/guides/getting-started.md](docs/guides/getting-started.md). The [`docs/`](docs/) folder is the SDK's website (GitHub Pages): guides and a searchable reference of every SDK feature, game class and game event.

- **Loader** (`crates/jibbers-hotload`): ships as `version.dll` next to the game executable. It waits for IL2CPP, hooks a few per-frame methods so mod work runs on Unity's main thread, loads every plugin DLL in `<game>\JibbersMods\`, and owns the shared **Options > Mods** tab. It fingerprints the game first and loads nothing on a build that isn't on the compatibility list (see [sdk/docs/GAME-BUILDS.md](sdk/docs/GAME-BUILDS.md)).
- **C# SDK** (`sdk/dotnet/`): `Jibbers.Sdk.dll`, with mods, settings and the Mods tab, events, game settings, input, hooks and detours, IL2CPP and Unity. It includes generated bindings for every game class (stable `Jibbers.Sdk.Game.*` names and raw `Jibbers.Interop.*`), the template, examples, the bindings/docs generator and tests ([sdk/README.md](sdk/README.md), [sdk/AGENTS.md](sdk/AGENTS.md) for AI agents). The SDK contains no gameplay code.
- **.NET bridge** (`sdk/crates/jibbers-dotnet-bridge`): `JibbersMods\jibbers_dotnet.dll` starts .NET inside the game and registers every C# mod with the loader.
- **Native SDK** (`sdk/crates/`): the plugin ABI the loader speaks, the runtime plugin with a signed auto-updater, the symbol catalogue (`symbols.json`) and maintainer tools. The loader and the .NET bridge are built on it; mods are written in C#.
- **Mods** (`mods/cs/`): [Alpine Vision](mods/cs/AlpineVision), [FPV](mods/cs/FPV), [Rewind](mods/cs/Reshred) and [Atmosphere](mods/cs/Atmosphere), each a standalone C# project with a `<Mod>.Tests` project beside it ([walkthrough](mods/cs/README.md), [coverage](docs/guides/coverage.md)). Alpine Vision replaces Atmosphere in the update channel; Atmosphere remains as source.
- **Mod assets** (`mods/JibbersAlpineVision`, `mods/JibbersAtmosphere`, `mods/JibbersFPV`): the Unity projects and prebuilt asset bundles those mods load.
- **macOS preview** (`mac/`, `crates/jibbers-macos-ui`): an experimental native Apple Silicon host for the same SDK and mods ([mac/README.md](mac/README.md)).

Mod settings are available in **Options > Mods** from both the main menu and the in-game phone.
The tab is recreated when the scene changes, and opening it reloads current settings so changes
made through mod hotkeys appear in the controls.

The [public mod manager and update channel](docs/DISTRIBUTION.md) uses GitHub Actions and
Releases to build, sign and distribute the loader and public mods. Install the small bootstrap once;
then select mods in **Options > Mods > Install and update mods** and receive updates at startup.
The workflow is prepared in `.github/workflows/mod-channel.yml`; publishing requires the protected
environment's signing secret. Original project code is licensed under [GPL-3.0-only](LICENSE).
Third-party components retain their own licenses.

## Build and test

```powershell
cargo test -p jibbers-hotload --lib                       # loader
cargo test -p jibbers-hotload --lib --features release    # loader, player build
cd sdk; cargo test --workspace                            # native SDK, runtime, publish tool, .NET bridge
```

## C# SDK and mods

```powershell
dotnet build sdk\dotnet\Jibbers.Sdk -c Release
dotnet run --project sdk\dotnet\Jibbers.Sdk.Tests -c Release            # SDK tests
dotnet run --project mods\cs\FPV.Tests -c Release                       # mod tests (likewise AlpineVision, Atmosphere, Reshred)
dotnet run --project sdk\dotnet\Jibbers.Sdk.Generator -c Release -- resolve --dump <dump.json>
```

## Package

```powershell
tools\package_dotnet.ps1 -BundleRuntime   # dist\JibbersCSharp.zip, JibbersCSharpMods.zip, JibbersCSharpSDK.zip
tools\package_sdk.ps1 [-UpdateUrl <url>]  # dist\JibbersModSDK.zip: SDK runtime plugin and updater
tools\package_sdk.ps1 -Publish 0.1.0      # also a signed auto-update release in dist\sdk-release\0.1.0
```

The packaging scripts build from a fresh staging folder and verify the result offline (`tools/verify_csharp_packages.py`, `sdk-publish verify`). None of them reads or modifies a game install. The macOS preview is packaged by `.github/workflows/mac-preview.yml` (`tools/package_macos.py`).

## Developer tools

A loader built without the `release` feature serves JSON requests on `127.0.0.1:47820`. `tools/hotload.py` is a small client (`ping`, `status`, `raw '<json>'`); the C# [Inspector example](sdk/dotnet/examples/Inspector) answers probes such as `dump`, `class` and `live` through `Jibbers.Sdk.Diagnostics.InspectorCommands`. Set `JIBBERS_HOTLOAD_DISABLE=1` to start the game without the loader, and `JIBBERS_HOTLOAD_FORCE=1` to load on an unvalidated game build.

The loader's log is `jibbers_hotload.log` beside the game executable.
