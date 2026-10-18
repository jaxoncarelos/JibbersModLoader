//! Thin helpers over UnityEngine APIs, called through IL2CPP. Main thread only.

use crate::il2cpp::{self, api, Class, Object};
use std::ffi::c_void;
use std::ptr::null_mut;

pub type R<T> = Result<T, String>;

pub const CORE: &str = "UnityEngine.CoreModule.dll";
pub const TERRAIN: &str = "UnityEngine.TerrainModule.dll";

#[derive(Clone, Copy, Debug, Default, PartialEq, serde::Serialize)]
#[repr(C)]
pub struct Vec3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

pub unsafe fn class(img: &str, ns: &str, name: &str) -> R<*mut Class> {
    il2cpp::class(img, ns, name).ok_or_else(|| format!("class {ns}.{name} not found in {img}"))
}

/// Calls `name` (with `argc` params) on `this` (null for static), resolving the method on `k`.
pub unsafe fn call(k: *const Class, this: *mut c_void, name: &str, args: &mut [*mut c_void]) -> R<*mut Object> {
    let m = il2cpp::method(k, name, args.len() as i32).ok_or_else(|| format!("method {name}/{} not found", args.len()))?;
    il2cpp::invoke(m, this, args).map_err(|e| format!("{name}: {e}"))
}

/// Same as `call`, on the object's own runtime class.
pub unsafe fn callv(this: *mut Object, name: &str, args: &mut [*mut c_void]) -> R<*mut Object> {
    // A null object (e.g. UI destroyed by a scene change) has no class: reading it would crash.
    if this.is_null() {
        return Err(format!("{name} on a null object"));
    }
    call((api().object_get_class)(this), this, name, args)
}

pub unsafe fn call_unbox<T: Copy>(this: *mut Object, name: &str) -> R<T> {
    let r = callv(this, name, &mut [])?;
    if r.is_null() {
        return Err(format!("{name} returned null"));
    }
    Ok(il2cpp::unbox::<T>(r))
}

pub unsafe fn type_object(k: *const Class) -> *mut Object {
    (api().type_get_object)((api().class_get_type)(k))
}

/// Root a managed object using Unity 6's pointer-sized GCHandle API. The legacy
/// il2cpp_gchandle_* u32 exports truncate handles in this game and must not be used.
pub unsafe fn gc_handle(o: *mut Object) -> R<isize> {
    let k = class("mscorlib.dll", "System.Runtime.InteropServices", "GCHandle")?;
    let mut zero: isize = 0;
    let mut normal: i32 = 2; // GCHandleType.Normal
    let r = call(k, null_mut(), "GetTargetHandle", &mut [o, &mut zero as *mut isize as _, &mut normal as *mut i32 as _])?;
    if r.is_null() {
        return Err("GCHandle.GetTargetHandle returned null".into());
    }
    let h = il2cpp::unbox::<isize>(r);
    if h == 0 {
        return Err("GCHandle.GetTargetHandle returned an empty handle".into());
    }
    Ok(h)
}

pub unsafe fn gc_target(mut h: isize) -> R<*mut Object> {
    if h == 0 {
        return Ok(null_mut());
    }
    let k = class("mscorlib.dll", "System.Runtime.InteropServices", "GCHandle")?;
    call(k, null_mut(), "GetTarget", &mut [&mut h as *mut isize as _])
}

pub unsafe fn gc_free(mut h: isize) -> R<()> {
    if h != 0 {
        let k = class("mscorlib.dll", "System.Runtime.InteropServices", "GCHandle")?;
        call(k, null_mut(), "FreeHandle", &mut [&mut h as *mut isize as _])?;
    }
    Ok(())
}

pub unsafe fn is_null(o: *mut Object) -> bool {
    // A destroyed UnityEngine.Object is a live managed object whose native pointer is gone;
    // UnityEngine.Object.op_Implicit(bool) handles both cases.
    if o.is_null() {
        return true;
    }
    let Ok(k) = class(CORE, "UnityEngine", "Object") else { return false };
    let Some(m) = il2cpp::method(k, "op_Implicit", 1) else { return false };
    match il2cpp::invoke(m, null_mut(), &mut [o]) {
        Ok(r) if !r.is_null() => !il2cpp::unbox::<bool>(r),
        _ => false,
    }
}

