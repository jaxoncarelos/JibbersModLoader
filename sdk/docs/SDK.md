# SDK guide

API reference lives in the crate docs: `cargo doc --open -p jibbers-mod-sdk` in this folder.

## A plugin

```rust
use jibbers_mod_sdk::plugin::{Info, Plugin};
use jibbers_mod_sdk::{declare_plugin, host, json, log, Setting, Value};

struct MyMod;
impl Plugin for MyMod {
    const INFO: Info = Info { id: "my_mod", name: "My Mod", version: env!("CARGO_PKG_VERSION"), author: "me", description: "..." };
    fn settings() -> Vec<Setting> { vec![Setting::toggle("fancy", "Fancy", true)] }
    fn init() { /* look up classes, install hooks */ }
}
declare_plugin!(MyMod);
```

`declare_plugin!` generates the DLL exports `jh_core_init`, `jh_core_handle`, `jh_core_free`, `jh_core_unload`.
The crate type must be `cdylib` (see `template/Cargo.toml`).

Lifecycle: the loader loads every `JibbersMods\*.dll` once, about 8 to 45 s after the game starts (when its window
is up). `Plugin::init` runs once on the loader's thread. Requests (`plugin.*` and your commands) arrive from any thread.
A new DLL needs a game restart.

Trait items: `INFO`, `settings()`, `init()`, `changed(key, value)`, `command(cmd, req)`, `unload()`,
`ENABLED_TOGGLE` (default true: an "enabled" setting is added first).

## Settings

`Setting::toggle(key, label, default)`, `slider(key, label, default, min, max, step)`,
`choice(key, label, &options, default)`, `key(key, label, virtual_key_code)`; chain `.section("..")` and `.tooltip("..")`.
They show up under Options > Mods and persist in `JibbersMods\<id>.settings.json`. Read them anywhere with
`settings::get_bool / get_f64 / get_string / get_key / enabled`, or `plugin::store()`.

## Hooks and threads

`host::pre_hook_method(image, namespace, class, method, argc, callback)` runs `callback(this, arg0)` before every call of
a game method, on the main thread. Limits: void instance methods only, at most two parameters, none a float, 12 slots
overall, 3 plugins per method. A failed lookup returns `Err`: log it and carry on without the feature.

`host::pump::run(|| { ... })` runs a closure on Unity's main thread and returns its result (errors if the game is
minimised or loading). All Unity/IL2CPP calls belong in a hook callback or in `pump::run`.

Other plugins: `host::list_plugins()`, `host::call_plugin(id, &json)`, `host::call_plugin_by_file("name", &json)`; not from
`init` or `unload`. The SDK runtime is reached with `runtime::call("sdk.info", json!({}))` (see RUNTIME.md).

## IL2CPP and Unity

`il2cpp` (raw lookups) and `unity` (class/method calls, field access, GC handles, object search, arrays) are thin and
`unsafe`, main thread only. Classes are found by assembly image, namespace and name (`unity::class("Assembly-CSharp.dll", "", "MjScene")`),
members by name. Game code is obfuscated: for game classes, use the stable names in `symbols` instead
(`symbols::hook("camera.follow.late_update", cb)`, `symbols::get_obj(cam, "camera.follow.target")`). They are found by
shape and survive game updates. See [SYMBOLS.md](SYMBOLS.md) and [SYMBOLS-REFERENCE.md](SYMBOLS-REFERENCE.md).

## Logging

`log::info("text")` appends to `jibbers_hotload.log` beside `Jibbers.exe`. Log failures, not every frame.

## Testing

Put logic that does not need the game in plain functions and cover it with `cargo test`. Anything that touches IL2CPP can
only be checked in the game. Check the log after each change.

## Game updates

Hooks target obfuscated game code, so a game update can break them. The loader therefore refuses to hook on a build that
has not been validated. See GAME-BUILDS.md.
