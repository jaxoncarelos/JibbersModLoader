//! Raw detours for C# mods: replace any native function (an IL2CPP method's code, a MuJoCo export)
//! with a C# `[UnmanagedCallersOnly]` function of the same signature, and call the original through
//! the trampoline. Unlike the loader's pre-hooks there is no slot limit and any signature works.

use crate::set_error;
use retour::RawDetour;
use std::ffi::c_void;

/// Creates a disabled detour of `target` to `replacement`; writes the trampoline (call it to run
/// the original) to `*trampoline`. Returns a handle, or null (see `last_error`).
pub unsafe extern "C" fn create(target: *const c_void, replacement: *const c_void, trampoline: *mut *const c_void) -> *mut c_void {
    if target.is_null() || replacement.is_null() {
        set_error("detour: null target or replacement");
        return std::ptr::null_mut();
    }
    match RawDetour::new(target as *const (), replacement as *const ()) {
        Ok(d) => {
            if !trampoline.is_null() {
                *trampoline = d.trampoline() as *const () as *const c_void;
            }
            Box::into_raw(Box::new(d)) as *mut c_void
        }
        Err(e) => {
            set_error(format!("detour: {e}"));
            std::ptr::null_mut()
        }
    }
}

pub unsafe extern "C" fn enable(h: *mut c_void) -> i32 {
    if h.is_null() {
        return 1;
    }
    match (*(h as *mut RawDetour)).enable() {
        Ok(()) => 0,
        Err(e) => {
            set_error(format!("detour enable: {e}"));
            2
        }
    }
}

pub unsafe extern "C" fn disable(h: *mut c_void) -> i32 {
    if h.is_null() {
        return 1;
    }
    match (*(h as *mut RawDetour)).disable() {
        Ok(()) => 0,
        Err(e) => {
            set_error(format!("detour disable: {e}"));
            2
        }
    }
}

pub unsafe extern "C" fn destroy(h: *mut c_void) {
    if !h.is_null() {
        let d = Box::from_raw(h as *mut RawDetour);
        let _ = d.disable();
    }
}
