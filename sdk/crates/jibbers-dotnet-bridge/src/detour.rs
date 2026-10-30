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

#[cfg(all(test, windows, target_arch = "x86_64"))]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use windows_sys::Win32::System::Memory::{VirtualAlloc, MEM_COMMIT, MEM_RESERVE, PAGE_EXECUTE_READWRITE};

    static ORIGINAL: AtomicUsize = AtomicUsize::new(0);

    extern "C" fn replacement() -> u8 {
        let original: extern "C" fn() -> u8 = unsafe { std::mem::transmute(ORIGINAL.load(Ordering::SeqCst)) };
        original() + 10
    }

    /// SteamAPI_ManualDispatch_GetNextCallback opens with a RIP-relative compare that has an
    /// immediate after its displacement; relocating it wrongly crashed the game on the first frame.
    #[test]
    fn relocates_rip_relative_compare_with_immediate() {
        #[rustfmt::skip]
        let code: [u8; 27] = [
            0x48, 0x83, 0xEC, 0x28,             // 0:  sub rsp, 0x28
            0x83, 0x3D, 53, 0, 0, 0, 0x00,      // 4:  cmp dword ptr [rip+53], 0   (the flag at 64)
            0x7F, 0x07,                         // 11: jg 20
            0x30, 0xC0,                         // 13: xor al, al
            0x48, 0x83, 0xC4, 0x28,             // 15: add rsp, 0x28
            0xC3,                               // 19: ret
            0xB0, 0x01,                         // 20: mov al, 1
            0x48, 0x83, 0xC4, 0x28,             // 22: add rsp, 0x28
            0xC3,                               // 26: ret
        ];
        unsafe {
            let page = VirtualAlloc(std::ptr::null(), 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE) as *mut u8;
            assert!(!page.is_null());
            std::ptr::copy_nonoverlapping(code.as_ptr(), page, code.len());
            let flag = page.add(64) as *mut i32;
            let function: extern "C" fn() -> u8 = std::mem::transmute(page);
            *flag = 0;
            assert_eq!(function(), 0);
            *flag = 5;
            assert_eq!(function(), 1);

            let mut trampoline = std::ptr::null();
            let handle = create(page as *const c_void, replacement as *const c_void, &mut trampoline);
            assert!(!handle.is_null());
            ORIGINAL.store(trampoline as usize, Ordering::SeqCst);
            assert_eq!(enable(handle), 0);
            assert_eq!(function(), 11, "the trampoline must read the flag the original reads");
            *flag = 0;
            assert_eq!(function(), 10);
            destroy(handle);
            assert_eq!(function(), 0);
        }
    }
}
