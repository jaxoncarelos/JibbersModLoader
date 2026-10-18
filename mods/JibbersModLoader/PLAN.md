# Jibbers Mod Loader — plan

## Release packaging update (2026-10-18)

The release loader now owns the shared Mods tab by reusing the existing menu implementation,
bootstrapped on Unity's main thread only after plugin attachment releases the loader lock.
Retries are finite; a developer core, if present, remains menu owner (player docs recommend
removing it to avoid duplicated Atmosphere). No separate menu DLL is needed. The default
developer loader remains unchanged. Plugin ABI remains v4.

The canonical packaging pipeline is tools/package_mods.ps1: independently built release loader,
atmosphere-only plugin, reshred-only plugin, workspace bundle, docs and texture credit. It emits
loader-only, two plugin-only, two standalone and combined archives with hash manifests and
offline PE/ABI/content verification. No game-install files or personal settings are used.
The SDK/template sections below remain future work; they are not shipped as completed features.

A general-purpose mod loader for Jibbers (IL2CPP, so no C#/MelonLoader mods): one `version.dll`
loads native plugin DLLs from `<game>\JibbersMods\`, and an in-game **Mods** menu styled like the
game's own settings menu lists every plugin with an on/off switch and its settings as sliders,
toggles and dropdowns. Good enough for our own plugins (Atmosphere first) and a few other authors;
expandable later.

## Pieces

1. **Loader** (`crates/jibbers-hotload`, ships as `version.dll`) — done in P0: attaches to the
   game, runs main-thread jobs, owns method hooks (per-plugin ownership), loads every plugin in
   `JibbersMods\`. Release build has no TCP server; the developer build keeps it.
2. **Plugin protocol** (extends abi.rs; JSON over the existing `jh_core_handle`, so no new exports
   are required). Every plugin answers:
   - `{"cmd":"plugin.info"}` → `{id, name, version, author, description, settings: [schema]}`
   - `{"cmd":"plugin.get"}` → current values `{key: value}`
   - `{"cmd":"plugin.set","key":k,"value":v}` → applies + persists (plugin keeps its own json in
     `JibbersMods\<id>.json`)
   - `{"cmd":"plugin.enable","on":bool}`
   Settings schema entries: `{key, label, type: "toggle"|"slider"|"choice"|"key"|"button",
   min, max, step, options[], default, section, tooltip}`.
   New host functions (abi.rs, `Host`): `list_plugins() -> json`, `call_plugin(id, json) -> json`,
   so one plugin (the menu) can talk to the others. ABI version bumps to 4.
3. **SDK** (`crates/jibbers-mod-sdk`): the IL2CPP/Unity helpers we built (il2cpp.rs, unity.rs,
   find-by-signature helpers, logging, main-thread `pump`, `pre_hook`), plus a `declare_plugin!`
   macro that generates the three exports, the `plugin.*` handlers from a settings struct
   (serde + a schema description), json persistence and enable/disable. Atmosphere and the menu
   are rebuilt on it. `mods/template/` = minimal example plugin + README for other authors.
4. **Mods menu plugin** (`jibbers_modmenu.dll`, ships with the loader): adds a **Mods** entry to
   the game's menu and builds pages with the game's own form UI (`FormMenu`: the class
   `EnvironmentMenu` uses for its sliders — to be reverse-engineered in P2), one page per plugin,
   controls generated from each plugin's schema. Fallback if FormMenu can't be driven safely: our
   own UGUI overlay from an asset bundle, styled to match (fonts/colours copied from the game's
   menu at runtime). Hotkey to open it from anywhere (default F1, configurable).

## Phases

- **P0** plugin loading in the loader — DONE when the current agent reports.
- **P1** plugin protocol + host functions + SDK crate + `declare_plugin!`; port Atmosphere to it
  (settings schema: on/off, preset, fog, snow, cloud bank, sun shafts, speed streaks, colour
  grading, hotkeys). No game needed. Unit-test the macro-generated handlers.
- **P2** reverse-engineer the game's menu: FormMenu / EnvironmentMenu / how the settings screen is
  opened, how a slider/toggle/dropdown row is created and read, fonts/prefabs. Needs the
  developer build in the game (game closed to swap version.dll), offline. Report only.
- **P3** Mods menu plugin on top of P2 (or the overlay fallback), driven entirely by plugin
  schemas. Verify in game: open menu, toggle Atmosphere, move sliders live.
- **P4** template plugin + docs (`mods/JibbersModLoader/README.md`: install, writing a plugin,
  settings schema, hooks, main thread, finding game code safely across updates), release zip
  `dist/JibbersModLoader.zip` (version.dll + JibbersMods/jibbers_modmenu.dll) and
  `dist/JibbersAtmosphere.zip` (plugin + bundle only).

## Rules for every phase
Never close/kill the game or other processes; offline only for live tests; restore anything an
experiment changes; no commits; match the repo's style; each phase ends with a short report.
