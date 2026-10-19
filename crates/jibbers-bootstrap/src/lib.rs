//! Stable version.dll proxy. The game-facing loader is a replaceable DLL loaded only after
//! signed startup updates finish, so UI, hooks and mod-management fixes can ship on next launch.
#[path = "../../jibbers-hotload/src/proxy.rs"]
mod proxy;
use std::{ffi::c_void, path::PathBuf};
use windows_sys::Win32::{Foundation::HINSTANCE, System::{LibraryLoader::{GetModuleFileNameW, LoadLibraryW}, SystemServices::DLL_PROCESS_ATTACH}};
static IMPLEMENTATION: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);

/// Preserve the C# bridge's existing version.dll entry point across the bootstrap split.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn jh_register_plugin(name: *const u8, len: usize, ctx: *mut c_void, handle: *const c_void, free: *const c_void, unload: *const c_void) -> u32 {
    use windows_sys::Win32::System::LibraryLoader::GetProcAddress;
    let module = IMPLEMENTATION.load(std::sync::atomic::Ordering::Acquire);
    if module == 0 || handle.is_null() || free.is_null() { return 0; }
    let Some(function) = GetProcAddress(module as _, c"jh_register_plugin".as_ptr() as _) else { return 0 };
    let register: unsafe extern "C" fn(*const u8, usize, *mut c_void, *const c_void, *const c_void, *const c_void) -> u32 = std::mem::transmute(function);
    register(name, len, ctx, handle, free, unload)
}

fn exe() -> PathBuf {
    let mut buf = vec![0u16; 32768];
    let n = unsafe { GetModuleFileNameW(std::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32) } as usize;
    PathBuf::from(String::from_utf16_lossy(&buf[..n]))
}
fn log(root: &std::path::Path, message: &str) {
    use std::io::Write;
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(root.join("jibbers_hotload.log")) {
        let _ = writeln!(f, "[bootstrap] {message}");
    }
}
fn run() {
    let path = exe(); let Some(root) = path.parent() else { return };
    log(root, "checking signed loader and mod updates before loading mods");
    let result = jibbers_distribution::manager::startup(root, &jibbers_distribution::http::WinHttp);
    let message = match result { Ok(message) => message, Err(error) => error };
    log(root, &message);
    // A failed recovery is never bypassed by loading mixed files.
    let journal = root.join(jibbers_distribution::STATE_DIR).join("journal.json");
    if journal.exists() { log(root, "installation recovery required; loader was not started"); return; }
    let source = match jibbers_distribution::install::safe_path(root, jibbers_distribution::LOADER_PATH) {
        Ok(path) => path, Err(error) => { log(root, &error); return; }
    };
    // Windows locks loaded DLLs. Run a private copy so the installed implementation is always
    // replaceable by the next startup update; no executable waits to rewrite version.dll.
    let copies = match jibbers_distribution::install::safe_path(root, "JibbersMods/system/running") {
        Ok(path) => path, Err(error) => { log(root, &error); return; }
    };
    if std::fs::create_dir_all(&copies).is_err() { log(root, "cannot create loader working directory"); return; }
    let copy = copies.join(format!("loader-{}.dll", std::process::id()));
    if let Err(error) = std::fs::copy(&source, &copy) { log(root, &format!("cannot copy installed loader: {error}")); return; }
    let wide: Vec<u16> = copy.to_string_lossy().encode_utf16().chain(Some(0)).collect();
    let module = unsafe { LoadLibraryW(wide.as_ptr()) };
    if module.is_null() { log(root, "could not load the installed implementation"); }
    else { IMPLEMENTATION.store(module as usize, std::sync::atomic::Ordering::Release); }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn DllMain(_module: HINSTANCE, reason: u32, _reserved: *mut c_void) -> i32 {
    if reason == DLL_PROCESS_ATTACH {
        proxy::init();
        if exe().file_name().is_some_and(|p| p.eq_ignore_ascii_case("Jibbers.exe")) && std::env::var_os("JIBBERS_HOTLOAD_DISABLE").is_none() {
            std::thread::spawn(|| { let _ = std::panic::catch_unwind(run); });
        }
    }
    1
}
