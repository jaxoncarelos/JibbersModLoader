//! Loads the hotload cores and swaps the developer core on request.
//!
//! Two kinds of core are loaded, each from a temp copy so the files in the game folder can be
//! overwritten while the game runs:
//! - the developer core `jibbers_hotload_core.dll` in the game folder, if it exists. `reload-core`
//!   swaps it for the file's current contents.
//! - plugins: every `*.dll` in `JibbersMods\` that exports the core ABI (the atmosphere, Reshred).
//!   They are loaded once, when the loader attaches. A plugin that fails to start is skipped.
//!
//! A third kind is registered, not loaded: a plugin DLL that hosts other plugins (the .NET bridge,
//! one entry per C# mod) calls the `jh_register_plugin` export from its `jh_core_init`. Each
//! registered plugin is listed, called and shown in the Mods tab like a DLL plugin.
//!
//! A request goes to the developer core first, then the plugins in file-name order (see `handle`).

use crate::abi::{self, CoreFree, CoreHandle, CoreInit, Host};
use crate::{game_dir, hooks, log, pump};
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::ffi::c_void;
use std::sync::{Mutex, RwLock};
use windows_sys::Win32::Foundation::{FreeLibrary, HMODULE};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

pub const CORE_FILE: &str = "jibbers_hotload_core.dll";
/// Folder in the game folder whose DLLs are loaded as plugins.
const PLUGIN_DIR: &str = "JibbersMods";

/// A plugin that never hooks the game and so may load even on a game build that isn't validated:
/// the SDK runtime. Its updater is how a newer compatibility list reaches the player.
const RUNTIME_PLUGIN: &str = "jibbers_sdk";

/// Why a core failed to load. `permanent` failures (wrong ABI, missing exports) come from the files
/// themselves, so retrying cannot help and only repeats the log line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoadError {
    pub msg: String,
    pub permanent: bool,
}

impl LoadError {
    fn retry(msg: impl Into<String>) -> LoadError {
        LoadError { msg: msg.into(), permanent: false }
    }
    fn permanent(msg: impl Into<String>) -> LoadError {
        LoadError { msg: msg.into(), permanent: true }
    }
}

/// Checks what `jh_core_init` returned. A core built for another ABI answers 0 (it refuses, and
/// logs why) or its own version; either way the install is mixed and must not be retried.
fn check_init_result(v: u32) -> Result<(), LoadError> {
    match v {
        v if v == abi::ABI_VERSION => Ok(()),
        0 => Err(LoadError::permanent(format!("core declined to start (loader ABI {}; usually a loader/plugin version mismatch, see the line it logged)", abi::ABI_VERSION))),
        v => Err(LoadError::permanent(format!("core ABI {v} doesn't match loader ABI {}: reinstall the mod", abi::ABI_VERSION))),
    }
}

static HOST: Host = Host {
    abi_version: abi::ABI_VERSION,
    run_main: pump::run_main,
    pre_hook: hooks::pre_hook,
    list_plugins,
    call_plugin,
    free_buf,
};

/// Borrow the existing host table; do not create a second plugin registry.
#[cfg(feature = "release")]
pub fn host() -> &'static Host {
    &HOST
}

/// Request handler of a registered plugin: `ctx` is the value given at registration.
pub type CtxHandle = unsafe extern "C" fn(ctx: *mut c_void, req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize);
pub type CtxFree = unsafe extern "C" fn(ctx: *mut c_void, p: *mut u8, len: usize);
pub type CtxUnload = unsafe extern "C" fn(ctx: *mut c_void);

/// How the loader talks to a core.
#[derive(Clone, Copy)]
enum Calls {
    /// A DLL with the core exports.
    Dll { handle: CoreHandle, free: CoreFree, unload: Option<abi::CoreUnload> },
    /// A plugin registered through `jh_register_plugin`.
    Registered { ctx: usize, handle: CtxHandle, free: CtxFree, unload: Option<CtxUnload> },
}

struct Core {
    /// File name, for the log.
    name: String,
    /// Owner id: tags this core's hook callbacks (see `hooks::set_owner`).
    id: u32,
    /// True for `jibbers_hotload_core.dll`, the core `reload-core` swaps.
    developer: bool,
    /// Null for a registered plugin (its host DLL owns the code).
    module: HMODULE,
    /// The optional unload (`jh_core_unload`) lets the core take down what it put into the game
    /// (UI objects whose callbacks point into its code) before the DLL is freed.
    calls: Calls,
    copy: PathBuf,
}
unsafe impl Send for Core {}
unsafe impl Sync for Core {}

