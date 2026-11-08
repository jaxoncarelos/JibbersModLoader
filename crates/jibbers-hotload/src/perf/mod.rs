//! Built-in performance features, on by default for everyone with the loader. Both leave the game's
//! physics exactly as it is and only skip work MuJoCo would redo for nothing.
//!
//! - Fast map loading (`fast_parse`, `cache`): from **Fast Map Load** by xhail39 (GPL-3.0). Skips two
//!   redundant per-element steps in MuJoCo's XML parsing and remembers each big map's compiled
//!   physics, so large maps load in seconds instead of minutes.
//! - Static collider skipping (`kin`): MuJoCo's `mj_kinematics` recomputes the world pose of every
//!   collider each physics step, including the map's static colliders whose poses never change. On a
//!   map with ~100k colliders that was most of the step (1.9 -> 0.6 ms on a large Shredders map) and
//!   made the game fall behind, so it caught up in bursts that felt like 15 fps.
//!
//! The section shows in Options > Mods as "Performance". Installing the standalone
//! `fast_map_load.dll` / `physics_boost.dll` turns the matching built-in part off, so MuJoCo is never
//! patched twice.

mod cache;
mod fast_parse;
mod kin;
mod pcore;
mod scene;

use crate::settings::{Setting, Store};
use serde_json::{json, Value};
use std::ffi::c_void;
use std::sync::OnceLock;

const ID: &str = "performance";
const NAME: &str = "Performance";
static STORE: OnceLock<Store> = OnceLock::new();
static INIT: OnceLock<()> = OnceLock::new();

pub(crate) fn say(msg: impl AsRef<str>) {
    crate::log::info(format!("performance: {}", msg.as_ref()));
}

pub fn store() -> Option<&'static Store> {
    STORE.get()
}

fn settings() -> Vec<Setting> {
    let s = "Performance";
    vec![
        Setting::toggle("fast_parse", "Fast map loading", true).section(s)
            .tooltip("Skips two redundant steps when big maps build their physics (from Fast Map Load by xhail39). Restart the game after changing."),
        Setting::toggle("cache", "Remember maps (faster second load)", true).section(s)
            .tooltip("Saves each big map's built physics in %LOCALAPPDATA%/Jibbers/FastMapLoad. Restart the game after changing."),
        Setting::slider("cache_gb", "Map cache size limit (GB)", 4.0, 1.0, 20.0, 1.0).section(s)
            .tooltip("Maps you haven't loaded in a while are removed first."),
        Setting::toggle("static_kinematics", "Skip static colliders", true).section(s)
            .tooltip("Computes the positions of colliders that never move once instead of every physics step. Same physics, much less work on big maps. Restart the game after changing."),
    ]
}

fn on(key: &str) -> bool {
    store().and_then(|s| s.get(key)).and_then(|v| v.as_bool()).unwrap_or(true)
}

/// Installs the features (once) and registers the settings section with the loader. Called by
/// `core::attach` before the plugins load, so a standalone copy in JibbersMods is seen first.
pub fn init() {
    INIT.get_or_init(|| {
        let path = crate::game_dir().join("JibbersMods").join(format!("{ID}.settings.json"));
        match Store::new(settings(), Some(NAME), Some(path)) {
            Ok(store) => {
                let _ = STORE.set(store);
            }
            Err(e) => return say(format!("settings: {e}")),
        }
        unsafe { register() };
        if !on("enabled") {
            return say("off in Options > Mods");
        }
        let mods = crate::game_dir().join("JibbersMods");
        let standalone_fast = mods.join("fast_map_load.dll").exists();
        let standalone_kin = mods.join("physics_boost.dll").exists();
        let pcore = match pcore::load() {
            Ok(m) => m,
            Err(e) => return say(format!("can't find the physics engine: {e}")),
        };
        if standalone_fast {
            say("fast_map_load.dll is installed; built-in fast map loading left to it");
        } else {
            if on("fast_parse") {
                match fast_parse::enable(pcore) {
                    Ok(()) => say("fast map loading on"),
                    Err(e) => say(format!("fast map loading not available: {e}")),
                }
            }
            match scene::find_tree_lists(pcore) {
                Ok(n) => say(format!("faster scene parsing on ({n} tree-list call sites)")),
                Err(e) => say(format!("faster scene parsing not available: {e}")),
            }
            if let Err(e) = scene::enable_mesh_cache(pcore, 3usize << 30) {
                say(format!("mesh cache not available: {e}"));
            }
            let gb = store().and_then(|s| s.get("cache_gb")).and_then(|v| v.as_f64()).unwrap_or(4.0).max(1.0);
            let folder = on("cache").then(cache::folder).flatten();
            if let Err(e) = cache::enable(pcore, folder, gb) {
                say(format!("map cache not available: {e}"));
            }
        }
        if standalone_kin {
            say("physics_boost.dll is installed; built-in collider skipping left to it");
        } else if on("static_kinematics") {
            match unsafe { kin::install(pcore) } {
                Ok(()) => say("static collider skipping on"),
                Err(e) => say(format!("static collider skipping not available: {e}")),
            }
        }
    });
}

// --- the Mods tab section: the same plugin.* protocol SDK plugins speak -------------------------

unsafe fn register() {
    let name = "jibbers_loader.performance";
    crate::core::jh_register_plugin(name.as_ptr(), name.len(), std::ptr::null_mut(), handle, free, None);
}

fn reply(req: &Value) -> Result<Value, String> {
    let store = store().ok_or("not initialised")?;
    match req.get("cmd").and_then(Value::as_str).unwrap_or("") {
        "plugin.info" => Ok(json!({
            "ok": true, "id": ID, "name": NAME, "version": env!("CARGO_PKG_VERSION"), "author": "Jibbers Mod Loader (fast loading by xhail39)",
            "description": "Big maps load faster and run smoother; the physics are unchanged.",
            "sdk": env!("CARGO_PKG_VERSION"), "show_in_menu": true,
            "enabled": store.get("enabled").and_then(|v| v.as_bool()).unwrap_or(true),
            "settings": store.schema(),
        })),
        "plugin.get" => Ok(json!({"ok": true, "values": store.values()})),
        "plugin.set" => {
            let key = req.get("key").and_then(Value::as_str).ok_or("plugin.set needs a key")?;
            store.set(key, req.get("value").ok_or("plugin.set needs a value")?)?;
            Ok(json!({"ok": true, "values": store.values()}))
        }
        "plugin.enable" => {
            let on = req.get("on").and_then(Value::as_bool).ok_or("plugin.enable needs \"on\"")?;
            store.set("enabled", &Value::Bool(on))?;
            Ok(json!({"ok": true, "enabled": on}))
        }
        other => Err(format!("{other:?} is not a command of {ID}")),
    }
}

unsafe extern "C" fn handle(_ctx: *mut c_void, req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize) {
    let bytes = if req.is_null() { &[][..] } else { std::slice::from_raw_parts(req, len) };
    let v = match serde_json::from_slice::<Value>(bytes) {
        Ok(r) => std::panic::catch_unwind(|| reply(&r)).unwrap_or_else(|_| Err("internal error".into())).unwrap_or_else(|e| json!({"ok": false, "error": e})),
        Err(e) => json!({"ok": false, "error": format!("bad request: {e}")}),
    };
    let mut s = v.to_string().into_bytes().into_boxed_slice();
    *out_len = s.len();
    *out = s.as_mut_ptr();
    std::mem::forget(s);
}

unsafe extern "C" fn free(_ctx: *mut c_void, p: *mut u8, len: usize) {
    if !p.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(p, len)));
    }
}
