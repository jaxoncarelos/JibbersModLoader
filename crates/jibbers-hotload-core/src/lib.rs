//! Jibbers hotload core: everything that talks to the game. Loaded by the version.dll loader
//! (crates/jibbers-hotload), which can unload and replace it while the game keeps running.
//!
//! Requests (one JSON object each):
//! - `{"cmd":"status"}`: scene, loaded terrains, physics state, camera position
//! - `{"cmd":"sample","x":…,"z":…}`: terrain height under a world position
//! - developer probes: `dump`, `inspect`, `objects`, `call` (see `probe`)
//! - plugin protocol for the Mods menu: `plugin.info`, `plugin.get`, `plugin.set`, `plugin.enable`
//!   (see `plugin`)
//!
//! With the `reshred-only` feature only `reshred` and the plugin protocol are dispatched. With the
//! `atmosphere-only` feature only `atmo` is dispatched; the rest of the code stays compiled
//! but is unreachable, so its dead-code and unused-import lints are silenced for that build.

#![cfg_attr(any(feature = "atmosphere-only", feature = "reshred-only"), allow(dead_code, unused_imports))]

// abi, il2cpp, unity and log live in the SDK crate (sdk/crates/jibbers-mod-sdk), shared by path.
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/abi.rs"]
mod abi;
mod atmosphere;
mod sparkle_mask;
mod chatcmd;
mod dump;
mod game;
mod grading;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/il2cpp.rs"]
mod il2cpp;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/log.rs"]
mod log;
mod maphook;
mod modmenu;
mod plugin;
mod probe;
mod reshred;
mod reshred_ground;
mod reshred_input;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/unity.rs"]
mod unity;
// Stable names for obfuscated game members (sdk/docs/SYMBOLS.md), shared with the SDK by path.
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/gamebuild.rs"]
mod gamebuild;
#[allow(dead_code)]
#[path = "host_shim.rs"]
mod host;
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/inspect.rs"]
mod inspect;
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/shape.rs"]
mod shape;
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/symbols.rs"]
mod symbols;

use serde_json::{json, Value};
use std::ffi::c_void;
use std::path::PathBuf;
use std::sync::OnceLock;
use windows_sys::Win32::System::LibraryLoader::GetModuleFileNameW;

struct HostRef(&'static abi::Host);
unsafe impl Sync for HostRef {}
unsafe impl Send for HostRef {}
static HOST: OnceLock<HostRef> = OnceLock::new();

/// The loader's host functions (set once in `jh_core_init`).
pub fn host() -> &'static abi::Host {
    HOST.get().expect("host not set").0
}

/// Folder containing the game executable.
pub fn game_dir() -> PathBuf {
    let mut buf = [0u16; 1024];
    let n = unsafe { GetModuleFileNameW(std::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32) } as usize;
    let exe = PathBuf::from(String::from_utf16_lossy(&buf[..n]));
    exe.parent().map(|p| p.to_path_buf()).unwrap_or_default()
}

/// Main-thread execution, provided by the loader.
pub mod pump {
    use super::*;

    type Job = Box<dyn FnOnce() + Send>;

    unsafe extern "C" fn trampoline(ctx: *mut c_void) {
        let job = Box::from_raw(ctx as *mut Job);
        job();
    }

