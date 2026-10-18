//! Developer probes: inspect static fields, list live objects and call static methods by name.
//! Used to find the game's (obfuscated) map registry, and to re-find things after game updates.

use crate::il2cpp::{self, api, cstr, type_name, Class, Object};
use crate::unity::{self, R};
use serde_json::{json, Map, Value};
use std::ffi::c_void;
use std::ptr::null_mut;

const MAX_ITEMS: usize = 64;

// IL2CPP_TYPE_PTR, BYREF, FNPTR, I and U are addresses, never managed object references.
fn native_pointer_type(kind: i32) -> bool {
    matches!(kind, 0x0f | 0x10 | 0x18 | 0x19 | 0x1b)
}

/// JSON description of a value of IL2CPP type `t` stored at `p` (a pointer to the slot).
unsafe fn describe_slot(t: *const il2cpp::Type, p: *const u8, depth: u32) -> Value {
    if t.is_null() || p.is_null() {
        return Value::Null;
    }
    if native_pointer_type((api().type_get_type)(t)) {
        return json!({"$pointer": format!("0x{:x}", std::ptr::read_unaligned(p as *const usize))});
    }
    match type_name(t).as_str() {
        "System.Boolean" => json!(*(p as *const bool)),
        "System.Int32" => json!(*(p as *const i32)),
        "System.Int64" => json!(*(p as *const i64)),
        "System.Single" => json!(*(p as *const f32)),
        "System.Double" => json!(*(p as *const f64)),
        _ => {
            let k = (api().class_from_type)(t);
            if k.is_null() {
                return Value::Null;
            }
            if (api().class_is_valuetype)(k) {
                describe_struct(k, p, depth)
            } else {
                describe(*(p as *const *mut Object), depth)
            }
        }
    }
}

unsafe fn describe_struct(k: *mut Class, p: *const u8, depth: u32) -> Value {
    let mut m = Map::new();
    m.insert("$type".into(), json!(cstr((api().class_get_name)(k))));
    if depth == 0 {
        return Value::Object(m);
    }
    let mut it: *mut c_void = null_mut();
    loop {
        let f = (api().class_get_fields)(k, &mut it);
        if f.is_null() {
            break;
        }
        if (api().field_get_flags)(f) & 0x10 != 0 {
            continue;
        }
        // Field offsets of value types include the (absent) object header.
        let Some(off) = (api().field_get_offset)(f).checked_sub(16) else { continue };
        m.insert(cstr((api().field_get_name)(f)), describe_slot((api().field_get_type)(f), p.add(off), depth - 1));
    }
    Value::Object(m)
}

pub unsafe fn describe(o: *mut Object, depth: u32) -> Value {
    if o.is_null() {
        return Value::Null;
    }
    let a = api();
    let k = (a.object_get_class)(o);
    if (a.class_is_valuetype)(k) {
        return describe_slot((a.class_get_type)(k), (a.object_unbox)(o) as *const u8, depth);
    }
    let tname = type_name((a.class_get_type)(k));
    if tname == "System.String" {
        return json!(il2cpp::read_string(o));
    }
    if tname.ends_with("[]") {
        let n = (a.array_length)(o) as usize;
        let mut items = Vec::new();
        if depth > 0 {
            let ek = (a.class_get_element_class)(k);
            let size = if (a.class_is_valuetype)(ek) { (a.class_value_size)(ek, null_mut()) as usize } else { 8 };
            let data = il2cpp::array_data::<u8>(o);
            for i in 0..n.min(MAX_ITEMS) {
                items.push(describe_slot((a.class_get_type)(ek), data.add(i * size), depth - 1));
            }
        }
        return json!({"$type": tname, "length": n, "items": items});
    }
    let mut m = Map::new();
    m.insert("$type".into(), json!(tname));
    if depth == 0 {
        return Value::Object(m);
    }
    let mut cur = k;
    while !cur.is_null() {
        // Stop at engine base classes; their fields are native handles.
        let cname = cstr((a.class_get_name)(cur));
        if matches!(cname.as_str(), "Object" | "MonoBehaviour" | "Behaviour" | "Component") {
            break;
        }
        let mut it: *mut c_void = null_mut();
        loop {
            let f = (a.class_get_fields)(cur, &mut it);
            if f.is_null() {
                break;
            }
            if (a.field_get_flags)(f) & 0x10 != 0 {
                continue;
            }
            let off = (a.field_get_offset)(f);
            m.insert(cstr((a.field_get_name)(f)), describe_slot((a.field_get_type)(f), (o as *const u8).add(off), depth - 1));
        }
        cur = (a.class_get_parent)(cur);
    }
    Value::Object(m)
}

