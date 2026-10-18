//! The loader's host table, as seen from a plugin: main-thread jobs, method hooks and calls
//! between plugins.
//!
//! Locking rules (enforced by the loader, not by this crate):
//! - Do not call [`list_plugins`] / [`call_plugin`] from `Plugin::init` (the loader holds the plugin
//!   list write-locked then) or from `Plugin::unload`.
//! - Never block the main thread waiting on another thread that is itself waiting on
//!   [`pump::run`]; that deadlocks.

use crate::abi::{self, Host, PreHookFn};
use crate::{il2cpp, log};
use serde_json::Value;
use std::ffi::c_void;
use std::sync::OnceLock;

struct HostRef(&'static Host);
unsafe impl Sync for HostRef {}
unsafe impl Send for HostRef {}
static HOST: OnceLock<HostRef> = OnceLock::new();

/// Stores the loader's host table (called by the generated `jh_core_init`). Returns false when
/// the loader's ABI differs from the one this SDK was built for.
pub unsafe fn set_host(host: *const Host) -> bool {
    if host.is_null() || (*host).abi_version != abi::ABI_VERSION {
        return false;
    }
    let _ = HOST.set(HostRef(&*host));
    true
}

/// The loader's host table. Panics if called before `jh_core_init` (never in plugin code that runs
/// after init).
pub fn host() -> &'static Host {
    HOST.get().expect("host not set: jh_core_init has not run").0
}

/// Main-thread execution, provided by the loader.
pub mod pump {
    use super::*;

    type Job = Box<dyn FnOnce() + Send>;

    unsafe extern "C" fn trampoline(ctx: *mut c_void) {
        let job = Box::from_raw(ctx as *mut Job);
        job();
    }

    /// Runs `f` on Unity's main thread, blocks until it has run, and returns its result.
    /// Every Unity/IL2CPP call belongs in here (or in a hook callback, which is already on the
    /// main thread). Errors if the main thread didn't pick the job up in time (game minimised or
    /// loading) or if `f` panicked.
    pub fn run<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> Result<T, String> {
        let host = HOST.get().ok_or("plugin not initialised")?.0;
        let slot = std::sync::Arc::new(std::sync::Mutex::new(None));
        let out = slot.clone();
        let job: Job = Box::new(move || {
            let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(f));
            *out.lock().unwrap() = Some(r);
        });
        let ctx = Box::into_raw(Box::new(job));
        if unsafe { (host.run_main)(trampoline, ctx as *mut c_void) } != 0 {
            // Never ran: reclaim the closure.
            unsafe { drop(Box::from_raw(ctx)) };
            return Err("timed out waiting for the game's main thread (is the game minimised or loading?)".into());
        }
        let r = slot.lock().unwrap().take();
        match r {
            Some(Ok(v)) => Ok(v),
            Some(Err(_)) => Err("internal error (panic) while running on the main thread".into()),
            None => Err("job did not run".into()),
        }
    }
}

/// Calls `cb(this, arg0)` before every call of the instance method `class.method`.
///
/// Limits (from the loader): the method must return void, take at most two parameters, and none of
/// them may be a float (the detour forwards the four integer argument registers). There are 12
/// hook slots in total, and up to 3 plugins may hook the same method. Call this from
/// `Plugin::init`; the callback runs on the game's main thread and is dropped when the plugin unloads.
pub unsafe fn pre_hook_method(image: &str, namespace: &str, class: &str, method: &str, argc: i32, cb: PreHookFn) -> Result<(), String> {
    let k = il2cpp::class(image, namespace, class).ok_or_else(|| format!("class {namespace}.{class} not found in {image}"))?;
    let m = il2cpp::method(k, method, argc).ok_or_else(|| format!("{class}.{method}/{argc} not found"))?;
    match (host().pre_hook)(m as *const c_void, cb) {
        0 => Ok(()),
        rc => {
            log::info(format!("hook {class}.{method} failed ({rc})"));
            Err(format!("hook {class}.{method} failed ({rc}: 1 null method, 2 no free slot, 3 enable failed, 4 detour failed, 5 target shared by too many plugins)"))
        }
    }
}

/// A loaded plugin, as listed by the loader.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoadedPlugin {
    /// Owner id: pass to [`call_plugin`].
    pub id: u32,
    /// DLL file name without extension (`jibbers_sdk` for `jibbers_sdk.dll`).
    pub file: String,
    /// True for the developer core.
    pub developer: bool,
}

unsafe fn take(p: *mut u8, n: usize) -> Option<Value> {
    if p.is_null() {
        return None;
    }
    let v = serde_json::from_slice(std::slice::from_raw_parts(p, n)).ok();
    (host().free_buf)(p, n);
    v
}

/// Every plugin the loader has loaded.
pub fn list_plugins() -> Vec<LoadedPlugin> {
    unsafe {
        let (mut p, mut n) = (std::ptr::null_mut(), 0usize);
        (host().list_plugins)(&mut p, &mut n);
        let list = take(p, n).unwrap_or(Value::Null);
        list.as_array()
            .into_iter()
            .flatten()
            .filter_map(|e| {
                Some(LoadedPlugin {
                    id: e.get("id")?.as_u64()? as u32,
                    file: e.get("file")?.as_str()?.to_string(),
                    developer: e.get("developer").and_then(Value::as_bool).unwrap_or(false),
                })
            })
            .collect()
    }
}

/// Sends one JSON request to the plugin with owner id `id` and returns its JSON reply
/// (`None` if there is no such plugin or it answered with something that isn't JSON).
pub fn call_plugin(id: u32, req: &Value) -> Option<Value> {
    unsafe {
        let s = req.to_string();
        let (mut p, mut n) = (std::ptr::null_mut(), 0usize);
        if (host().call_plugin)(id, s.as_ptr(), s.len(), &mut p, &mut n) != 0 {
            return None;
        }
        take(p, n)
    }
}

/// Like [`call_plugin`], finding the plugin by DLL file name without extension.
pub fn call_plugin_by_file(file: &str, req: &Value) -> Option<Value> {
    let id = list_plugins().into_iter().find(|p| p.file.eq_ignore_ascii_case(file))?.id;
    call_plugin(id, req)
}
