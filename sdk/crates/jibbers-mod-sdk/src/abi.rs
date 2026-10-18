//! C ABI between the loader (version.dll) and the core DLL. Shared by both crates via `#[path]`.
//!
//! The loader owns everything that must outlive a core reload: the version.dll proxy, the
//! per-frame hooks that run jobs on Unity's main thread, and the localhost server. The core holds
//! the game logic and can be unloaded and replaced while the game runs.

// Each crate uses only its side of the ABI.
#![allow(dead_code)]

use std::ffi::c_void;

pub const ABI_VERSION: u32 = 4;

/// A job to run on the main thread.
pub type JobFn = unsafe extern "C" fn(ctx: *mut c_void);
/// Called (on the main thread) before a hooked instance method runs, with its `this` and first
/// argument (a pointer for reference types and for structs larger than 8 bytes).
pub type PreHookFn = unsafe extern "C" fn(this: *mut c_void, arg0: *mut c_void);

#[repr(C)]
pub struct Host {
    pub abi_version: u32,
    /// Runs `job(ctx)` on Unity's main thread and blocks until it has run. Returns 0 on success,
    /// non-zero if the main thread didn't pick it up in time (the job is then never run).
    pub run_main: unsafe extern "C" fn(job: JobFn, ctx: *mut c_void) -> i32,
    /// Calls `cb(this, arg0)` before every call of the instance method `method` (an IL2CPP
    /// MethodInfo*). Only for void methods with at most two parameters and no float parameters:
    /// the detour forwards the four integer argument registers unchanged. The loader owns the
    /// detour. The callback belongs to the core whose `jh_core_init` registers it and is dropped
    /// when that core unloads. Up to three cores can hook the same method; their callbacks run in
    /// registration order. Returns 0 on success.
    pub pre_hook: unsafe extern "C" fn(method: *const c_void, cb: PreHookFn) -> i32,
    /// Writes a JSON array of every loaded core: `[{"id": owner id, "file": DLL name without
    /// extension, "developer": bool}]`. The buffer is freed with `free_buf`. Read locks only, so a
    /// core may call it from its own handler or hook; never while the cores are being swapped.
    pub list_plugins: unsafe extern "C" fn(out: *mut *mut u8, out_len: *mut usize),
    /// Forwards one JSON request to the core with owner id `id` (its `jh_core_handle`) and writes
    /// its reply, freed with `free_buf`. Returns 0 on success, 1 if no loaded core has that id
    /// (then `out` is null). Same locking rules as `list_plugins`: a core must not call it from its
    /// own `jh_core_init`, since the cores are write-locked then.
    pub call_plugin: unsafe extern "C" fn(id: u32, req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize) -> i32,
    /// Frees a buffer returned by `list_plugins` or `call_plugin`.
    pub free_buf: unsafe extern "C" fn(p: *mut u8, len: usize),
}

/// `jh_core_init(host) -> abi version of the core`.
pub type CoreInit = unsafe extern "C" fn(host: *const Host) -> u32;
/// `jh_core_handle(req, len, &out, &out_len)`: one JSON request in, one JSON reply out.
/// The reply buffer is freed with `jh_core_free`.
pub type CoreHandle = unsafe extern "C" fn(req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize);
pub type CoreFree = unsafe extern "C" fn(p: *mut u8, len: usize);
/// Optional `jh_core_unload()`: called (from a loader thread, with the cores write-locked) just
/// before the core is unloaded, so it can remove game objects whose callbacks point into its code.
/// It may run jobs on the main thread (`run_main`); it must not call `call_plugin`/`list_plugins`.
pub type CoreUnload = unsafe extern "C" fn();