/// Loaded cores: the developer core (when present) first, then the plugins.
static CORES: RwLock<Vec<Core>> = RwLock::new(Vec::new());
/// Counts the copies made, so every load gets a new file name and owner id.
static GENERATION: AtomicU32 = AtomicU32::new(0);
/// Set once the JibbersMods folder has been read, so plugins load only once.
static PLUGINS_SCANNED: AtomicBool = AtomicBool::new(false);

fn temp_dir() -> PathBuf {
    std::env::temp_dir().join("jibbers-hotload")
}

/// Copies `src` to temp and loads it. Checks the exports and the ABI version, then runs the core's
/// `jh_core_init` with its hook callbacks tagged with the new owner id.
unsafe fn load_from(src: &Path, developer: bool) -> Result<Core, LoadError> {
    let dir = temp_dir();
    let _ = std::fs::create_dir_all(&dir);
    let n = GENERATION.fetch_add(1, Ordering::SeqCst);
    let copy = dir.join(format!("core-{}-{n}.dll", std::process::id()));
    std::fs::copy(src, &copy).map_err(|e| LoadError::retry(format!("copying {}: {e}", src.display())))?;
    let name = src.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    let wide: Vec<u16> = copy.as_os_str().to_string_lossy().encode_utf16().chain(Some(0)).collect();
    let module = LoadLibraryW(wide.as_ptr());
    if module.is_null() {
        let _ = std::fs::remove_file(&copy);
        return Err(LoadError::retry(format!("LoadLibrary {} failed", copy.display())));
    }
    let sym = |name: &[u8]| GetProcAddress(module, name.as_ptr());
    let (Some(init), Some(handle), Some(free)) = (sym(b"jh_core_init\0"), sym(b"jh_core_handle\0"), sym(b"jh_core_free\0")) else {
        FreeLibrary(module);
        let _ = std::fs::remove_file(&copy);
        return Err(LoadError::permanent("core DLL is missing exports"));
    };
    let init: CoreInit = std::mem::transmute(init);
    let id = n + 1;
    hooks::set_owner(id);
    let v = init(&HOST);
    hooks::set_owner(0);
    if let Err(e) = check_init_result(v) {
        hooks::clear_callbacks(id);
        FreeLibrary(module);
        let _ = std::fs::remove_file(&copy);
        return Err(e);
    }
    let unload = sym(b"jh_core_unload\0").map(|f| std::mem::transmute::<_, abi::CoreUnload>(f));
    Ok(Core { name, id, developer, module, calls: Calls::Dll { handle: std::mem::transmute(handle), free: std::mem::transmute(free), unload }, copy })
}

/// Plugins registered while the cores are write-locked (from a host plugin's `jh_core_init`);
/// moved into `CORES` right after that plugin has loaded.
static PENDING: Mutex<Vec<Core>> = Mutex::new(Vec::new());

/// Registers a plugin hosted by another plugin (one C# mod of the .NET bridge). Call it from your
/// `jh_core_init`; `name` (UTF-8) is shown as the plugin's file. Returns its owner id, or 0 if
/// `name` is empty or not UTF-8. The host DLL must stay loaded while the plugin is registered:
/// the loader calls `unload(ctx)` before it frees the host.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn jh_register_plugin(name: *const u8, name_len: usize, ctx: *mut c_void, handle: CtxHandle, free: CtxFree, unload: Option<CtxUnload>) -> u32 {
    if name.is_null() || name_len == 0 {
        return 0;
    }
    let Ok(name) = std::str::from_utf8(std::slice::from_raw_parts(name, name_len)) else { return 0 };
    let id = GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    let core = Core { name: format!("{name}.dll"), id, developer: false, module: std::ptr::null_mut(), calls: Calls::Registered { ctx: ctx as usize, handle, free, unload }, copy: PathBuf::new() };
    PENDING.lock().unwrap().push(core);
    id
}

/// Moves registered plugins into the core list.
fn take_pending(cores: &mut Vec<Core>) {
    for c in PENDING.lock().unwrap().drain(..) {
        log::info(format!("plugin {} registered", c.name));
        cores.push(c);
    }
}

/// Drops a core's hook callbacks and unloads it.
fn unload(core: Core) {
    match core.calls {
        Calls::Dll { unload: Some(f), .. } => unsafe { f() },
        Calls::Registered { ctx, unload: Some(f), .. } => unsafe { f(ctx as *mut c_void) },
        _ => {}
    }
    hooks::clear_callbacks(core.id);
    if !core.module.is_null() {
        unsafe { FreeLibrary(core.module) };
        let _ = std::fs::remove_file(&core.copy);
    }
}

