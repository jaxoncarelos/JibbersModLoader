//! Jibbers in-game hotload mod: loader.
//!
//! Ships as `version.dll` in the game folder. UnityPlayer.dll imports VERSION.dll, finds ours
//! first, and we forward every call to the real System32 copy (see `proxy`). In the game process
//! a worker thread waits for IL2CPP, hooks a few per-frame methods so work can run on Unity's main
//! thread (`pump`), loads the game-logic DLLs (`core`), and serves developer requests on localhost.
//!
//! The loader loads the developer core `jibbers_hotload_core.dll` (if it is in the game folder) and
//! every plugin in `JibbersMods\` that exports the core ABI, so one version.dll serves all of them.
//!
//! Set `JIBBERS_HOTLOAD_DISABLE=1` to start the game without the mod.
//!
//! Before touching IL2CPP the loader fingerprints the game (`compat`): on a build that is not in
//! the compatibility list it hooks nothing and loads no plugin except the SDK runtime, logs one
//! line, and writes `JibbersMods\MODS-DISABLED.txt`. Set `JIBBERS_HOTLOAD_FORCE=1` to load anyway
//! (at your own risk) when testing a new game build.
//!
//! With the `release` feature there is no server: the loader attaches on its own, as players'
//! builds need, and the plugins run without a developer client. The loader also owns their shared Mods
//! tab unless the developer core is present and already provides it.

#![cfg_attr(feature = "release", allow(dead_code))]

#[path = "../../../sdk/crates/jibbers-mod-sdk/src/abi.rs"]
mod abi;
mod core;
mod compat;
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/gamebuild.rs"]
mod gamebuild;
mod hooks;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/il2cpp.rs"]
mod il2cpp;
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/log.rs"]
mod log;
mod proxy;
mod pump;
#[cfg(feature = "release")]
#[path = "../../jibbers-hotload-core/src/modmenu.rs"]
mod modmenu;
#[cfg(feature = "release")]
mod release_menu;
#[cfg(feature = "release")]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/unity.rs"]
mod unity;
// The Mods tab reaches the game's menus through the SDK's symbols (shared by path).
#[cfg(feature = "release")]
#[allow(dead_code)]
#[path = "../../jibbers-hotload-core/src/host_shim.rs"]
mod host;
#[cfg(feature = "release")]
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/inspect.rs"]
mod inspect;
#[cfg(feature = "release")]
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/shape.rs"]
mod shape;
#[cfg(feature = "release")]
#[allow(dead_code)]
#[path = "../../../sdk/crates/jibbers-mod-sdk/src/symbols.rs"]
mod symbols;
#[cfg(feature = "release")]
use release_menu::probe;
#[cfg(not(feature = "release"))]
mod server;

use std::ffi::c_void;
use std::path::PathBuf;
use std::time::Duration;
use windows_sys::Win32::Foundation::HINSTANCE;
use windows_sys::Win32::System::LibraryLoader::{GetModuleFileNameW, GetModuleHandleA};
use windows_sys::Win32::System::SystemServices::DLL_PROCESS_ATTACH;

/// Folder containing the host executable.
pub fn game_dir() -> PathBuf {
    exe_path().parent().map(|p| p.to_path_buf()).unwrap_or_default()
}

/// The same loader-owned host table passed to every plugin.
#[cfg(feature = "release")]
pub fn host() -> &'static abi::Host {
    core::host()
}