    /// Runs `f` on Unity's main thread and returns its result.
    pub fn run<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> Result<T, String> {
        let host = HOST.get().ok_or("core not initialised")?.0;
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

fn dispatch(req: &Value) -> Result<Value, String> {
    let cmd = req.get("cmd").and_then(Value::as_str).unwrap_or("");
    #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
    let r2 = req.clone();
    match cmd {
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "status" => pump::run(game::status)?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "sample" => {
            let x = req.get("x").and_then(Value::as_f64).ok_or("sample needs x")? as f32;
            let z = req.get("z").and_then(Value::as_f64).ok_or("sample needs z")? as f32;
            pump::run(move || game::sample(x, z))?
        }
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "dump" => {
            let path = game_dir().join("jibbers_hotload_api.txt");
            let p = path.clone();
            pump::run(move || unsafe { dump::run(&p) })?;
            Ok(json!({"ok": true, "path": path}))
        }
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "inspect" => pump::run(move || probe::inspect(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "objects" => pump::run(move || probe::objects(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "call" => pump::run(move || probe::call(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "materials" => pump::run(probe::materials)?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "fieldsoftype" => pump::run(move || probe::fieldsoftype(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "hierarchy" => pump::run(move || probe::hierarchy(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "findstatic" => pump::run(move || probe::findstatic(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "shaderprops" => pump::run(probe::shaderprops)?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "texpixel" => pump::run(move || probe::texpixel(&r2))?,
        #[cfg(not(feature = "reshred-only"))]
        "atmo" => atmosphere::command(req),
        // plugin protocol for the Mods menu: every build answers for its own plugin
        #[cfg(feature = "reshred-only")]
        other if other.starts_with("plugin.") => reshred::plugin(req),
        #[cfg(not(feature = "reshred-only"))]
        other if other.starts_with("plugin.") => plugin::handle(req),
        #[cfg(feature = "reshred-only")]
        "reshred" => reshred::command(req),
        // chat-command probes (developer build only)
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "chatcmd" => pump::run(move || chatcmd::test(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "modmenu" => pump::run(move || modmenu::test(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "screenshot" => pump::run(move || atmosphere::screenshot(&r2))?,
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        "classinfo" => pump::run(move || probe::classinfo(&r2))?,
        #[cfg(any(feature = "atmosphere-only", feature = "reshred-only"))]
        other => Err(format!("{other:?} is not available in this build")),
        #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
        other => Err(format!("unknown command {other:?}")),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn jh_core_init(host: *const abi::Host) -> u32 {
    // A panic must not unwind out of an extern "C" fn (that aborts the game): report failure instead.
    std::panic::catch_unwind(|| init(host)).unwrap_or_else(|_| {
        log::info("core: panicked during init; giving up");
        0
    })
}

unsafe fn init(host: *const abi::Host) -> u32 {
    if host.is_null() || (*host).abi_version != abi::ABI_VERSION {
        return 0;
    }
    let _ = HOST.set(HostRef(&*host));
    if let Err(e) = il2cpp::init() {
        log::info(format!("core: {e}"));
        return 0;
    }
    #[cfg(feature = "reshred-only")]
    reshred::install(&*host);
    #[cfg(not(feature = "reshred-only"))]
    {
        maphook::install_hooks(&*host);
        atmosphere::install(&*host);
    }
    // The Mods tab in Options: built once the main menu exists and rebuilt whenever the menu is
    // recreated (returning from a server).
    #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
    modmenu::watch();
    log::info(format!("core {} loaded", env!("CARGO_PKG_VERSION")));
    abi::ABI_VERSION
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn jh_core_handle(req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize) {
    let bytes = std::slice::from_raw_parts(req, len);
    let reply = match serde_json::from_slice::<Value>(bytes) {
        Ok(req) => std::panic::catch_unwind(|| dispatch(&req))
            .unwrap_or_else(|_| Err("internal error (panic)".into()))
            .unwrap_or_else(|e| json!({"ok": false, "error": e})),
        Err(e) => json!({"ok": false, "error": format!("bad request: {e}")}),
    };
    let mut s = reply.to_string().into_bytes().into_boxed_slice();
    *out_len = s.len();
    *out = s.as_mut_ptr();
    std::mem::forget(s);
}

/// Called by the loader just before this core is unloaded (developer `reload-core`).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn jh_core_unload() {
    let _ = std::panic::catch_unwind(|| unload());
}

unsafe fn unload() {
    #[cfg(not(feature = "reshred-only"))]
    let _ = pump::run(|| sparkle_mask::teardown());
    #[cfg(not(any(feature = "atmosphere-only", feature = "reshred-only")))]
    {
        modmenu::stop();
        let _ = pump::run(modmenu::teardown);
    }
    #[cfg(feature = "reshred-only")]
    let _ = pump::run(|| reshred::teardown());
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn jh_core_free(p: *mut u8, len: usize) {
    if !p.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(p, len)));
    }
}
