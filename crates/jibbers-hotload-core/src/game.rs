//! Operations on the running game. Every `pub fn` here runs on the main thread (via `pump::run`).

use crate::il2cpp::{self, api, Object};
use crate::unity::{self, Vec3, R};
use serde_json::{json, Value};
use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::Mutex;

const ACS: &str = "Assembly-CSharp.dll";
const MUJOCO: &str = "Mujoco.Runtime.dll";

/// (id, name) of the map the game last loaded, from its MapLoadEvent. None until a map loads
/// while the core is running.
static LOADED_MAP: Mutex<Option<(String, String)>> = Mutex::new(None);

pub fn set_loaded_map(id: String, name: String) {
    *LOADED_MAP.lock().unwrap() = Some((id, name));
}

pub fn loaded_map_id() -> Option<String> {
    LOADED_MAP.lock().unwrap().as_ref().map(|(id, _)| id.clone())
}

pub struct LoadedTerrain {
    pub terrain: *mut Object,
    pub data: *mut Object,
    pub position: Vec3,
    pub size: Vec3,
    pub res: i32,
}

/// Terrains of the loaded map.
pub unsafe fn map_terrains() -> R<Vec<LoadedTerrain>> {
    loaded_terrains()
}

/// Terrains belonging to the loaded map (from MapRuntimeImporter), falling back to all terrains.
unsafe fn loaded_terrains() -> R<Vec<LoadedTerrain>> {
    let mut list = Vec::new();
    if let Ok(k) = unity::class(ACS, "", "MapRuntimeImporter") {
        for imp in unity::find_objects(k)? {
            // `uzv` is the importer's Terrain[] (obfuscated name; the type is what identifies it).
            if let Some(f) = terrain_array_field(k) {
                let mut arr: *mut Object = null_mut();
                (api().field_get_value)(imp, f, &mut arr as *mut _ as _);
                list.extend(unity::array_items(arr));
            }
        }
    }
    if list.is_empty() {
        list = unity::find_objects(unity::class(unity::TERRAIN, "UnityEngine", "Terrain")?)?;
    }
    let mut out = Vec::new();
    for t in list {
        if unity::is_null(t) {
            continue;
        }
        let data = unity::callv(t, "get_terrainData", &mut [])?;
        out.push(LoadedTerrain {
            terrain: t,
            data,
            position: unity::position(t)?,
            size: unity::call_unbox::<Vec3>(data, "get_size")?,
            res: unity::call_unbox::<i32>(data, "get_heightmapResolution")?,
        });
    }
    Ok(out)
}

/// Finds MapRuntimeImporter's `Terrain[]` field without relying on its obfuscated name.
unsafe fn terrain_array_field(k: *mut il2cpp::Class) -> Option<*mut il2cpp::Field> {
    let a = api();
    let mut it: *mut c_void = null_mut();
    loop {
        let f = (a.class_get_fields)(k, &mut it);
        if f.is_null() {
            return None;
        }
        if il2cpp::type_name((a.field_get_type)(f)) == "UnityEngine.Terrain[]" {
            return Some(f);
        }
    }
}

unsafe fn mj_scene() -> Option<*mut Object> {
    let k = unity::class(MUJOCO, "Mujoco", "MjScene").ok()?;
    let exists = unity::call(k, null_mut(), "get_InstanceExists", &mut []).ok()?;
    if exists.is_null() || !il2cpp::unbox::<bool>(exists) {
        return None;
    }
    let s = unity::call(k, null_mut(), "get_Instance", &mut []).ok()?;
    (!s.is_null()).then_some(s)
}

/// MjGeoms whose shape is a height field, paired with that height field and its terrain.
unsafe fn hfield_geoms() -> R<Vec<(*mut Object, *mut Object, *mut Object)>> {
    let Ok(k) = unity::class(MUJOCO, "Mujoco", "MjGeom") else { return Ok(vec![]) };
    let mut out = Vec::new();
    for g in unity::find_objects(k)? {
        let hf = unity::field_obj(g, "HField")?;
        if hf.is_null() {
            continue;
        }
        let shape: i32 = unity::field_val(g, "ShapeType")?;
        let terrain = unity::field_obj(hf, "Terrain")?;
        if terrain.is_null() || shape != hfield_shape_type() {
            continue;
        }
        out.push((g, hf, terrain));
    }
    Ok(out)
}

/// `MjShapeComponent.ShapeTypes.HField` — resolved from the enum's literal field.
unsafe fn hfield_shape_type() -> i32 {
    let Ok(k) = unity::class(MUJOCO, "Mujoco", "MjShapeComponent") else { return 7 };
    let mut it: *mut c_void = null_mut();
    loop {
        let nested = (api().class_get_nested_types)(k, &mut it);
        if nested.is_null() {
            return 7;
        }
        if il2cpp::cstr((api().class_get_name)(nested)) == "ShapeTypes" {
            if let Some(f) = il2cpp::field(nested, "HField") {
                let mut v = 0i32;
                (api().field_static_get_value)(f, &mut v as *mut i32 as _);
                return v;
            }
            return 7;
        }
    }
}

pub fn status() -> R<Value> {
    unsafe {
        let scene_k = unity::class(unity::CORE, "UnityEngine.SceneManagement", "SceneManager")?;
        let scene = unity::call(scene_k, null_mut(), "GetActiveScene", &mut [])?;
        let scene_name = if scene.is_null() {
            String::new()
        } else {
            // Scene is a struct; GetActiveScene returns it boxed.
            let sk = unity::class(unity::CORE, "UnityEngine.SceneManagement", "Scene")?;
            let p = (api().object_unbox)(scene);
            il2cpp::read_string(unity::call(sk, p, "get_name", &mut [])?)
        };
        let terrains = loaded_terrains()?;
        let geoms = hfield_geoms()?;
        // Camera.main is unset in this game; report every enabled camera instead.
        let cam_k = unity::class(unity::CORE, "UnityEngine", "Camera")?;
        let camera: Vec<Value> = unity::find_objects(cam_k)?
            .into_iter()
            .filter(|c| unity::call_unbox::<bool>(*c, "get_isActiveAndEnabled").unwrap_or(false))
            .map(|c| json!({"name": unity::name(c), "position": unity::position(c).ok()}))
            .collect();
        let loaded = LOADED_MAP.lock().unwrap().clone();
        Ok(json!({
            "loadedMap": loaded.map(|(id, name)| json!({"id": id, "name": name})),
            "camera": camera,
            "scene": scene_name,
            "importers": unity::class(ACS, "", "MapRuntimeImporter").ok().map(|k| unity::find_objects(k).map(|v| v.len()).unwrap_or(0)),
            "mjScene": mj_scene().is_some(),
            "hfieldGeoms": geoms.len(),
            "terrains": terrains.iter().map(|t| json!({
                "name": unity::name(t.terrain),
                "position": t.position,
                "size": t.size,
                "res": t.res,
                "data": format!("{:p}", t.data),
            })).collect::<Vec<_>>(),
        }))
    }
}

/// Height of the loaded terrain under world (x, z), as the game sees it.
pub fn sample(x: f32, z: f32) -> R<Value> {
    unsafe {
        let mut out = Vec::new();
        for t in loaded_terrains()? {
            let mut p = Vec3 { x, y: 0.0, z };
            let h = unity::callv(t.terrain, "SampleHeight", &mut [&mut p as *mut Vec3 as _])?;
            out.push(json!({"terrain": unity::name(t.terrain), "height": il2cpp::unbox::<f32>(h) + t.position.y}));
        }
        Ok(json!({"ok": true, "samples": out}))
    }
}