fn str_arg<'a>(req: &'a Value, k: &str) -> R<&'a str> {
    req.get(k).and_then(Value::as_str).ok_or_else(|| format!("missing {k}"))
}

unsafe fn req_class(req: &Value) -> R<*mut Class> {
    unity::class(str_arg(req, "image")?, req.get("ns").and_then(Value::as_str).unwrap_or(""), str_arg(req, "class")?)
}

fn depth(req: &Value, def: u32) -> u32 {
    req.get("depth").and_then(Value::as_u64).map(|d| d as u32).unwrap_or(def)
}

/// `{"image","class","field","depth"}`: description of a static field.
pub fn inspect(req: &Value) -> R<Value> {
    unsafe {
        let k = req_class(req)?;
        (api().runtime_class_init)(k);
        let f = il2cpp::field(k, str_arg(req, "field")?).ok_or("no such field")?;
        let t = (api().field_get_type)(f);
        let fk = (api().class_from_type)(t);
        let size = if !fk.is_null() && (api().class_is_valuetype)(fk) {
            (api().class_value_size)(fk, null_mut()).max(0) as usize
        } else {
            std::mem::size_of::<usize>()
        };
        // Reflection writes the entire value: a fixed 64-byte buffer corrupts the stack for
        // larger structs (Rewired and MuJoCo contain native interop structures).
        let mut buf = vec![0u8; size.max(std::mem::size_of::<usize>())];
        (api().field_static_get_value)(f, buf.as_mut_ptr() as _);
        Ok(json!({"ok": true, "value": describe_slot(t, buf.as_ptr(), depth(req, 2))}))
    }
}

/// `{"image","class","depth"}`: descriptions of all live instances of a component class.
pub fn objects(req: &Value) -> R<Value> {
    unsafe {
        let k = req_class(req)?;
        let d = depth(req, 1);
        let list: Vec<Value> = unity::find_objects(k)?.into_iter().map(|o| describe(o, d)).collect();
        Ok(json!({"ok": true, "objects": list}))
    }
}

/// `{"image","class","method","args":[strings],"depth"}`: calls a static method.
pub fn call(req: &Value) -> R<Value> {
    unsafe {
        let k = req_class(req)?;
        let args: Vec<String> = req
            .get("args")
            .and_then(Value::as_array)
            .map(|a| a.iter().map(|v| v.as_str().unwrap_or_default().to_string()).collect())
            .unwrap_or_default();
        let mut ptrs: Vec<*mut c_void> = args.iter().map(|s| il2cpp::string(s) as *mut c_void).collect();
        let r = unity::call(k, null_mut(), str_arg(req, "method")?, &mut ptrs)?;
        Ok(json!({"ok": true, "value": describe(r, depth(req, 2))}))
    }
}