/// All loaded (active) objects of a component type.
pub unsafe fn find_objects(k: *const Class) -> R<Vec<*mut Object>> {
    let uobj = class(CORE, "UnityEngine", "Object")?;
    let t = type_object(k);
    let arr = if let Some(m) = il2cpp::method_sig(uobj, "FindObjectsByType", &["System.Type", "UnityEngine.FindObjectsSortMode"]) {
        let mut mode: i32 = 0; // None
        il2cpp::invoke(m, null_mut(), &mut [t, &mut mode as *mut i32 as _])?
    } else {
        call(uobj, null_mut(), "FindObjectsOfType", &mut [t])?
    };
    Ok(array_items(arr))
}

pub unsafe fn array_items(arr: *mut Object) -> Vec<*mut Object> {
    if arr.is_null() {
        return vec![];
    }
    let n = (api().array_length)(arr) as usize;
    let p = il2cpp::array_data::<*mut Object>(arr);
    (0..n).map(|i| *p.add(i)).collect()
}

pub unsafe fn name(o: *mut Object) -> String {
    callv(o, "get_name", &mut []).map(|s| il2cpp::read_string(s)).unwrap_or_default()
}

pub unsafe fn position(component: *mut Object) -> R<Vec3> {
    let tr = callv(component, "get_transform", &mut [])?;
    call_unbox::<Vec3>(tr, "get_position")
}

/// Reads an object reference field by name.
pub unsafe fn field_obj(o: *mut Object, name: &str) -> R<*mut Object> {
    if o.is_null() {
        return Err(format!("field {name} of a null object"));
    }
    let f = il2cpp::field((api().object_get_class)(o), name).ok_or_else(|| format!("field {name} not found"))?;
    let mut v: *mut Object = null_mut();
    (api().field_get_value)(o, f, &mut v as *mut _ as _);
    Ok(v)
}

pub unsafe fn field_val<T: Copy + Default>(o: *mut Object, name: &str) -> R<T> {
    if o.is_null() {
        return Err(format!("field {name} of a null object"));
    }
    let f = il2cpp::field((api().object_get_class)(o), name).ok_or_else(|| format!("field {name} not found"))?;
    let mut v = T::default();
    (api().field_get_value)(o, f, &mut v as *mut T as _);
    Ok(v)
}

pub unsafe fn set_field_obj(o: *mut Object, name: &str, v: *mut Object) -> R<()> {
    if o.is_null() {
        return Err(format!("field {name} of a null object"));
    }
    let f = il2cpp::field((api().object_get_class)(o), name).ok_or_else(|| format!("field {name} not found"))?;
    (api().field_set_value)(o, f, v as *mut c_void);
    Ok(())
}

/// A new managed `float[rows, cols]`; returns the array and a pointer to its row-major data.
pub unsafe fn new_float2d(rows: usize, cols: usize) -> R<(*mut Object, *mut f32)> {
    let single = class("mscorlib.dll", "System", "Single")?;
    let k = (api().bounded_array_class_get)(single, 2, false);
    let mut lengths = [rows, cols];
    let mut lower = [0usize, 0];
    let arr = (api().array_new_full)(k, lengths.as_mut_ptr(), lower.as_mut_ptr());
    if arr.is_null() {
        return Err("could not allocate float[,]".into());
    }
    Ok((arr, il2cpp::array_data::<f32>(arr)))
}

pub unsafe fn new_byte_array(bytes: &[u8]) -> R<*mut Object> {
    let byte = class("mscorlib.dll", "System", "Byte")?;
    let arr = (api().array_new)(byte, bytes.len());
    if arr.is_null() {
        return Err("could not allocate byte[]".into());
    }
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), il2cpp::array_data::<u8>(arr), bytes.len());
    Ok(arr)
}

pub unsafe fn boxed_i32(v: &mut i32) -> *mut c_void {
    v as *mut i32 as *mut c_void
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_gc_handle_has_no_target() {
        // No IL2CPP runtime is needed: an absent label must never enter the managed API.
        assert!(unsafe { gc_target(0) }.unwrap().is_null());
    }

    #[test]
    fn freeing_empty_gc_handle_is_a_noop() {
        assert!(unsafe { gc_free(0) }.is_ok());
    }
}
