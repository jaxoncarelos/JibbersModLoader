//! Writes a readable listing of classes, fields and methods to `jibbers_hotload_api.txt`.
//! Used to find the game's map-loading path, which hides behind obfuscated names.

use crate::il2cpp::{self, api, cstr, type_name, Class};
use std::ffi::c_void;
use std::fmt::Write;

/// Types that make an obfuscated Assembly-CSharp class worth listing.
const INTERESTING: &[&str] = &[
    "MapData", "MapImporter", "MapExporter", "MapTerrainChunk", "MapObject", "MapSpawnPoint",
    "UnityEngine.Terrain", "UnityEngine.TerrainData", "MjScene", "MjHeightFieldShape", "jbrmap",
];

pub unsafe fn class_text(k: *const Class) -> String {
    let a = api();
    let mut out = String::new();
    let parent = (a.class_get_parent)(k);
    let pname = if parent.is_null() { String::new() } else { cstr((a.class_get_name)(parent)) };
    let _ = writeln!(out, "class {}.{} : {}", cstr((a.class_get_namespace)(k)), cstr((a.class_get_name)(k)), pname);
    let mut it: *mut c_void = std::ptr::null_mut();
    loop {
        let f = (a.class_get_fields)(k, &mut it);
        if f.is_null() {
            break;
        }
        let stat = (a.field_get_flags)(f) & 0x10 != 0;
        let _ = writeln!(
            out,
            "  field {}{} : {} @0x{:x}",
            if stat { "static " } else { "" },
            cstr((a.field_get_name)(f)),
            type_name((a.field_get_type)(f)),
            (a.field_get_offset)(f)
        );
    }
    let mut it: *mut c_void = std::ptr::null_mut();
    loop {
        let m = (a.class_get_methods)(k, &mut it);
        if m.is_null() {
            break;
        }
        let mut iflags = 0u32;
        let flags = (a.method_get_flags)(m, &mut iflags);
        let params: Vec<String> = (0..(a.method_get_param_count)(m))
            .map(|i| format!("{} {}", type_name((a.method_get_param)(m, i)), cstr((a.method_get_param_name)(m, i))))
            .collect();
        let _ = writeln!(
            out,
            "  method {}{}({}) -> {}",
            if flags & 0x10 != 0 { "static " } else { "" },
            cstr((a.method_get_name)(m)),
            params.join(", "),
            type_name((a.method_get_return_type)(m))
        );
    }
    out
}

unsafe fn mentions_interesting(text: &str) -> bool {
    INTERESTING.iter().any(|w| text.contains(w))
}

pub unsafe fn run(path: &std::path::Path) {
    let a = api();
    let mut out = String::new();
    for (img, name) in il2cpp::assemblies() {
        let full = name == "fun.jibbers.map-tools.dll" || name == "Mujoco.Runtime.dll";
        let scan = name == "Assembly-CSharp.dll";
        let _ = writeln!(out, "### {name} ({} classes)", (a.image_get_class_count)(img));
        if !full && !scan {
            continue;
        }
        for i in 0..(a.image_get_class_count)(img) {
            let k = (a.image_get_class)(img, i);
            let text = class_text(k);
            if full || mentions_interesting(&text) {
                out.push_str(&text);
            }
        }
    }
    let _ = std::fs::write(path, out);
}
