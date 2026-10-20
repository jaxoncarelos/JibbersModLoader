//! JSON descriptions of live game objects, for logs and developer probes. Main thread only.
//!
//! ```ignore
//! let cam = symbols::live("camera.follow")?.first().copied().ok_or("no camera")?;
//! log::info(format!("{}", inspect::describe(cam, 1)));
//! ```
//!
//! Values are read by their declared type: numbers, bools, strings, enums (as their number),
//! structs field by field, arrays (first [`MAX_ITEMS`] items), and objects as `{"$type", fields...}`
//! down to `depth`. Fields of the engine base classes (`Component`, `MonoBehaviour`, ...) are
//! native handles and are skipped. Pointer-typed fields are shown as addresses, never followed.

use crate::il2cpp::{self, api, cstr, type_name, Class, Object, Type};
use serde_json::{json, Map, Value};
use std::ffi::c_void;
use std::ptr::null_mut;

/// Array items described per array.
pub const MAX_ITEMS: usize = 64;

// IL2CPP_TYPE_PTR, BYREF, FNPTR, I and U are addresses, never managed object references.
fn native_pointer_type(kind: i32) -> bool {
    matches!(kind, 0x0f | 0x10 | 0x18 | 0x19 | 0x1b)
}

/// Description of a value of type `t` stored at `p` (a pointer to the slot: the field's address,
/// or a buffer filled by `field_static_get_value`).
pub unsafe fn describe_slot(t: *const Type, p: *const u8, depth: u32) -> Value {
    if t.is_null() || p.is_null() {
        return Value::Null;
    }
    if native_pointer_type((api().type_get_type)(t)) {
        return json!({"$pointer": format!("0x{:x}", std::ptr::read_unaligned(p as *const usize))});
    }
    match type_name(t).as_str() {
        "System.Boolean" => json!(*p != 0),
        "System.Byte" => json!(*p),
        "System.SByte" => json!(*(p as *const i8)),
        "System.Int16" => json!(std::ptr::read_unaligned(p as *const i16)),
        "System.UInt16" | "System.Char" => json!(std::ptr::read_unaligned(p as *const u16)),
        "System.Int32" => json!(std::ptr::read_unaligned(p as *const i32)),
        "System.UInt32" => json!(std::ptr::read_unaligned(p as *const u32)),
        "System.Int64" => json!(std::ptr::read_unaligned(p as *const i64)),
        "System.UInt64" => json!(std::ptr::read_unaligned(p as *const u64)),
        "System.Single" => json!(std::ptr::read_unaligned(p as *const f32)),
        "System.Double" => json!(std::ptr::read_unaligned(p as *const f64)),
        _ => {
            let k = (api().class_from_type)(t);
            if k.is_null() {
                return Value::Null;
            }
            if (api().class_is_valuetype)(k) {
                describe_struct(k, p, depth)
            } else {
                describe(std::ptr::read_unaligned(p as *const *mut Object), depth)
            }
        }
    }
}

unsafe fn describe_struct(k: *mut Class, p: *const u8, depth: u32) -> Value {
    let a = api();
    let mut m = Map::new();
    m.insert("$type".into(), json!(type_name((a.class_get_type)(k))));
    if depth == 0 {
        return Value::Object(m);
    }
    let mut it: *mut c_void = null_mut();
    loop {
        let f = (a.class_get_fields)(k, &mut it);
        if f.is_null() {
            break;
        }
        if (a.field_get_flags)(f) & 0x10 != 0 {
            continue;
        }
        // Field offsets of value types include the (absent) object header.
        let Some(off) = (a.field_get_offset)(f).checked_sub(16) else { continue };
        m.insert(cstr((a.field_get_name)(f)), describe_slot((a.field_get_type)(f), p.add(off), depth - 1));
    }
    // An enum is its number.
    if m.len() == 2 {
        if let Some(v) = m.get("value__").cloned() {
            return v;
        }
    }
    Value::Object(m)
}

/// Description of an object (null, string, array, boxed value or class instance).
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
    if tname.ends_with(']') {
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
    if is_unity_object(k) {
        // A destroyed UnityEngine.Object is still a managed object; say so instead of reading it.
        if crate::unity::is_null(o) {
            m.insert("$destroyed".into(), json!(true));
            return Value::Object(m);
        }
        m.insert("$name".into(), json!(crate::unity::name(o)));
    }
    if depth == 0 {
        return Value::Object(m);
    }
    let mut cur = k;
    while !cur.is_null() {
        // Stop at engine base classes; their fields are native handles.
        if cstr((a.class_get_namespace)(cur)) == "UnityEngine" || cstr((a.class_get_name)(cur)) == "Object" {
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

/// Whether `k` derives from `UnityEngine.Object`.
pub unsafe fn is_unity_object(k: *mut Class) -> bool {
    match il2cpp::class(crate::unity::CORE, "UnityEngine", "Object") {
        Some(u) => (api().class_is_assignable_from)(u, k),
        None => false,
    }
}

/// Description of a static field's current value.
pub unsafe fn describe_static(f: *mut il2cpp::Field, depth: u32) -> Value {
    let a = api();
    let t = (a.field_get_type)(f);
    let fk = (a.class_from_type)(t);
    let size = if !fk.is_null() && (a.class_is_valuetype)(fk) { (a.class_value_size)(fk, null_mut()).max(0) as usize } else { 8 };
    // Reflection writes the whole value: size the buffer for it (large interop structs exist).
    let mut buf = vec![0u8; size.max(8)];
    (a.field_static_get_value)(f, buf.as_mut_ptr() as _);
    describe_slot(t, buf.as_ptr(), depth)
}

/// Description of an instance field's current value.
pub unsafe fn describe_field(o: *mut Object, f: *mut il2cpp::Field, depth: u32) -> Value {
    if o.is_null() {
        return Value::Null;
    }
    let a = api();
    if (a.field_get_flags)(f) & 0x10 != 0 {
        return describe_static(f, depth);
    }
    describe_slot((a.field_get_type)(f), (o as *const u8).add((a.field_get_offset)(f)), depth)
}