/// Where the snow-mask textures are bound for each terrain: material and global
/// shader textures (the terrain property-block API is stripped from this game). `{"cmd":"materials"}`.
pub fn materials() -> R<Value> {
    unsafe {
        let tk = unity::class(unity::TERRAIN, "UnityEngine", "Terrain")?;
        let shader_k = unity::class(unity::CORE, "UnityEngine", "Shader")?;
        let props = ["_SnowMask", "_SnowMask2", "_ThirdMaskTex"];
        let describe_tex = |t: *mut Object| -> Value {
            if unity::is_null(t) {
                return Value::Null;
            }
            json!({
                "name": unity::name(t),
                "ptr": format!("{:p}", t),
                "width": unity::call_unbox::<i32>(t, "get_width").ok(),
                "height": unity::call_unbox::<i32>(t, "get_height").ok(),
            })
        };
        let mut out = Vec::new();
        for t in unity::find_objects(tk)? {
            let mat = unity::callv(t, "get_materialTemplate", &mut [])?;
            let mut row = Map::new();
            row.insert("terrain".into(), json!(unity::name(t)));
            row.insert("material".into(), json!(if unity::is_null(mat) { String::new() } else { unity::name(mat) }));
            for p in props {
                let s = il2cpp::string(p) as *mut c_void;
                let m = if unity::is_null(mat) { null_mut() } else { unity::callv(mat, "GetTexture", &mut [s]).unwrap_or(null_mut()) };
                let g = unity::call(shader_k, null_mut(), "GetGlobalTexture", &mut [s]).unwrap_or(null_mut());
                row.insert(p.into(), json!({"material": describe_tex(m), "global": describe_tex(g)}));
            }
            out.push(Value::Object(row));
        }
        Ok(json!({"ok": true, "terrains": out}))
    }
}

/// `{"cmd":"texpixel","prop":"_SnowMask","x":…,"y":…}`: a pixel of the loaded map terrain's
/// material texture (y counts from the bottom, as in Unity).
pub fn texpixel(req: &Value) -> R<Value> {
    unsafe {
        let tk = unity::class(unity::TERRAIN, "UnityEngine", "Terrain")?;
        let prop = str_arg(req, "prop")?;
        let mut x = req.get("x").and_then(Value::as_i64).unwrap_or(0) as i32;
        let mut y = req.get("y").and_then(Value::as_i64).unwrap_or(0) as i32;
        for t in unity::find_objects(tk)? {
            if unity::name(t) != "Terrain" {
                continue;
            }
            let mat = unity::callv(t, "get_materialTemplate", &mut [])?;
            let tex = unity::callv(mat, "GetTexture", &mut [il2cpp::string(prop) as *mut c_void])?;
            let readable = unity::call_unbox::<bool>(tex, "get_isReadable").ok();
            let format = unity::call_unbox::<i32>(tex, "get_format").ok();
            let mips = unity::call_unbox::<i32>(tex, "get_mipmapCount").ok();
            let px = unity::callv(tex, "GetPixel", &mut [&mut x as *mut i32 as _, &mut y as *mut i32 as _])
                .map(|c| il2cpp::unbox::<[f32; 4]>(c).to_vec());
            return Ok(json!({"ok": true, "readable": readable, "format": format, "mipmaps": mips, "pixel": px.map_err(|e| e)}));
        }
        Err("no map terrain".into())
    }
}

