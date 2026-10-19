# Game symbols

The game's own classes, fields and methods are obfuscated: renamed to two or three lowercase letters (`uzv`, `bjc`,
`<sdj>k__BackingField`), and the names change with every game build. Some classes keep their real names
(`MapRuntimeImporter`, `FollowCam`) while their members are obfuscated.

The SDK names these things for you. A **symbol** is a stable id for one game class (`map.importer`) and the members
mods use (`map.importer.terrains`). The SDK finds them on the running build by **shape**: field types, method
signatures, the parent class, and the engine and library types they use. Those survive a rebuild; obfuscated names don't.

The reference of every symbol is [SYMBOLS-REFERENCE.md](SYMBOLS-REFERENCE.md).

## Using symbols

```rust
use jibbers_mod_sdk::{log, symbols};

fn init() {
    // Hooks check the loader's limits first (void or bool, at most two non-float parameters).
    // Install them here, in init: the loader ties a hook to the plugin being initialised.
    if let Err(e) = unsafe { symbols::hook("camera.follow.late_update", on_camera) } {
        log::info(format!("my_mod: {e}")); // the game updated and the symbol no longer resolves: run without it
    }
}

unsafe extern "C" fn on_camera(cam: *mut std::ffi::c_void, _: *mut std::ffi::c_void) {
    let _ = (|| -> Result<(), String> {
        let target = symbols::get_obj(cam, "camera.follow.target")?;   // object field
        let photo = symbols::get::<bool>(cam, "camera.follow.photo_mode")?; // value field
        // ...
        Ok(())
    })();
}
```

| Function | Does |
| --- | --- |
| `symbols::class(id)` | the class (`*mut Class`) |
| `symbols::info(id)` | its metadata: real name, fields with types and offsets, methods |
| `symbols::field(path)` / `method(path)` | the IL2CPP field / method |
| `symbols::hook(path, cb)` | pre-hook a method, as `host::pre_hook_method` |
| `symbols::get_obj` / `get::<T>` / `set` / `set_obj` | read or write a field of an object |
| `symbols::get_static_obj(path)` | read a static object field (initialises the class) |
| `symbols::call(this, path, args)` | call a method (`this` null for static methods); checks the argument count |
| `symbols::is(obj, id)` | whether an object is an instance of the symbol's class |
| `symbols::real_name(path)` | the obfuscated name on this build, for logs |
| `symbols::report()` | every symbol's status on this build, as JSON |
| `symbols::register(json)` | add your own symbols |

Paths are `<symbol id>.<member>`. The first lookup reads the game's metadata and resolves every symbol (a few tens of
milliseconds). After that, lookups are map reads.

**Threads.** Lookups (`class`, `field`, `method`, `hook`, `info`) read metadata only. Like `host::pre_hook_method`,
they may run in `Plugin::init`. Anything that touches an object (`get*`, `set*`, `call`, `is`) runs on the main thread:
inside a hook callback or `host::pump::run`.

**Failures.** If a symbol does not resolve on the running build, every lookup of it returns `Err` with the reason:

- **not found:** no class has that shape any more;
- **ambiguous:** several classes match, and the error lists them;
- **members:** the class was found but a member was not.

The log gets one `symbols: <id> unresolved: ...` line per failed symbol. Treat a failure like a missing feature: log
it and carry on. Never fall back to a hard-coded obfuscated name.

## Your own symbols

Register a catalogue in the same format as the built-in `symbols.json`, before your first lookup. Prefix the ids with
your plugin id:

```rust
symbols::register(include_str!("../my_symbols.json"))?;
let k = unsafe { symbols::class("my_mod.trick_tracker") }?;
```

If a symbol would be useful to other mods, propose it for the built-in catalogue (see "Adding a symbol").

## Rules

Each symbol has a `match` rule for the class and one rule per member:

```json
{
  "id": "player.spawner",
  "summary": "PlayerSpawner: spawns the local rider.",
  "tags": ["shape"],
  "match": { "name": "PlayerSpawner" },
  "members": {
    "rider": { "field": { "type": "@player.ragdoll", "static": false, "inherited": true } }
  }
}
```

Class conditions (all optional, all must hold):

| Key | Means |
| --- | --- |
| `image` | assembly, default `Assembly-CSharp.dll` |
| `namespace`, `name` | exact; `name` only for classes that keep their real name |
| `obfuscated` | the class name must (true) or must not (false) look obfuscated |
| `parent`, `declaring` | full name of the parent class / of the enclosing class for a nested class |
| `valuetype` | struct or enum |
| `interfaces` | interfaces it must implement |
| `fields`, `methods` | each rule must match at least one member (same keys as member rules) |
| `instance_fields` | `[min, max]` declared instance fields |