fn exe_path() -> PathBuf {
    let mut buf = [0u16; 1024];
    let n = unsafe { GetModuleFileNameW(std::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32) } as usize;
    PathBuf::from(String::from_utf16_lossy(&buf[..n]))
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn DllMain(_module: HINSTANCE, reason: u32, _reserved: *mut c_void) -> i32 {
    if reason == DLL_PROCESS_ATTACH {
        proxy::init();
        // UnityCrashHandler and other tools in the folder load VERSION.dll too; only run in the game.
        let is_game = exe_path()
            .file_name()
            .map(|n| n.to_string_lossy().eq_ignore_ascii_case("Jibbers.exe"))
            .unwrap_or(false);
        if is_game && std::env::var_os("JIBBERS_HOTLOAD_DISABLE").is_none() {
            std::thread::spawn(worker);
        }
    }
    1
}

static START: std::sync::OnceLock<std::time::Instant> = std::sync::OnceLock::new();
static READY: std::sync::Mutex<Option<Result<(), String>>> = std::sync::Mutex::new(None);

#[cfg(feature = "release")]
const MODE: &str = "release: plugins only, no server";
#[cfg(not(feature = "release"))]
const MODE: &str = "lazy: nothing happens until the first request";

/// At game start the loader only opens its port: touching IL2CPP while Jibbers boots (even
/// attaching a thread) crashed the game after its Oct 2026 update. Everything else happens on the
/// first request or once the game window has settled (`auto_attach`, 8-45 s), whichever comes first.
fn worker() {
    START.get_or_init(std::time::Instant::now);
    log::info(format!("jibbers-hotload loader {} starting ({MODE})", env!("CARGO_PKG_VERSION")));
    core::clean_temp();
    std::thread::spawn(auto_attach);
    #[cfg(not(feature = "release"))]
    server::serve();
}

/// Attaches on its own once the game has settled, so in-game features of the cores (the
/// atmosphere) run without a client ever connecting. Set `JIBBERS_HOTLOAD_LAZY=1` to wait for
/// the first request instead (not in the release build, which always attaches).
fn auto_attach() {
    if !cfg!(feature = "release") && std::env::var_os("JIBBERS_HOTLOAD_LAZY").is_some() {
        return;
    }
    // Attach once the game's window has been up and responsive for a moment (the main menu is
    // loading by then), never before MIN_UP, and at the latest after MAX_WAIT.
    let mut ready_since: Option<std::time::Instant> = None;
    loop {
        std::thread::sleep(Duration::from_millis(500));
        let up = START.get().map(|t| t.elapsed()).unwrap_or_default();
        if up >= MAX_WAIT {
            break;
        }
        if unsafe { game_window_ready() } {
            let since = *ready_since.get_or_insert_with(std::time::Instant::now);
            if up >= MIN_UP && since.elapsed() >= WINDOW_SETTLE {
                break;
            }
        } else {
            ready_since = None;
        }
    }
    log::info(format!("auto-attach after {}s", START.get().map(|t| t.elapsed().as_secs()).unwrap_or(0)));
    for _ in 0..60 {
        match ensure_ready() {
            Ok(()) => {
                // init_game attached this thread; detach it before it exits.
                let a = il2cpp::api();
                unsafe { (a.thread_detach)((a.thread_attach)((a.domain_get)())) };
                log::info("auto-attach done");
                return;
            }
            Err(e) => {
                if compat::blocked() {
                    // The gate already logged why; the build won't change while the game runs.
                    log::info("auto-attach: stopped (mods disabled for this game build)");
                    return;
                }
                log::info(format!("auto-attach: {e}; retrying"));
            }
        }
        std::thread::sleep(Duration::from_secs(10));
    }
}

/// Earliest attach after launch: touching IL2CPP while the game boots crashed it (Oct 2026 update).
const MIN_UP: Duration = Duration::from_secs(8);
/// How long the game window must be visible and responsive before auto-attach.
const WINDOW_SETTLE: Duration = Duration::from_secs(3);
/// Auto-attach no later than this, window or not.
const MAX_WAIT: Duration = Duration::from_secs(45);

/// True when this process has a visible, responsive Unity window (the game has finished its
/// native start-up and is drawing frames).
unsafe fn game_window_ready() -> bool {
    use windows_sys::core::BOOL;
    use windows_sys::Win32::Foundation::{HWND, LPARAM};
    use windows_sys::Win32::UI::WindowsAndMessaging::{EnumWindows, GetClassNameW, GetWindowThreadProcessId, IsHungAppWindow, IsWindowVisible};
    unsafe extern "system" fn each(hwnd: HWND, found: LPARAM) -> BOOL {
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, &mut pid);
        if pid != std::process::id() || IsWindowVisible(hwnd) == 0 || IsHungAppWindow(hwnd) != 0 {
            return 1;
        }
        let mut buf = [0u16; 64];
        let n = GetClassNameW(hwnd, buf.as_mut_ptr(), buf.len() as i32) as usize;
        if String::from_utf16_lossy(&buf[..n]) == "UnityWndClass" {
            *(found as *mut bool) = true;
            return 0;
        }
        1
    }
    let mut found = false;
    EnumWindows(Some(each), &mut found as *mut bool as LPARAM);
    found
}

