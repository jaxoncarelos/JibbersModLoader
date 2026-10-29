# Rules for AI agents working with the Jibbers Mod SDK

New mods are written in **C#** against `dotnet/Jibbers.Sdk`. Read `README.md`, then the guides in
`../docs/guides/` (getting-started, concepts, native). Look up APIs in `../docs/data/api.json` (every
SDK type and member, every game symbol and event) or the generated code in `dotnet/Jibbers.Sdk/Generated/`.

## Build and ship

- Start from `dotnet/template/MyMod`. Build: `dotnet build -c Release`. With the game at the default
  Steam path the build copies the DLL to `<game>\JibbersMods\cs\`; pass `-p:JibbersNoInstall=true` to
  only build. A new DLL is picked up on the next game launch.
- Do not start, kill or attach a debugger to the game unless the user asks. Ask the user to restart it and
  to paste `jibbers_hotload.log` (beside `Jibbers.exe`).
- Mod id (`Id`): lowercase letters, digits, underscore. Prefix custom commands with it (`my_mod.status`).

## Safety rules (a violation crashes the game, natively, with nothing to catch)

1. Touch game objects only on the main thread: inside hook, detour, event, watch or key-binding
   callbacks, or in `MainThread.Run` / `MainThread.Later`. `OnLoad`, `OnSettingChanged` and
   `OnCommand` are not on the main thread.
2. Use the friendly `Jibbers.Sdk.Game.*` bindings. Never hard-code addresses, offsets or obfuscated
   names (two or three lowercase letters, like `bjc` or `<sdj>k__BackingField`). Raw `Jibbers.Interop.*`
   bindings use real names that change with game updates: prefer adding a symbol (`docs/guides/reverse-engineering.md`).
3. Pre-hooks (`Hooks.Before`, `XxxMethod.Before`) take instance methods returning void or bool with at
   most two non-float parameters; 12 per game. Install hooks in `OnLoad`. To skip/wrap a zero-argument
   void instance method, use `XxxMethod.Around(original => ...)` and call `original()` synchronously
   when the game should continue. Other signatures use `Detour<TDelegate>` with a typed `Original`.
4. A detour's `[UnmanagedCallersOnly]` function must match the native signature exactly: IL2CPP
   methods take `(this, args..., MethodInfo*)`. Never let an exception escape it.
5. Root game objects you keep across frames (`.Keep()` / `GameRef`); check `IsDead()` before using
   a Unity object you kept.
6. Free what you create in `OnUnload` (the SDK removes your hooks, detours, events and watches).
7. Do not write symbols or code for the game's networking, server-side checks or other players'
   state (see "Not covered on purpose" in `docs/guides/coverage.md`). For mods that play together,
   use the SDK's Steam party (`Jibbers.Sdk.Multiplayer.Party`, `docs/guides/multiplayer.md`), and
   validate every message you receive: it comes from another player's PC. The SDK's built-in Play
   together joins and tunnels the game's own servers; keep that internal (no public API for it).

## Game builds

- The loader fingerprints the game before loading anything. On a build not in `compat/compat.json` (or
  the newer list the updater installs) it loads no mods and logs `game build ... not validated; mods disabled`.
- That is intended. Do not remove the check, edit `gamebuild.rs` or `compat.json` to get around it, and do
  not tell the user to set `JIBBERS_HOTLOAD_FORCE=1` as a fix (see `docs/GAME-BUILDS.md`).
- When the game updates, dump it and run `jibbers-gen resolve`: fix failing symbols before anything else.

## Files you must not edit

- `JibbersMods\sdk\`, `JibbersMods\jibbers_sdk.dll`, `JibbersMods\dotnet\` (installed SDK files).
- `version.dll` (the loader). Only the signed SDK loader migration/updater owns replacement of it;
  ordinary mod code must not edit it directly.
- `dotnet/Jibbers.Sdk/Generated/` (regenerate with `jibbers-gen bindings`), `crates/jibbers-sdk-runtime/src/trust.rs`
  (signing keys) and anything under `compat/`.

## Native layer

The Rust crates (`crates/`) are the loader-facing layer: the plugin ABI, the .NET bridge, the runtime
and maintainer tools. The original mods in `../mods/` are Rust plugins on that layer and keep working.
Their rules are in `docs/SDK.md`.
