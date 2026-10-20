//! Pre-call hooks on managed instance methods, owned by the loader on behalf of the cores.
//!
//! The detours live here (they must survive core reloads); a core only registers callbacks, and
//! each callback is tagged with the owner id of the core whose `jh_core_init` registered it (see
//! `set_owner`). When the developer core is reloaded, `clear_callbacks(owner)` drops only its own.
//!
//! One detour per target method. Several cores can hook the same method: each target gets a slot
//! holding up to `PER_SLOT` callbacks, all called in registration order inside that one detour.

use crate::abi::PreHookFn;
use crate::{core, il2cpp, log};
use retour::RawDetour;
use std::ffi::c_void;
use std::sync::atomic::{AtomicU32, AtomicUsize, Ordering};
use std::sync::Mutex;

const SLOTS: usize = 12;
/// Callbacks per slot: one for each core that hooks the same method.
const PER_SLOT: usize = 3;

static TARGETS: [AtomicUsize; SLOTS] = [const { AtomicUsize::new(0) }; SLOTS];
static ORIGINALS: [AtomicUsize; SLOTS] = [const { AtomicUsize::new(0) }; SLOTS];
static BOOLEAN: [AtomicU32; SLOTS] = [const { AtomicU32::new(0) }; SLOTS];
static CALLBACKS: [[AtomicUsize; PER_SLOT]; SLOTS] =
    [const { [const { AtomicUsize::new(0) }; PER_SLOT] }; SLOTS];
static OWNERS: [[AtomicU32; PER_SLOT]; SLOTS] =
    [const { [const { AtomicU32::new(0) }; PER_SLOT] }; SLOTS];

/// Owner id of the core being initialised (0 = none). Set by the loader around `jh_core_init`.
static CURRENT_OWNER: AtomicU32 = AtomicU32::new(0);

