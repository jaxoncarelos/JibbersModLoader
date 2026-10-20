//! Game delegates (`System.Action<T>`, ...) that call native Rust functions, and the reflection
//! helpers to name generic types. Main thread only.
//!
//! IL2CPP calls a delegate as `invoke_impl(method_code, args..., method)`. A delegate made by
//! [`native`] has `invoke_impl` set to your function and `method_code` to your `ctx`, so for an
//! `Action<T>` your function is `extern "C" fn(ctx: usize, arg: T, method: *const c_void)`, with
//! `arg` passed by the Windows x64 rules: integers, pointers and references in an integer
//! register; `float`/`double` in a float register; structs of 1, 2, 4 or 8 bytes by value and any
//! other struct as a pointer to a copy. [`crate::events`] and [`crate::vars`] wrap this for you.
//!
//! The game keeps the delegate alive while it holds it. The function must stay loaded while the
//! game can call it: remove what you handed out in `Plugin::unload`.

use crate::il2cpp::{self, api, Class, Object};
use crate::unity::{self, R};
use std::ffi::c_void;

/// `System.Type` object of a class.
pub unsafe fn type_of(k: *const Class) -> *mut Object {
    unity::type_object(k)
}

/// The class behind a `System.Type` object.
pub unsafe fn class_of(t: *mut Object) -> R<*mut Class> {
    if t.is_null() {
        return Err("null System.Type".into());
    }
    let k = (api().class_from_system_type)(t);
    if k.is_null() {
        Err("System.Type has no class".into())
    } else {
        Ok(k)
    }
}

/// A new `System.Type[]` holding the classes' types.
unsafe fn type_array(classes: &[*mut Class]) -> R<*mut Object> {
    let tk = unity::class("mscorlib.dll", "System", "Type")?;
    let arr = (api().array_new)(tk, classes.len());
    if arr.is_null() {
        return Err("could not allocate Type[]".into());
    }
    let data = il2cpp::array_data::<*mut Object>(arr);
    for (i, k) in classes.iter().enumerate() {
        *data.add(i) = type_of(*k);
    }
    Ok(arr)
}

/// `def<args>`: a generic class instantiated with `args`, e.g. `Action`1` with `MapLoadEvent`.
/// The instantiation must exist in the game (it does whenever the game uses that type).
pub unsafe fn instantiate(def: *mut Class, args: &[*mut Class]) -> R<*mut Class> {
    let arr = type_array(args)?;
    let t = unity::callv(type_of(def), "MakeGenericType", &mut [arr as *mut c_void])?;
    class_of(t)
}

/// The generic arguments of an instantiated class (`List<int>` -> `[int]`).
pub unsafe fn generic_args(k: *mut Class) -> R<Vec<*mut Class>> {
    let arr = unity::callv(type_of(k), "GetGenericArguments", &mut [])?;
    unity::array_items(arr).into_iter().map(|t| class_of(t)).collect()
}

/// `System.Action<T>` for `arg`.
pub unsafe fn action_of(arg: *mut Class) -> R<*mut Class> {
    instantiate(unity::class("mscorlib.dll", "System", "Action`1")?, &[arg])
}

/// A new delegate of class `dk` that calls `f` with `ctx` as its first argument.
pub unsafe fn native(dk: *mut Class, f: *const c_void, ctx: usize) -> R<*mut Object> {
    let a = api();
    let invoke = il2cpp::method(dk, "Invoke", -1).ok_or("delegate has no Invoke")?;
    let d = (a.object_new)(dk);
    if d.is_null() {
        return Err("could not allocate delegate".into());
    }
    for (name, v) in [("method_ptr", f as usize), ("invoke_impl", f as usize), ("method", invoke as usize), ("method_code", ctx)] {
        let fld = il2cpp::field(dk, name).ok_or_else(|| format!("delegate field {name} missing"))?;
        let mut v = v;
        (a.field_set_value)(d, fld, &mut v as *mut usize as _);
    }
    Ok(d)
}

/// Calls a delegate's `Invoke` (to notify the game's own listeners). `args` as for [`il2cpp::invoke`].
pub unsafe fn invoke(d: *mut Object, args: &mut [*mut c_void]) -> R<*mut Object> {
    if d.is_null() {
        return Err("null delegate".into());
    }
    let k = (api().object_get_class)(d);
    let m = il2cpp::method(k, "Invoke", args.len() as i32).ok_or("delegate has no Invoke")?;
    il2cpp::invoke(m, d, args)
}

/// How a value of `size` bytes reaches a native function under the Windows x64 convention: by
/// value in a register (true) or as a pointer to a copy (false).
pub fn struct_in_register(size: usize) -> bool {
    matches!(size, 1 | 2 | 4 | 8)
}

/// Size of a value type's data (without the object header).
pub unsafe fn value_size(k: *mut Class) -> usize {
    (api().class_value_size)(k, std::ptr::null_mut()).max(0) as usize
}

/// A `System.Object` to own subscriptions (some game registries are keyed by owner).
pub unsafe fn new_owner() -> R<*mut Object> {
    let k = unity::class("mscorlib.dll", "System", "Object")?;
    let o = (api().object_new)(k);
    if o.is_null() {
        Err("could not allocate owner".into())
    } else {
        Ok(o)
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn windows_x64_struct_passing() {
        assert!([1, 2, 4, 8].iter().all(|&n| super::struct_in_register(n)));
        assert!([3, 5, 6, 7, 12, 16, 24].iter().all(|&n| !super::struct_in_register(n)));
    }
}
