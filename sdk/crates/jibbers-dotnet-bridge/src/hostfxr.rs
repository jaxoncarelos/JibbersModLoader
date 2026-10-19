//! Hosting .NET through `hostfxr` (the documented native hosting API, the same path as
//! `dotnet.exe`): pick a runtime, initialise it for the SDK's `runtimeconfig.json`, and get a
//! function pointer to a static `[UnmanagedCallersOnly]` method.

use std::ffi::c_void;
use std::path::{Path, PathBuf};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

#[repr(C)]
struct InitParams {
    size: usize,
    host_path: *const u16,
    dotnet_root: *const u16,
}

type InitForConfig = unsafe extern "C" fn(config: *const u16, params: *const InitParams, handle: *mut *mut c_void) -> i32;
type GetDelegate = unsafe extern "C" fn(handle: *mut c_void, kind: i32, delegate: *mut *mut c_void) -> i32;
type Close = unsafe extern "C" fn(handle: *mut c_void) -> i32;
type SetErrorWriter = unsafe extern "C" fn(writer: Option<unsafe extern "C" fn(*const u16)>) -> *const c_void;
type LoadAssemblyAndGetFunctionPointer =
    unsafe extern "C" fn(assembly: *const u16, type_name: *const u16, method: *const u16, delegate_type: *const u16, reserved: *mut c_void, out: *mut *mut c_void) -> i32;

/// `hdt_load_assembly_and_get_function_pointer`.
const HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER: i32 = 5;
/// `UNMANAGEDCALLERSONLY_METHOD`: the target is `[UnmanagedCallersOnly]`, no delegate type.
const UNMANAGED_CALLERS_ONLY: *const u16 = usize::MAX as *const u16;
/// Oldest runtime major version the SDK targets.
const MIN_MAJOR: u32 = 8;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}

fn wide_path(p: &Path) -> Vec<u16> {
    wide(&p.to_string_lossy())
}

unsafe extern "C" fn error_writer(msg: *const u16) {
    if msg.is_null() {
        return;
    }
    let n = (0..).take_while(|&i| *msg.add(i) != 0).count();
    jibbers_mod_sdk::log::info(format!("dotnet host: {}", String::from_utf16_lossy(std::slice::from_raw_parts(msg, n))));
}

/// `(major, minor, patch)` of a version folder name like `8.0.32`.
pub fn parse_version(name: &str) -> Option<(u32, u32, u32)> {
    let base = name.split('-').next()?;
    let mut it = base.split('.').map(|p| p.parse::<u32>().ok());
    Some((it.next()??, it.next().flatten().unwrap_or(0), it.next().flatten().unwrap_or(0)))
}

/// The newest `host\fxr\<version>\hostfxr.dll` under a .NET root, at least [`MIN_MAJOR`].
pub fn find_hostfxr(dotnet_root: &Path) -> Option<PathBuf> {
    let rd = std::fs::read_dir(dotnet_root.join("host").join("fxr")).ok()?;
    rd.flatten()
        .filter_map(|e| {
            let name = e.file_name().to_string_lossy().into_owned();
            let v = parse_version(&name)?;
            let dll = e.path().join("hostfxr.dll");
            (v.0 >= MIN_MAJOR && dll.exists()).then_some((v, dll))
        })
        .max_by_key(|(v, _)| *v)
        .map(|(_, p)| p)
}

/// .NET roots to try, in order: the runtime shipped with the SDK, `DOTNET_ROOT`, the machine-wide install.
pub fn candidate_roots(bundled: &Path) -> Vec<PathBuf> {
    let mut v = vec![bundled.to_path_buf()];
    if let Some(r) = std::env::var_os("DOTNET_ROOT") {
        v.push(PathBuf::from(r));
    }
    if let Some(pf) = std::env::var_os("ProgramFiles") {
        v.push(PathBuf::from(pf).join("dotnet"));
    }
    v.push(PathBuf::from(r"C:\Program Files\dotnet"));
    v
}

pub struct Runtime {
    load: LoadAssemblyAndGetFunctionPointer,
}

