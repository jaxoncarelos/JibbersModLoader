# Rules for AI agents working with the Jibbers Mod SDK

Read `README.md`, then `docs/SDK.md`. Work from `template/` and keep your mod in its own folder next to `crates/`.

## Build and ship

- Build: `cargo build --release` in your mod folder. The output is `target\release\<lib name>.dll`.
- Copy it to `<game>\JibbersMods\`. The loader loads a temp copy, so overwriting the file while the game runs is
  safe, but a new DLL is only picked up on the next launch.
- Do not start, kill or attach a debugger to the game unless the user asks. Ask the user to restart it and to
  paste `jibbers_hotload.log` (beside `Jibbers.exe`).
- Plugin id: lowercase letters, digits, underscore. Prefix custom commands with it (`my_mod.status`).

## Safety rules (a violation crashes the game, usually natively, with no Rust panic to catch)

1. Touch Unity/IL2CPP objects only on the main thread: inside a hook callback, or via `host::pump::run`.
   Never from `Plugin::init`, `changed`, `command` or your own threads directly.
2. Hooks (`host::pre_hook_method`) take only void instance methods with at most two non-float parameters. There
   are 12 hook slots in total and at most 3 plugins per method. Hook callbacks must not panic and must not block;
   do the work in a helper that returns a `Result`.
3. Look up classes and methods by name; never hard-code addresses or offsets. The game's code is obfuscated and
   changes between updates, so check every lookup result and log failures instead of unwrapping.
4. Do not call `host::list_plugins`, `host::call_plugin` or `runtime::call` from `Plugin::init` or `Plugin::unload`.
5. Do not block the main thread waiting for another thread that waits for `pump::run`.
6. Free what you create (GC handles, game objects) in `Plugin::unload`.

## Game builds

- The loader fingerprints the game before hooking anything. On a build that is not in `compat/compat.json` (or the
  newer list the updater installs) it loads no mods and logs `game build ... not validated; mods disabled`.
- That is intended. Do not remove the check, edit `gamebuild.rs` or `compat.json` to get around it, and do not
  tell the user to set `JIBBERS_HOTLOAD_FORCE=1` as a fix. The variable is for testing your own mod on a new build
  you are about to validate, at your own risk (see `docs/GAME-BUILDS.md`).
- When the game updates, expect hooks to break. Re-check every class and method your mod resolves.

## Files you must not edit

- `JibbersMods\sdk\` and `JibbersMods\jibbers_sdk.dll` (managed by the updater, overwritten on update).
- `version.dll` (the loader). It is not auto-updated; it comes with the SDK package.
- `crates/jibbers-sdk-runtime/src/trust.rs` (signing keys) and anything under `compat/`.

## Useful

- Check what is available at runtime: `runtime::has_feature("sdk.build")`; the runtime commands are in
  `docs/RUNTIME.md` (kept current by the updater at `JibbersMods\sdk\docs\RUNTIME.md`).
- Unit-test pure logic with `cargo test`; code that needs the game cannot run in tests.