/// Swaps the developer core for the file in the game folder. The old core is unloaded first, so a
/// failed load leaves no developer core behind.
fn swap_developer(cores: &mut Vec<Core>) -> Result<(), LoadError> {
    let src = game_dir().join(CORE_FILE);
    if let Some(i) = cores.iter().position(|c| c.developer) {
        unload(cores.remove(i));
    }
    let core = unsafe { load_from(&src, true) }?;
    cores.insert(0, core);
    log::info("core loaded");
    Ok(())
}

/// Loads every plugin in JibbersMods (or, with `only`, just the one with that file stem), the
/// first time only. Plugins that fail are logged once and skipped; nothing is retried.
fn load_plugins(cores: &mut Vec<Core>, only: Option<&str>) {
    if PLUGINS_SCANNED.swap(true, Ordering::SeqCst) {
        return;
    }
    // Create the folder on first run so players know where to drop plugin DLLs.
    let dir = game_dir().join(PLUGIN_DIR);
    let _ = std::fs::create_dir_all(&dir);
    let Ok(rd) = std::fs::read_dir(&dir) else { return };
    let mut files: Vec<PathBuf> = rd
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll")))
        .filter(|p| only.is_none_or(|stem| p.file_stem().is_some_and(|s| s.eq_ignore_ascii_case(stem))))
        .collect();
    if only.is_none() {
        match crate::managed_runtime::prepare(&game_dir()) {
            Ok(Some(bridge)) => {
                // The signed channel bridge takes precedence over a manually installed copy.
                files.retain(|file| !file.file_stem().is_some_and(|name| name.eq_ignore_ascii_case("jibbers_dotnet")));
                files.push(bridge);
            }
            Ok(None) => {},
            Err(error) => log::info(format!("C# runtime was not prepared: {error}")),
        }
    }
    files.sort();
    for f in files {
        if std::fs::read(&f).is_ok_and(|bytes| crate::managed_runtime::is_managed(&bytes)) { continue; }
        match unsafe { load_from(&f, false) } {
            Ok(c) => {
                log::info(format!("plugin {} loaded", c.name));
                cores.push(c);
            }
            Err(e) => log::info(format!("plugin {}: {}; skipped", f.display(), e.msg)),
        }
        take_pending(cores);
    }
}

/// `reload-core`: swaps the developer core (an error if its file is missing or fails to load) and
/// loads the plugins if they aren't loaded yet.
pub fn reload() -> Result<(), String> {
    let mut cores = CORES.write().unwrap();
    swap_developer(&mut cores).map_err(|e| e.msg)?;
    load_plugins(&mut cores, None);
    Ok(())
}

/// Called once when the loader attaches: loads the developer core if its file is in the game
/// folder, then the plugins. A developer core that fails transiently is an error (the attach is
/// retried); see `attach_outcome` for the rest.
pub fn attach() -> Result<(), String> {
    let mut cores = CORES.write().unwrap();
    let dev = if game_dir().join(CORE_FILE).exists() { swap_developer(&mut cores) } else { Ok(()) };
    load_plugins(&mut cores, None);
    attach_outcome(dev, cfg!(feature = "release"))
}

/// What `attach` reports for the developer core's result. A mixed install (wrong ABI, missing
/// exports) is logged once and never retried; in players' builds there is no developer client to retry for, so
/// a stale developer core must not block the plugins either. Only a transient failure in a
/// developer build is returned, so the attach is tried again.
fn attach_outcome(dev: Result<(), LoadError>, release: bool) -> Result<(), String> {
    match dev {
        Ok(()) => Ok(()),
        Err(e) if e.permanent || release => {
            log::info(format!("developer core skipped: {}{}", e.msg, if e.permanent { " (not retrying)" } else { "" }));
            Ok(())
        }
        Err(e) => Err(e.msg),
    }
}

/// Used when the game build is not validated (see `compat`): loads only the SDK runtime, so its
/// updater can still fetch a newer compatibility list. No developer core, no other plugin, and no
/// frame hooks are involved.
pub fn attach_runtime_only() {
    let mut cores = CORES.write().unwrap();
    load_plugins(&mut cores, Some(RUNTIME_PLUGIN));
}

/// Read guard on the cores, unless they are being swapped right now.
pub fn try_read() -> Option<impl Drop> {
    CORES.try_read().ok()
}

pub fn loaded() -> bool {
    !CORES.read().unwrap().is_empty()
}

/// Forwards one JSON request to the cores. The first reply with `"ok": true` wins. If none has it,
/// the developer core's reply is returned if there is one, else the last reply.
pub fn handle(req: &[u8]) -> Result<Vec<u8>, String> {
    let cores = CORES.read().unwrap();
    if cores.is_empty() {
        return Err("hotload core isn't loaded (see jibbers_hotload.log in the game folder)".into());
    }
    let mut dev = None;
    let mut last = Vec::new();
    for c in cores.iter() {
        let reply = unsafe { call(c, req) };
        if is_ok(&reply) {
            return Ok(reply);
        }
        if c.developer {
            dev = Some(reply.clone());
        }
        last = reply;
    }
    Ok(dev.unwrap_or(last))
}