Member rules: `field` takes `name`, `obfuscated`, `type`, `static`. `method` takes `name`, `obfuscated`, `params`
(exact parameter types, and so the count), `argc` (count only), `return`, `static`. Both also take:

- `nth`: the n-th match, 0-based, in declaration order. Use it for "the first `int` field".
- `inherited`: also search parent classes, class first.

A member must match exactly one member unless the rule has `nth`.

Type names are IL2CPP's: `System.Int32`, `UnityEngine.Terrain[]`,
`System.Collections.Generic.List<UnityEngine.Vector3>`. Classes in the global namespace have no prefix (`Button[]`).
Inside a type, `@id` stands for the class that symbol resolves to (`@player.ragdoll`, `@chat.command[]`). That is how one
obfuscated class refers to another. `*` alone matches any type.

**Tags.** `shape` means every lookup goes by shape. `pinned` means at least one member still uses an obfuscated name,
because no shape for it has been confirmed yet. Pinned members break on the next game update. Replacing them is the
first job after a new dump (see below).

## Reverse-engineering workflow

All of it runs on your own machine against your own game install. Nothing is sent anywhere.

1. **Dump the game.** With the SDK runtime installed, start the game and, once it is on the main menu, send
   `{"cmd": "sdk.symbols.dump"}` to the runtime. From a plugin: `runtime::call("sdk.symbols.dump", json!({}))`. With the
   developer loader, which forwards requests on `127.0.0.1:47820` to every plugin: `python tools\hotload.py raw '{"cmd":"sdk.symbols.dump"}'`.
   It writes `jibbers_symbols_dump.json` beside `Jibbers.exe`: every class of `Assembly-CSharp.dll` with its fields
   (types, offsets) and methods (signatures). Pass `"images": ["Assembly-CSharp.dll", "fun.jibbers.map-tools.dll"]` for
   more assemblies. The dump reads metadata only, so it also works on a build where the loader has disabled mods.
2. **See what still resolves:** `cargo run -p jibbers-sdk-symbols -- resolve jibbers_symbols_dump.json`.
3. **Explore** to write or fix a rule:
   - `sdk-symbols find <dump> "UnityEngine.Terrain[]"` lists classes with that type in a field or signature.
   - `sdk-symbols show <dump> MapRuntimeImporter` prints one class.
   - In the game, the developer probes (`inspect`, `objects`, `classinfo`, `fieldsoftype`) show live values. Use them
     to confirm a field is what you think before you name it.
4. **Write the rule** from facts that won't change: the types around the member, not its name. Prefer:
   - a unique parameter list, or a return type that is an engine, library or MapTools type;
   - a field whose type is unique within the class;
   - `@` references to other symbols.

   If nothing is unique, use `nth`, and say why in `notes`.
5. **Check it:** `sdk-symbols lint`, then `resolve` against the dump. An ambiguous result lists the candidates; add a
   condition that tells them apart.
6. **After a game update,** dump the new build and run `sdk-symbols diff old.json new.json`:
   - `renamed` is the normal case: the shape still finds the class under its new name.
   - `broke` means a rule needs work.

   Fix the rules before validating the build (GAME-BUILDS.md).

## Adding a symbol to the catalogue

Edit `crates/jibbers-mod-sdk/symbols.json`, then:

```powershell
cargo test -p jibbers-mod-sdk -p jibbers-sdk-symbols   # lint + the known-facts fixture
cargo run -p jibbers-sdk-symbols -- resolve <dump>      # against the real game
cargo run -p jibbers-sdk-symbols -- docs --out docs\SYMBOLS-REFERENCE.md
```

If the symbol relies on a fact the fixture doesn't have yet, add that fact to the `known_facts` test in
`crates/jibbers-sdk-symbols/src/main.rs`. Only add facts you have seen in a dump.

### Out of scope

The catalogue covers what single-player and cosmetic mods need: maps, the rider, camera, replays, physics and UI. It
leaves out three areas on purpose. Rules for them don't belong in the catalogue, and the same applies to private
catalogues you share.

- **Networking.** The FishNet transport and the game's network messages.
- **Server-side checks.** Authentication, permissions, and moderation or anti-cheat checks.
- **Other players' input and state.**

Mapping these mostly helps people cheat in multiplayer, and a mod that changes them can affect other players.
