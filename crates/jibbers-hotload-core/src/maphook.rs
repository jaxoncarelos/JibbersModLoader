//! Remembers which map the game has loaded, for the atmosphere and the `status` probe.
//!
//! The game raises a `MapLoadEvent { MapData map; Terrain[] terrains }` to an instance method on
//! `EnvironmentManager` (or `TerrainPainter`) when a map finishes loading. The method is found by its
//! parameter type, not its obfuscated name, and hooked with the loader's `pre_hook`.

use crate::il2cpp::{self, api, type_name, Object};
use crate::unity;
use crate::{game, log};
use std::ffi::c_void;
use std::ptr::null_mut;

const ACS: &str = "Assembly-CSharp.dll";

/// `MapLoadEvent { MapData map; Terrain[] terrains }` handler: remembers which map is loaded.
unsafe extern "C" fn on_map_load(_this: *mut c_void, ev: *mut c_void) {
    if ev.is_null() {
        return;
    }
    let map = *(ev as *const *mut Object);
    if map.is_null() {
        return;
    }
    let id = unity::field_obj(map, "id").map(|s| il2cpp::read_string(s)).unwrap_or_default();
    let name = unity::field_obj(map, "name").map(|s| il2cpp::read_string(s)).unwrap_or_default();
    log::info(format!("game loaded map {name:?} ({id})"));
    game::set_loaded_map(id, name);
}

/// An instance method on `class` taking exactly one `MapLoadEvent`.
unsafe fn map_load_handler(class: &str) -> Option<*const il2cpp::Method> {
    let a = api();
    let k = unity::class(ACS, "", class).ok()?;
    let mut it: *mut c_void = null_mut();
    loop {
        let m = (a.class_get_methods)(k, &mut it);
        if m.is_null() {
            return None;
        }
        let mut iflags = 0;
        if (a.method_get_flags)(m, &mut iflags) & 0x10 == 0
            && (a.method_get_param_count)(m) == 1
            && type_name((a.method_get_param)(m, 0)) == "MapLoadEvent"
        {
            return Some(m);
        }
    }
}

/// Registers the map-load hook; called once when the core loads.
pub unsafe fn install_hooks(host: &crate::abi::Host) {
    match map_load_handler("EnvironmentManager").or_else(|| map_load_handler("TerrainPainter")) {
        Some(m) => {
            let rc = (host.pre_hook)(m as *const c_void, on_map_load);
            log::info(format!("hook MapLoadEvent: {}", if rc == 0 { "ok".to_string() } else { format!("failed ({rc})") }));
        }
        None => log::info("no MapLoadEvent handler found; the loaded map isn't known"),
    }
}