struct Hook(#[allow(dead_code)] RawDetour);
unsafe impl Send for Hook {}
static DETOURS: Mutex<Vec<Hook>> = Mutex::new(Vec::new());

unsafe fn callbacks(i: usize, a0: *mut c_void, a1: *mut c_void) {
    if let Some(_core) = core::try_read() {
        for cb in &CALLBACKS[i] {
            let cb = cb.load(Ordering::Acquire);
            if cb != 0 {
                let cb: PreHookFn = std::mem::transmute(cb);
                cb(a0, a1);
            }
        }
    }
}

unsafe fn call_bool(original: usize, args: [*mut c_void; 4]) -> bool {
    let original: unsafe extern "C" fn(*mut c_void, *mut c_void, *mut c_void, *mut c_void) -> bool =
        std::mem::transmute(original);
    original(args[0], args[1], args[2], args[3])
}

macro_rules! slots {
    ($($i:literal => $name:ident / $boolean:ident),*) => {
        $(
            // Win64: the first four integer/pointer arguments arrive in rcx, rdx, r8, r9. For an
            // IL2CPP instance method these are `this`, its parameters, then the MethodInfo*.
            unsafe extern "C" fn $name(a0: *mut c_void, a1: *mut c_void, a2: *mut c_void, a3: *mut c_void) {
                callbacks($i, a0, a1);
                let orig: unsafe extern "C" fn(*mut c_void, *mut c_void, *mut c_void, *mut c_void) =
                    std::mem::transmute(ORIGINALS[$i].load(Ordering::Acquire));
                orig(a0, a1, a2, a3)
            }
            unsafe extern "C" fn $boolean(a0: *mut c_void, a1: *mut c_void, a2: *mut c_void, a3: *mut c_void) -> bool {
                callbacks($i, a0, a1);
                call_bool(ORIGINALS[$i].load(Ordering::Acquire), [a0, a1, a2, a3])
            }
        )*
        const DETOUR_FNS: [unsafe extern "C" fn(*mut c_void, *mut c_void, *mut c_void, *mut c_void); SLOTS] = [$($name),*];
        const BOOL_DETOUR_FNS: [unsafe extern "C" fn(*mut c_void, *mut c_void, *mut c_void, *mut c_void) -> bool; SLOTS] = [$($boolean),*];
    };
}
slots!(
    0 => slot0 / bool0, 1 => slot1 / bool1, 2 => slot2 / bool2, 3 => slot3 / bool3, 4 => slot4 / bool4, 5 => slot5 / bool5,
    6 => slot6 / bool6, 7 => slot7 / bool7, 8 => slot8 / bool8, 9 => slot9 / bool9, 10 => slot10 / bool10, 11 => slot11 / bool11
);

/// Marks the core being initialised: callbacks registered until the next call belong to `owner`.
/// The loader sets it before `jh_core_init` and resets it to 0 afterwards.
pub fn set_owner(owner: u32) {
    CURRENT_OWNER.store(owner, Ordering::Release);
}

/// Host callback: registers `cb` before `method` (MethodInfo*) for the current owner, installing
/// the detour once per method. A core that hooks a method twice keeps only its latest callback.
pub unsafe extern "C" fn pre_hook(method: *const c_void, cb: PreHookFn) -> i32 {
    if method.is_null() {
        return 1;
    }
    let api = il2cpp::api();
    let method_info = method as *const il2cpp::Method;
    let return_type = il2cpp::type_name((api.method_get_return_type)(method_info));
    let boolean = match return_type.as_str() {
        "System.Void" => false,
        "System.Boolean" => true,
        _ => return 6,
    };
    if (api.method_get_param_count)(method_info) > 2 {
        return 6;
    }
    for p in 0..(api.method_get_param_count)(method_info) {
        if matches!(
            il2cpp::type_name((api.method_get_param)(method_info, p)).as_str(),
            "System.Single" | "System.Double"
        ) {
            return 6;
        }
    }
    let owner = CURRENT_OWNER.load(Ordering::Acquire);
    let target = *(method as *const usize);
    let mut detours = DETOURS.lock().unwrap();
    if let Some(i) = (0..SLOTS).find(|&i| TARGETS[i].load(Ordering::Acquire) == target) {
        if BOOLEAN[i].load(Ordering::Acquire) != u32::from(boolean) {
            return 6;
        }
        let k = (0..PER_SLOT)
            .find(|&k| {
                CALLBACKS[i][k].load(Ordering::Acquire) != 0
                    && OWNERS[i][k].load(Ordering::Acquire) == owner
            })
            .or_else(|| (0..PER_SLOT).find(|&k| CALLBACKS[i][k].load(Ordering::Acquire) == 0));
        let Some(k) = k else {
            log::info("hooks: no free callback on a shared target");
            return 5;
        };
        OWNERS[i][k].store(owner, Ordering::Release);
        CALLBACKS[i][k].store(cb as usize, Ordering::Release);
        return 0;
    }
    let Some(i) = (0..SLOTS).find(|&i| TARGETS[i].load(Ordering::Acquire) == 0) else {
        log::info("hooks: no free slot");
        return 2;
    };
    let detour = if boolean {
        BOOL_DETOUR_FNS[i] as *const ()
    } else {
        DETOUR_FNS[i] as *const ()
    };
    BOOLEAN[i].store(u32::from(boolean), Ordering::Release);
    match RawDetour::new(target as *const (), detour) {
        Ok(d) => {
            ORIGINALS[i].store(d.trampoline() as *const () as usize, Ordering::Release);
            OWNERS[i][0].store(owner, Ordering::Release);
            CALLBACKS[i][0].store(cb as usize, Ordering::Release);
            if let Err(e) = d.enable() {
                log::info(format!("hooks: enable failed: {e}"));
                CALLBACKS[i][0].store(0, Ordering::Release);
                OWNERS[i][0].store(0, Ordering::Release);
                return 3;
            }
            TARGETS[i].store(target, Ordering::Release);
            detours.push(Hook(d));
            0
        }
        Err(e) => {
            log::info(format!("hooks: detour failed: {e}"));
            4
        }
    }
}

/// Capability probe for plugins using boolean pre-hooks; older ABI-4 loaders lack this export.
#[no_mangle]
pub extern "C" fn jh_pre_hook_bool_supported() -> u32 {
    1
}

#[cfg(test)]
mod tests {
    use super::*;
    unsafe extern "C" fn original(
        a: *mut c_void,
        b: *mut c_void,
        c: *mut c_void,
        d: *mut c_void,
    ) -> bool {
        [a as usize, b as usize, c as usize, d as usize] == [1, 2, 3, 4]
    }
    #[test]
    fn boolean_original_preserves_arguments_and_both_return_values() {
        unsafe {
            assert!(call_bool(
                original as *const () as usize,
                [1usize as _, 2usize as _, 3usize as _, 4usize as _]
            ));
            assert!(!call_bool(
                original as *const () as usize,
                [1usize as _, 2usize as _, 3usize as _, 5usize as _]
            ));
        }
    }
}

/// Drops the callbacks registered by `owner` (called with the cores write-locked, before that
/// core is unloaded). The detours stay installed.
pub fn clear_callbacks(owner: u32) {
    for i in 0..SLOTS {
        for k in 0..PER_SLOT {
            if OWNERS[i][k].load(Ordering::Acquire) == owner {
                CALLBACKS[i][k].store(0, Ordering::Release);
                OWNERS[i][k].store(0, Ordering::Release);
            }
        }
    }
}
