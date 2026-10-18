//! Colour grading through the game's own post-processing and lighting values.
//!
//! The game drives its look from `EnvironmentManager`: plain float fields (ambientSaturation,
//! shadowStrength, ...) and a Beautify volume component it owns (highest-priority global volume).
//! Each frame, before the game's update, we write the configured values: Beautify parameters by
//! name (their `m_Value`, with the override flag set) and EnvironmentManager floats by name. The
//! first time a value is touched its original is remembered, so turning grading off restores it.

use crate::il2cpp::{self, api, type_name, Object};
use crate::unity::{self, R};
use serde_json::{json, Map, Value};
use std::collections::BTreeMap;
use std::ffi::c_void;
use std::ptr::null_mut;

const BEAUTIFY: &str = "Beautify.Universal.Beautify";

/// Values as they were before we changed them: ("beautify"|"override"|"env", name) -> original.
#[derive(Default)]
pub struct Originals(BTreeMap<(&'static str, String), Value>);

/// The Beautify component referenced by the EnvironmentManager (found by field type).
unsafe fn beautify(env: *mut Object) -> Option<*mut Object> {
    let a = api();
    let mut k = (a.object_get_class)(env);
    while !k.is_null() {
        let mut it: *mut c_void = null_mut();
        loop {
            let f = (a.class_get_fields)(k, &mut it);
            if f.is_null() {
                break;
            }
            if (a.field_get_flags)(f) & 0x10 == 0 && type_name((a.field_get_type)(f)) == BEAUTIFY {
                let mut v: *mut Object = null_mut();
                (a.field_get_value)(env, f, &mut v as *mut _ as _);
                return (!v.is_null()).then_some(v);
            }
        }
        k = (a.class_get_parent)(k);
    }
    None
}

/// Reads a field of `o` (float, int/enum, bool or Color) as JSON.
unsafe fn read(o: *mut Object, name: &str) -> R<Value> {
    let a = api();
    let f = il2cpp::field((a.object_get_class)(o), name).ok_or_else(|| format!("no field {name}"))?;
    let t = type_name((a.field_get_type)(f));
    let mut buf = [0u8; 16];
    (a.field_get_value)(o, f, buf.as_mut_ptr() as _);
    let fl = |i: usize| f32::from_le_bytes(buf[i * 4..i * 4 + 4].try_into().unwrap());
    Ok(match t.as_str() {
        "System.Single" => json!(fl(0)),
        "System.Boolean" => json!(buf[0] != 0),
        "UnityEngine.Color" => json!([fl(0), fl(1), fl(2), fl(3)]),
        "System.Int32" => json!(i32::from_le_bytes(buf[0..4].try_into().unwrap())),
        _ if (a.class_is_valuetype)((a.class_from_type)((a.field_get_type)(f))) && t.contains('.') => {
            // Enums: their underlying int.
            json!(i32::from_le_bytes(buf[0..4].try_into().unwrap()))
        }
        other => return Err(format!("{name}: unsupported type {other}")),
    })
}

/// Writes JSON into a field of `o`, using the field's own type.
unsafe fn write(o: *mut Object, name: &str, v: &Value) -> R<()> {
    let a = api();
    let f = il2cpp::field((a.object_get_class)(o), name).ok_or_else(|| format!("no field {name}"))?;
    let t = type_name((a.field_get_type)(f));
    let mut buf = [0u8; 16];
    match (t.as_str(), v) {
        ("System.Single", Value::Number(n)) => buf[..4].copy_from_slice(&(n.as_f64().unwrap_or(0.0) as f32).to_le_bytes()),
        ("System.Boolean", Value::Bool(b)) => buf[0] = *b as u8,
        ("UnityEngine.Color", Value::Array(c)) => {
            for i in 0..4 {
                let x = c.get(i).and_then(Value::as_f64).unwrap_or(1.0) as f32;
                buf[i * 4..i * 4 + 4].copy_from_slice(&x.to_le_bytes());
            }
        }
        (_, Value::Number(n)) => buf[..4].copy_from_slice(&(n.as_i64().unwrap_or(0) as i32).to_le_bytes()),
        _ => return Err(format!("{name}: can't write {v} into {t}")),
    }
    (a.field_set_value)(o, f, buf.as_mut_ptr() as _);
    Ok(())
}

/// A Beautify parameter object (VolumeParameter<T>) by field name.
unsafe fn param(b: *mut Object, name: &str) -> R<*mut Object> {
    let p = unity::field_obj(b, name)?;
    if p.is_null() {
        return Err(format!("Beautify.{name} is null"));
    }
    Ok(p)
}

/// Applies `beautify` / `env` values (or restores the originals when `on` is false).
pub unsafe fn apply(env: *mut Object, on: bool, beautify_vals: &Map<String, Value>, env_vals: &Map<String, Value>, orig: &mut Originals) {
    let b = beautify(env);
    if !on {
        for ((kind, name), v) in std::mem::take(&mut orig.0) {
            let _ = match (kind, b) {
                ("beautify", Some(b)) => param(b, &name).and_then(|p| write(p, "m_Value", &v)),
                ("override", Some(b)) => param(b, &name).and_then(|p| write(p, "m_OverrideState", &v)),
                ("env", _) => write(env, &name, &v),
                _ => Ok(()),
            };
        }
        return;
    }
    if let Some(b) = b {
        for (name, v) in beautify_vals {
            let Ok(p) = param(b, name) else { continue };
            let key = ("beautify", name.clone());
            if !orig.0.contains_key(&key) {
                if let (Ok(o), Ok(ov)) = (read(p, "m_Value"), read(p, "m_OverrideState")) {
                    orig.0.insert(key, o);
                    orig.0.insert(("override", name.clone()), ov);
                }
            }
            let _ = write(p, "m_Value", v);
            let _ = write(p, "m_OverrideState", &Value::Bool(true));
        }
    }
    for (name, v) in env_vals {
        let key = ("env", name.clone());
        if !orig.0.contains_key(&key) {
            if let Ok(o) = read(env, name) {
                orig.0.insert(key, o);
            }
        }
        let _ = write(env, name, v);
    }
}

/// Current values of the given names (for tuning): `{"beautify": {...}, "env": {...}}`.
pub unsafe fn snapshot(env: *mut Object, beautify_names: &[String], env_names: &[String]) -> Value {
    let mut bv = Map::new();
    if let Some(b) = beautify(env) {
        for n in beautify_names {
            let v = param(b, n).and_then(|p| read(p, "m_Value")).unwrap_or_else(|e| json!(e));
            bv.insert(n.clone(), v);
        }
    }
    let ev: Map<String, Value> = env_names.iter().map(|n| (n.clone(), read(env, n).unwrap_or_else(|e| json!(e)))).collect();
    json!({"beautify": bv, "env": ev})
}
