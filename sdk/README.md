# Jibbers Mod SDK

Write mods for Jibbers Playtest in Rust. A mod is a Windows x64 DLL that the mod loader (`version.dll`)
loads from `<game>\JibbersMods\`. The SDK gives it the Options > Mods tab, saved settings, hooks into game
methods, main-thread execution and raw IL2CPP/Unity access. Unofficial; not affiliated with the game's developer.

If you use an AI agent, point it at [AGENTS.md](AGENTS.md) first.

## Set up (once)

1. Install Rust (rustup, stable, `x86_64-pc-windows-msvc`) and the Visual Studio C++ build tools.
2. Close the game, then run `install.ps1` in this folder (PowerShell). It copies `bin\version.dll` (the loader)
   and `bin\jibbers_sdk.dll` (the SDK runtime) into the game folder, keeping any existing `version.dll` as
   `version.dll.bak`. Use `-GameDir` if the game is not in the default Steam folder.
3. Start the game once and check `jibbers_hotload.log` beside `Jibbers.exe`. You should see a line starting
   with `game build validated`. If it says `not validated; mods disabled`, read [docs/GAME-BUILDS.md](docs/GAME-BUILDS.md).

## Make a mod

```powershell
Copy-Item -Recurse template my-mod        # keep it next to crates\ (it depends on ..\crates\jibbers-mod-sdk)
cd my-mod
# edit Cargo.toml (names), src\lib.rs (INFO.id, settings, hooks)
cargo build --release
Copy-Item target\release\my_mod.dll "<game>\JibbersMods\"
```

Restart the game to load a new DLL (plugins load once per launch). Your settings appear under Options > Mods and
are saved in `JibbersMods\<id>.settings.json`. Problems and your own `log::info` lines go to `jibbers_hotload.log`.

## What is here

| Path | What |
| --- | --- |
| `template/` | Starter mod: settings, a per-frame hook, a custom command |
| `crates/jibbers-mod-sdk/` | The SDK your mod links against |
| `crates/jibbers-sdk-runtime/` | The runtime plugin (`jibbers_sdk.dll`) with the auto-updater |
| `crates/jibbers-sdk-publish/` | Maintainer tool for signed releases (not needed by modders) |
| `compat/compat.json` | Game builds the mods are validated for |
| `docs/SDK.md` | The guide: lifecycle, settings, hooks, threading, IL2CPP |
| `docs/RUNTIME.md` | Commands of the SDK runtime and how to call them |
| `docs/GAME-BUILDS.md` | Game-build guard, `JIBBERS_HOTLOAD_FORCE`, auto-updates, publishing |
| `bin/` | `version.dll` (loader) and `jibbers_sdk.dll` (runtime) |

## Updates

`jibbers_sdk.dll` checks a signed release at game start (after about 15 s, in the background) and installs newer
runtime features, docs, the template and the list of validated game builds under `JibbersMods\`, so you do not
need to download the SDK again. It never replaces `version.dll`. Turn it off in Options > Mods (Mod SDK) or with
`JIBBERS_SDK_NO_UPDATE=1`. Do not edit files under `JibbersMods\sdk\`; they are overwritten.
