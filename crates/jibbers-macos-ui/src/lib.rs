//! Reuses the actual Mods menu and symbol resolver; the managed Mac host owns the ABI table.
#![cfg(target_os = "macos")]
#![allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/abi.rs"] mod abi;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/il2cpp.rs"] mod il2cpp;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/log.rs"] mod log;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/unity.rs"] mod unity;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/inspect.rs"] mod inspect;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/shape.rs"] mod shape;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/symbols.rs"] mod symbols;
#[path = "../../jibbers-hotload/src/host_shim.rs"] mod host;
#[path = "../../jibbers-hotload/src/modmenu.rs"] mod modmenu;
use std::{ffi::{c_char, c_void, CStr}, path::PathBuf, sync::OnceLock};
static HOST: OnceLock<usize> = OnceLock::new();
static ROOT: OnceLock<PathBuf> = OnceLock::new();
pub fn host() -> &'static abi::Host { unsafe { &*(*HOST.get().expect("Mac host missing") as *const abi::Host) } }
pub fn game_dir() -> PathBuf { ROOT.get().cloned().unwrap_or_default() }
mod gamebuild {
    pub struct Fingerprint;
    impl Fingerprint { pub fn describe(&self) -> String { "macOS preview; managed metadata guard passed".into() } }
    pub fn fingerprint_loaded() -> Result<Fingerprint, String> { Ok(Fingerprint) }
}
mod probe { pub unsafe fn describe(_: *mut crate::il2cpp::Object, _: usize) -> serde_json::Value { serde_json::Value::Null } }
mod pump {
    use super::*;
    pub fn run<F, T>(f: F) -> Result<T, String> where F: FnOnce() -> T + Send + 'static, T: Send + 'static {
        type Closure = Box<dyn FnOnce() + Send>;
        unsafe extern "C" fn invoke(ctx: *mut c_void) { let job = Box::from_raw(ctx as *mut Closure); job(); }
        let slot = std::sync::Arc::new(std::sync::Mutex::new(None));
        let out = slot.clone();
        let job: Closure = Box::new(move || { *out.lock().unwrap() = Some(std::panic::catch_unwind(std::panic::AssertUnwindSafe(f))); });
        let ctx = Box::into_raw(Box::new(job));
        if unsafe { (host().run_main)(invoke, ctx as _) } != 0 {
            unsafe { drop(Box::from_raw(ctx)); }
            return Err("Mac main-thread job timed out".into());
        }
        let result = slot.lock().unwrap().take().ok_or("Mac main-thread result missing")?;
        result.map_err(|_| "Mac main-thread job panicked".into())
    }
}
/// Invoked on Unity's main thread after the Mac host has validated metadata and loaded the mods.
#[no_mangle]
pub unsafe extern "C" fn jibbers_macos_ui_init(table: *const abi::Host, root: *const c_char) -> i32 {
    std::panic::catch_unwind(|| {
        if table.is_null() || root.is_null() || (*table).abi_version != abi::ABI_VERSION { return 1; }
        HOST.set(table as usize).ok(); ROOT.set(PathBuf::from(CStr::from_ptr(root).to_string_lossy().into_owned())).ok();
        match il2cpp::init() { Ok(()) => { modmenu::watch(); 0 }, Err(e) => { log::info(e); 2 } }
    }).unwrap_or(3)
}