/// `{"cmd":"shaderprops"}`: texture properties of the map terrain's shader, with what's bound.
pub fn shaderprops() -> R<Value> {
    unsafe {
        let tk = unity::class(unity::TERRAIN, "UnityEngine", "Terrain")?;
        for t in unity::find_objects(tk)? {
            if unity::name(t) != "Terrain" {
                continue;
            }
            let mat = unity::callv(t, "get_materialTemplate", &mut [])?;
            let shader = unity::callv(mat, "get_shader", &mut [])?;
            let n = unity::call_unbox::<i32>(shader, "GetPropertyCount")?;
            let mut props = Vec::new();
            for i in 0..n {
                let mut i = i;
                let name = il2cpp::read_string(unity::callv(shader, "GetPropertyName", &mut [&mut i as *mut i32 as _])?);
                let ty = unity::callv(shader, "GetPropertyType", &mut [&mut i as *mut i32 as _]).map(|o| il2cpp::unbox::<i32>(o)).unwrap_or(-1);
                let mut v = Value::Null;
                if ty == 4 {
                    let tex = unity::callv(mat, "GetTexture", &mut [il2cpp::string(&name) as *mut c_void])?;
                    if !unity::is_null(tex) {
                        v = json!({"name": unity::name(tex), "ptr": format!("{:p}", tex)});
                    }
                } else if ty == 2 || ty == 3 {
                    v = unity::callv(mat, "GetFloat", &mut [il2cpp::string(&name) as *mut c_void]).map(|o| json!(il2cpp::unbox::<f32>(o))).unwrap_or(Value::Null);
                }
                props.push(json!({"name": name, "type": ty, "value": v}));
            }
            let kw = unity::callv(mat, "get_shaderKeywords", &mut []).map(|a| unity::array_items(a).into_iter().map(|s| il2cpp::read_string(s)).collect::<Vec<_>>()).unwrap_or_default();
            return Ok(json!({"ok": true, "shader": unity::name(shader), "keywords": kw, "props": props}));
        }
        Err("no map terrain".into())
    }
}

/// `{"cmd":"findstatic","type":"Jibbers.MapTools.MapData"}`: every static field of that type in
/// Assembly-CSharp, with its current value.
pub fn findstatic(req: &Value) -> R<Value> {
    unsafe {
        let a = api();
        let want = str_arg(req, "type")?;
        let img = il2cpp::image("Assembly-CSharp.dll").ok_or("no Assembly-CSharp")?;
        let mut out = Vec::new();
        for i in 0..(a.image_get_class_count)(img) {
            let k = (a.image_get_class)(img, i) as *mut Class;
            let mut it: *mut c_void = null_mut();
            loop {
                let f = (a.class_get_fields)(k, &mut it);
                if f.is_null() {
                    break;
                }
                if (a.field_get_flags)(f) & 0x10 == 0 || type_name((a.field_get_type)(f)) != want {
                    continue;
                }
                (a.runtime_class_init)(k);
                let mut buf = [0u8; 64];
                (a.field_static_get_value)(f, buf.as_mut_ptr() as _);
                out.push(json!({
                    "class": cstr((a.class_get_name)(k)),
                    "field": cstr((a.field_get_name)(f)),
                    "value": describe_slot((a.field_get_type)(f), buf.as_ptr(), depth(req, 1)),
                }));
            }
        }
        Ok(json!({"ok": true, "fields": out}))
    }
}

unsafe fn go_tree(go: *mut Object, depth: u32, max_children: usize) -> Value {
    let tr = unity::callv(go, "get_transform", &mut []).unwrap_or(null_mut());
    let n = unity::call_unbox::<i32>(tr, "get_childCount").unwrap_or(0);
    let comp_k = unity::class(unity::CORE, "UnityEngine", "Component").ok();
    let comps: Vec<String> = comp_k
        .and_then(|k| unity::callv(go, "GetComponents", &mut [unity::type_object(k) as *mut c_void]).ok())
        .map(|a| unity::array_items(a).into_iter().map(|c| type_name((api().class_get_type)((api().object_get_class)(c)))).collect())
        .unwrap_or_default();
    let mut children = Vec::new();
    if depth > 0 {
        for i in 0..n.min(max_children as i32) {
            let mut i = i;
            if let Ok(c) = unity::callv(tr, "GetChild", &mut [&mut i as *mut i32 as _]) {
                if let Ok(cgo) = unity::callv(c, "get_gameObject", &mut []) {
                    children.push(go_tree(cgo, depth - 1, max_children));
                }
            }
        }
    }
    json!({"name": unity::name(go), "components": comps, "childCount": n, "position": unity::call_unbox::<crate::unity::Vec3>(tr, "get_position").ok(), "children": children})
}