/// Connects to the running game once (attach, frame hooks, cores). Refused during start-up.
pub fn ensure_ready() -> Result<(), String> {
    let mut g = READY.lock().unwrap();
    if let Some(r) = g.as_ref() {
        return r.clone();
    }
    let up = START.get().map(|t| t.elapsed()).unwrap_or_default();
    if up < MIN_UP {
        return Err(format!("game still starting ({}s); try again in a moment", up.as_secs()));
    }
    finish(&mut g, unsafe { init_game() })
}

/// Why `init_game` did not finish.
#[derive(Debug, PartialEq, Eq)]
enum InitError {
    /// Not ready yet (game still loading, a core failed to copy); the next request tries again.
    Retry(String),
    /// The game build is refused. Permanent for this process: remembered, never retried.
    Blocked(String),
}

impl From<String> for InitError {
    fn from(e: String) -> Self {
        InitError::Retry(e)
    }
}

impl From<&str> for InitError {
    fn from(e: &str) -> Self {
        InitError::Retry(e.into())
    }
}

/// Records an init outcome in `READY`: success and a blocked build are remembered for good, other
/// failures are retried on the next request.
fn finish(g: &mut Option<Result<(), String>>, r: Result<(), InitError>) -> Result<(), String> {
    let r = match r {
        Ok(()) => Ok(()),
        Err(InitError::Retry(e)) => Err(e),
        Err(InitError::Blocked(why)) => {
            *g = Some(Err(why.clone())); // the gate logged why, once
            return Err(why);
        }
    };
    log::info(format!("init: {r:?}"));
    if r.is_ok() {
        *g = Some(Ok(()));
    }
    r
}

unsafe fn init_game() -> Result<(), InitError> {
    if GetModuleHandleA(c"GameAssembly.dll".as_ptr() as _).is_null() {
        return Err("GameAssembly.dll not loaded".into());
    }
    // Before IL2CPP is touched or any game method hooked: is this a build the mods were validated on?
    let verdict = compat::gate();
    if !verdict.allows_mods() {
        core::attach_runtime_only();
        return Err(InitError::Blocked(verdict.message()));
    }
    il2cpp::init()?;
    let a = il2cpp::api();
    let domain = (a.domain_get)();
    if domain.is_null() {
        return Err("IL2CPP domain not ready".into());
    }
    (a.thread_attach)(domain);
    if il2cpp::image("Assembly-CSharp.dll").is_none() {
        return Err("Assembly-CSharp not loaded yet".into());
    }
    let hooks = pump::install();
    log::info(format!("IL2CPP ready, {hooks} frame hook(s)"));
    if hooks == 0 {
        return Err("no frame hooks".into());
    }
    core::attach()?;
    // attach's CORES write guard must be gone: the menu enumerates/calls plugins.
    #[cfg(feature = "release")]
    release_menu::bootstrap();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_blocked_build_is_remembered_and_never_retried() {
        let mut g = None;
        let why = "game build X not validated; mods disabled (loader built for Y)".to_string();
        assert_eq!(finish(&mut g, Err(InitError::Blocked(why.clone()))), Err(why.clone()));
        assert_eq!(g, Some(Err(why)), "READY keeps the refusal, so ensure_ready never runs init_game again");
    }

    #[test]
    fn other_failures_are_retried_and_success_is_kept() {
        let mut g = None;
        assert_eq!(finish(&mut g, Err("Assembly-CSharp not loaded yet".into())), Err("Assembly-CSharp not loaded yet".to_string()));
        assert_eq!(g, None);
        assert_eq!(finish(&mut g, Ok(())), Ok(()));
        assert_eq!(g, Some(Ok(())));
    }
}