impl Runtime {
    /// Loads hostfxr and initialises the runtime for `runtime_config`.
    pub unsafe fn start(runtime_config: &Path, bundled: &Path) -> Result<Runtime, String> {
        let (root, fxr) = candidate_roots(bundled)
            .into_iter()
            .find_map(|r| find_hostfxr(&r).map(|f| (r, f)))
            .ok_or_else(|| format!(".NET {MIN_MAJOR} or newer not found: install the .NET {MIN_MAJOR} Desktop Runtime, or the C# SDK package with its bundled runtime"))?;
        jibbers_mod_sdk::log::info(format!("dotnet: using {}", fxr.display()));
        let module = LoadLibraryW(wide_path(&fxr).as_ptr());
        if module.is_null() {
            return Err(format!("could not load {}", fxr.display()));
        }
        let sym = |n: &std::ffi::CStr| GetProcAddress(module, n.as_ptr() as _).ok_or_else(|| format!("hostfxr has no {}", n.to_string_lossy()));
        let init: InitForConfig = std::mem::transmute(sym(c"hostfxr_initialize_for_runtime_config")?);
        let get: GetDelegate = std::mem::transmute(sym(c"hostfxr_get_runtime_delegate")?);
        let close: Close = std::mem::transmute(sym(c"hostfxr_close")?);
        if let Ok(f) = sym(c"hostfxr_set_error_writer") {
            let set: SetErrorWriter = std::mem::transmute(f);
            set(Some(error_writer));
        }
        let root_w = wide_path(&root);
        let host_w = wide_path(&std::env::current_exe().unwrap_or_default());
        let params = InitParams { size: std::mem::size_of::<InitParams>(), host_path: host_w.as_ptr(), dotnet_root: root_w.as_ptr() };
        let mut handle = std::ptr::null_mut();
        let rc = init(wide_path(runtime_config).as_ptr(), &params, &mut handle);
        // 0 = started, 1 = already initialised (compatible), 2 = initialised with other properties.
        if rc < 0 || handle.is_null() {
            return Err(format!("hostfxr_initialize_for_runtime_config failed (0x{:08x}) for {}", rc as u32, runtime_config.display()));
        }
        let mut load = std::ptr::null_mut();
        let rc = get(handle, HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER, &mut load);
        close(handle);
        if rc < 0 || load.is_null() {
            return Err(format!("hostfxr_get_runtime_delegate failed (0x{:08x})", rc as u32));
        }
        Ok(Runtime { load: std::mem::transmute(load) })
    }

    /// Pointer to `public static int Method(...)` marked `[UnmanagedCallersOnly]` in `type_name`
    /// (assembly-qualified) of the assembly at `assembly`.
    pub unsafe fn unmanaged_entry(&self, assembly: &Path, type_name: &str, method: &str) -> Result<*mut c_void, String> {
        let mut f = std::ptr::null_mut();
        let rc = (self.load)(wide_path(assembly).as_ptr(), wide(type_name).as_ptr(), wide(method).as_ptr(), UNMANAGED_CALLERS_ONLY, std::ptr::null_mut(), &mut f);
        if rc < 0 || f.is_null() {
            return Err(format!("could not load {type_name}.{method} from {} (0x{:08x})", assembly.display(), rc as u32));
        }
        Ok(f)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_parse_and_order() {
        assert_eq!(parse_version("8.0.32"), Some((8, 0, 32)));
        assert_eq!(parse_version("9.0.0-preview.1"), Some((9, 0, 0)));
        assert_eq!(parse_version("x"), None);
        assert!(parse_version("9.0.17") > parse_version("8.0.32"));
    }

    #[test]
    fn finds_the_machine_runtime_when_installed() {
        // On a machine with .NET 8+ this finds hostfxr; elsewhere it finds nothing, never a wrong file.
        if let Some(p) = candidate_roots(Path::new("Z:/none")).iter().find_map(|r| find_hostfxr(r)) {
            assert!(p.ends_with("hostfxr.dll"));
        }
    }
}