unsafe fn call(core: &Core, req: &[u8]) -> Vec<u8> {
    let mut out: *mut u8 = std::ptr::null_mut();
    let mut len = 0usize;
    match core.calls {
        Calls::Dll { handle, .. } => handle(req.as_ptr(), req.len(), &mut out, &mut len),
        Calls::Registered { ctx, handle, .. } => handle(ctx as *mut c_void, req.as_ptr(), req.len(), &mut out, &mut len),
    }
    if out.is_null() {
        return br#"{"ok":false,"error":"plugin returned no reply"}"#.to_vec();
    }
    let v = std::slice::from_raw_parts(out, len).to_vec();
    match core.calls {
        Calls::Dll { free, .. } => free(out, len),
        Calls::Registered { ctx, free, .. } => free(ctx as *mut c_void, out, len),
    }
    v
}

fn is_ok(reply: &[u8]) -> bool {
    serde_json::from_slice::<Value>(reply).ok().and_then(|v| v.get("ok").and_then(Value::as_bool)) == Some(true)
}

/// Hands `bytes` to a core as a buffer that `free_buf` frees (the same way `jh_core_handle` does).
unsafe fn give(bytes: Vec<u8>, out: *mut *mut u8, out_len: *mut usize) {
    let mut s = bytes.into_boxed_slice();
    *out_len = s.len();
    *out = s.as_mut_ptr();
    std::mem::forget(s);
}

/// Host function: JSON array of the loaded cores (see `abi::Host::list_plugins`).
unsafe extern "C" fn list_plugins(out: *mut *mut u8, out_len: *mut usize) {
    let cores = CORES.read().unwrap();
    let list: Vec<Value> = cores
        .iter()
        .map(|c| {
            let file = Path::new(&c.name).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            serde_json::json!({"id": c.id, "file": file, "developer": c.developer, "registered": matches!(c.calls, Calls::Registered { .. })})
        })
        .collect();
    give(Value::Array(list).to_string().into_bytes(), out, out_len);
}

/// Host function: forwards a request to the core with owner id `id` (see `abi::Host::call_plugin`).
unsafe extern "C" fn call_plugin(id: u32, req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize) -> i32 {
    *out = std::ptr::null_mut();
    *out_len = 0;
    let req: &[u8] = if req.is_null() { &[] } else { std::slice::from_raw_parts(req, len) };
    let cores = CORES.read().unwrap();
    let Some(c) = cores.iter().find(|c| c.id == id) else { return 1 };
    give(call(c, req), out, out_len);
    0
}

/// Host function: frees a buffer from `list_plugins` or `call_plugin`.
unsafe extern "C" fn free_buf(p: *mut u8, len: usize) {
    if !p.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(p, len)));
    }
}

/// Removes temp copies left by earlier runs.
pub fn clean_temp() {
    if let Ok(rd) = std::fs::read_dir(temp_dir()) {
        for e in rd.flatten() {
            let _ = std::fs::remove_file(e.path()); // in-use copies just fail to delete
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matching_abi_is_accepted() {
        assert_eq!(check_init_result(abi::ABI_VERSION), Ok(()));
    }

    #[test]
    fn abi_mismatch_is_permanent_with_a_clear_message() {
        let old = check_init_result(abi::ABI_VERSION + 1).unwrap_err();
        assert!(old.permanent);
        assert!(old.msg.contains("doesn't match loader ABI"), "{}", old.msg);
        let declined = check_init_result(0).unwrap_err();
        assert!(declined.permanent);
        assert!(declined.msg.contains("declined to start"), "{}", declined.msg);
    }

    #[test]
    fn a_permanent_developer_core_failure_is_logged_once_not_retried() {
        let e = LoadError::permanent("core DLL is missing exports");
        assert_eq!(attach_outcome(Err(e.clone()), false), Ok(()));
        assert_eq!(attach_outcome(Err(e), true), Ok(()));
    }

    #[test]
    fn a_transient_failure_retries_only_in_developer_builds() {
        let e = LoadError::retry("LoadLibrary failed");
        assert_eq!(attach_outcome(Err(e.clone()), false), Err("LoadLibrary failed".to_string()));
        assert_eq!(attach_outcome(Err(e), true), Ok(()));
        assert_eq!(attach_outcome(Ok(()), false), Ok(()));
    }
}
