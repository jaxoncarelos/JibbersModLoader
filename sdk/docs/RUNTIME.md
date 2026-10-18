# SDK runtime commands

The runtime plugin `JibbersMods\jibbers_sdk.dll` answers JSON commands from other plugins. New commands arrive by
auto-update, so check for one before using it: `jibbers_mod_sdk::runtime::has_feature("sdk.build")`.

```rust
let info = jibbers_mod_sdk::runtime::call("sdk.info", jibbers_mod_sdk::json!({}))?;
```

Never call from `Plugin::init` or `Plugin::unload`. Without the runtime, `runtime::call` returns an error.

| Command | Arguments | Reply |
| --- | --- | --- |
| `sdk.info` | | `sdkVersion`, `installedVersion`, `abi`, `features`, `update` (status), `autoUpdate`, `gameBuild` (as `sdk.build`) |
| `sdk.build` | | `verdict` (`validated`, `unvalidated`, `unidentified`, `forced`), `allowsMods`, `loadedWithMods`, `message`, `fingerprint`, `notes` |
| `sdk.plugins` | | `plugins`: every other loaded plugin with its `plugin.info` |
| `sdk.paths` | | `gameDir`, `modsDir`, `sdkDir`, `log`, `settingsPattern` |
| `sdk.log` | `msg`, optional `from` | writes a line to `jibbers_hotload.log` |
| `sdk.update.status` | | `update` state and `running` |
| `sdk.update.check` | | starts an update check now: `started`, `update` |

`sdk.build` reports what the running game is. When the loader has switched the mods off, only the runtime is loaded,
so this is how a tool can show the reason.