/// `{"cmd":"hierarchy","depth":3,"max":20}`: root GameObjects of the active scene.
pub fn hierarchy(req: &Value) -> R<Value> {
    unsafe {
        let sm = unity::class(unity::CORE, "UnityEngine.SceneManagement", "SceneManager")?;
        let sk = unity::class(unity::CORE, "UnityEngine.SceneManagement", "Scene")?;
        let scene = unity::call(sm, null_mut(), "GetActiveScene", &mut [])?;
        let p = (api().object_unbox)(scene);
        let roots = unity::array_items(unity::call(sk, p, "GetRootGameObjects", &mut [])?);
        let d = depth(req, 2);
        let max = req.get("max").and_then(Value::as_u64).unwrap_or(20) as usize;
        // `"path":"UI/MainMenu/OptionsMenu"`: only that subtree (inactive objects included)
        if let Some(path) = req.get("path").and_then(Value::as_str) {
            let (root, rest) = path.split_once('/').unwrap_or((path, ""));
            let r = roots.into_iter().find(|g| unity::name(*g) == root).ok_or("no such root object")?;
            let mut go = r;
            if !rest.is_empty() {
                let tr = unity::callv(r, "get_transform", &mut [])?;
                let t = unity::callv(tr, "Find", &mut [il2cpp::string(rest) as *mut c_void])?;
                if unity::is_null(t) {
                    return Err(format!("{path} not found"));
                }
                go = unity::callv(t, "get_gameObject", &mut [])?;
            }
            return Ok(json!({"ok": true, "roots": [go_tree(go, d, max)]}));
        }
        Ok(json!({"ok": true, "roots": roots.into_iter().map(|g| go_tree(g, d, max)).collect::<Vec<_>>()}))
    }
}

/// `{"cmd":"fieldsoftype","pattern":"Transform"}`: Assembly-CSharp fields whose type name
/// contains the pattern (instance and static).
pub fn fieldsoftype(req: &Value) -> R<Value> {
    unsafe {
        let a = api();
        let pat = str_arg(req, "pattern")?;
        let img = il2cpp::image("Assembly-CSharp.dll").ok_or("no Assembly-CSharp")?;
        let mut out = Vec::new();
        for i in 0..(a.image_get_class_count)(img) {
            let k = (a.image_get_class)(img, i) as *mut Class;
            let mut it: *mut c_void = null_mut();
            loop {
                let f = (a.class_get_fields)(k, &mut it);
                if f.is_null() {
                    break;
                }
                let t = type_name((a.field_get_type)(f));
                if t.contains(pat) {
                    let parent = (a.class_get_parent)(k);
                    out.push(format!("{} : {} . {}{} : {}", cstr((a.class_get_name)(k)), if parent.is_null() { String::new() } else { cstr((a.class_get_name)(parent)) },
                        if (a.field_get_flags)(f) & 0x10 != 0 { "static " } else { "" }, cstr((a.field_get_name)(f)), t));
                }
            }
        }
        Ok(json!({"ok": true, "fields": out}))
    }
}

/// `{"cmd":"classinfo","image","ns","class"}`: fields (with offsets) and method signatures.
pub fn classinfo(req: &Value) -> R<Value> {
    unsafe {
        let k = req_class(req)?;
        Ok(json!({"ok": true, "text": crate::dump::class_text(k)}))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_addresses_are_not_managed_references() {
        for kind in [0x0f, 0x10, 0x18, 0x19, 0x1b] {
            assert!(native_pointer_type(kind));
        }
        for kind in [0x02, 0x08, 0x0e, 0x11, 0x12, 0x14, 0x1c, 0x1d] {
            assert!(!native_pointer_type(kind));
        }
    }

    #[test]
    fn absent_slots_are_not_dereferenced() {
        assert_eq!(unsafe { describe_slot(std::ptr::null(), std::ptr::null(), 3) }, Value::Null);
    }
}
