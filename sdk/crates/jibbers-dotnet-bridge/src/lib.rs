//! The .NET bridge (`JibbersMods\jibbers_dotnet.dll`): starts the .NET runtime inside the game and
//! hands control to the C# SDK (`JibbersMods\dotnet\Jibbers.Sdk.dll`), which loads every C# mod in
//! `JibbersMods\cs\`.
//!
//! To the loader this is an ordinary plugin. During its `jh_core_init` it:
//! 1. finds a .NET 8+ runtime: the copy shipped in `JibbersMods\dotnet\runtime\`, else the one
//!    installed on the machine (`DOTNET_ROOT`, then `%ProgramFiles%\dotnet`);
//! 2. loads `Jibbers.Sdk.dll` and calls `Jibbers.Sdk.Bootstrap.Init(NativeApi*)`, which loads the
//!    mods and registers each one with the loader (`jh_register_plugin`), so every C# mod has its
//!    own section in the Mods tab and its own settings file;
//! 3. stays loaded for the life of the game.
//!
//! [`NativeApi`] is everything managed code cannot do by itself: the loader's host table (main
//! thread, method pre-hooks, other plugins), registration with the loader, raw detours of any
//! native function, and the log. Everything else (IL2CPP calls, Unity, settings, input) is done in
//! C# directly against `GameAssembly.dll` and Win32.

pub mod detour;
pub mod hostfxr;

use jibbers_mod_sdk::abi::Host;
use jibbers_mod_sdk::plugin::{Info, Plugin};
use jibbers_mod_sdk::{declare_plugin, host, json, log, Setting, Value};
use std::ffi::c_void;
use std::path::PathBuf;
use std::sync::Mutex;

/// Version of [`NativeApi`]. The C# SDK refuses a table older than the one it was built for.
pub const NATIVE_API_VERSION: u32 = 1;

pub type RegisterFn = unsafe extern "C" fn(name: *const u8, name_len: usize, ctx: *mut c_void, handle: *const c_void, free: *const c_void, unload: *const c_void) -> u32;

/// Function table handed to `Jibbers.Sdk.Bootstrap.Init`. Field order is the C# side's
/// `NativeApi` struct: append only.
#[repr(C)]
pub struct NativeApi {
    pub version: u32,
    pub host: *const Host,
    /// `log(utf8, len)`: one line in `jibbers_hotload.log`.
    pub log: unsafe extern "C" fn(*const u8, usize),
    /// The loader's `jh_register_plugin`, or null with an old loader.
    pub register_plugin: Option<RegisterFn>,
    /// `detour_create(target, replacement, &trampoline) -> handle` (null on failure: see `last_error`).
    pub detour_create: unsafe extern "C" fn(*const c_void, *const c_void, *mut *const c_void) -> *mut c_void,
    pub detour_enable: unsafe extern "C" fn(*mut c_void) -> i32,
    pub detour_disable: unsafe extern "C" fn(*mut c_void) -> i32,
    /// Disables and frees a detour.
    pub detour_destroy: unsafe extern "C" fn(*mut c_void),
    /// `last_error(buf, cap) -> len`: the calling thread's last bridge error (UTF-8).
    pub last_error: unsafe extern "C" fn(*mut u8, usize) -> usize,
    /// Game folder, `JibbersMods` and the game build fingerprint, UTF-8 and NUL-terminated.
    pub game_dir: *const u8,
    pub mods_dir: *const u8,
    pub game_build: *const u8,
}

unsafe extern "C" fn log_line(p: *const u8, n: usize) {
    if !p.is_null() {
        log::info(String::from_utf8_lossy(std::slice::from_raw_parts(p, n)).into_owned());
    }
}

thread_local! {
    static LAST_ERROR: std::cell::RefCell<String> = const { std::cell::RefCell::new(String::new()) };
}

pub fn set_error(e: impl Into<String>) {
    LAST_ERROR.with(|l| *l.borrow_mut() = e.into());
}

unsafe extern "C" fn last_error(buf: *mut u8, cap: usize) -> usize {
    LAST_ERROR.with(|l| {
        let s = l.borrow();
        let n = s.len().min(cap);
        if !buf.is_null() {
            std::ptr::copy_nonoverlapping(s.as_ptr(), buf, n);
        }
        s.len()
    })
}

/// The loader's `jh_register_plugin`: exported by `version.dll` in the game folder (not the system
/// `version.dll` it forwards to).
fn loader_register() -> Option<RegisterFn> {
    use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
    let path = jibbers_mod_sdk::game_dir().join("version.dll");
    let wide: Vec<u16> = path.as_os_str().to_string_lossy().encode_utf16().chain(Some(0)).collect();
    unsafe {
        let m = GetModuleHandleW(wide.as_ptr());
        if m.is_null() {
            return None;
        }
        GetProcAddress(m, c"jh_register_plugin".as_ptr() as _).map(|f| std::mem::transmute::<_, RegisterFn>(f))
    }
}

fn cstring(s: String) -> *const u8 {
    let mut b = s.into_bytes();
    b.push(0);
    Box::leak(b.into_boxed_slice()).as_ptr()
}

/// Where the SDK and the mods live.
pub struct Layout {
    pub sdk: PathBuf,
    pub runtime_config: PathBuf,
    pub mods: PathBuf,
    pub bundled_runtime: PathBuf,
}

pub fn layout(mods_dir: &std::path::Path) -> Layout {
    let dotnet = mods_dir.join("dotnet");
    Layout {
        sdk: dotnet.join("Jibbers.Sdk.dll"),
        runtime_config: dotnet.join("Jibbers.Sdk.runtimeconfig.json"),
        mods: mods_dir.join("cs"),
        bundled_runtime: dotnet.join("runtime"),
    }
}

static STATUS: Mutex<String> = Mutex::new(String::new());

fn status(s: impl Into<String>) {
    let s = s.into();
    log::info(format!("dotnet: {s}"));
    *STATUS.lock().unwrap() = s;
}

/// Starts .NET and the C# SDK. Runs inside `jh_core_init` (so C# mods can install hooks).
unsafe fn start() -> Result<(), String> {
    let mods = jibbers_mod_sdk::mods_dir();
    let l = layout(&mods);
    if !l.sdk.exists() {
        return Err(format!("{} is missing: install the C# SDK", l.sdk.display()));
    }
    let _ = std::fs::create_dir_all(&l.mods);
    let rt = hostfxr::Runtime::start(&l.runtime_config, &l.bundled_runtime)?;
    let init: unsafe extern "system" fn(*const NativeApi) -> i32 = std::mem::transmute(rt.unmanaged_entry(&l.sdk, "Jibbers.Sdk.Bootstrap, Jibbers.Sdk", "Init")?);
    let api = Box::leak(Box::new(NativeApi {
        version: NATIVE_API_VERSION,
        host: host::host() as *const Host,
        log: log_line,
        register_plugin: loader_register(),
        detour_create: detour::create,
        detour_enable: detour::enable,
        detour_disable: detour::disable,
        detour_destroy: detour::destroy,
        last_error,
        game_dir: cstring(jibbers_mod_sdk::game_dir().to_string_lossy().into_owned()),
        mods_dir: cstring(mods.to_string_lossy().into_owned()),
        game_build: cstring(jibbers_mod_sdk::gamebuild::fingerprint_loaded().map(|f| f.describe()).unwrap_or_default()),
    }));
    if api.register_plugin.is_none() {
        log::info("dotnet: this loader cannot register plugins; C# mods load but have no Mods tab section (update version.dll)");
    }
    match init(api) {
        0 => Ok(()),
        n => Err(format!("Jibbers.Sdk.Bootstrap.Init failed ({n}); see the lines above")),
    }
}

pub struct DotnetBridge;

impl Plugin for DotnetBridge {
    const INFO: Info = Info {
        id: "dotnet",
        name: "C# mods",
        version: env!("CARGO_PKG_VERSION"),
        author: "Jaxon",
        description: "Hosts the .NET runtime and the C# SDK; every C# mod has its own section.",
    };
    const ENABLED_TOGGLE: bool = false;

    fn settings() -> Vec<Setting> {
        vec![]
    }

    fn init() {
        match unsafe { start() } {
            Ok(()) => status("running"),
            Err(e) => status(format!("not started: {e}")),
        }
    }

    fn command(cmd: &str, _req: &Value) -> Option<Result<Value, String>> {
        match cmd {
            "dotnet.status" => Some(Ok(json!({"status": *STATUS.lock().unwrap(), "layout": {
                "sdk": layout(&jibbers_mod_sdk::mods_dir()).sdk,
                "mods": layout(&jibbers_mod_sdk::mods_dir()).mods,
            }}))),
            _ => None,
        }
    }
}

declare_plugin!(DotnetBridge);
