//! Runs the real Rust hooks against the game's pcore in an isolated process, never the game.
#[path = "../src/perf/cache.rs"]
mod cache;
#[path = "../src/perf/fast_parse.rs"]
mod fast_parse;
#[path = "../src/perf/pcore.rs"]
mod pcore;
#[path = "../src/perf/scene.rs"]
mod scene;
use std::ffi::{c_char, c_int, c_void, CString};
use std::path::Path;
use std::time::Instant;
use windows_sys::Win32::Foundation::HMODULE;
use windows_sys::Win32::System::LibraryLoader::LoadLibraryW;
fn game_dir() -> std::path::PathBuf {
    std::env::current_dir().unwrap()
}
fn say(message: impl AsRef<str>) {
    println!("{}", message.as_ref());
}
unsafe fn build(module: HMODULE, xml: &Path, save: Option<&Path>, label: &str) {
    let parse: unsafe extern "C" fn(
        *const c_char,
        *const c_void,
        *mut c_char,
        c_int,
    ) -> *mut c_void = std::mem::transmute(pcore::function(module, "mj_parseXMLString").unwrap());
    let compile: unsafe extern "C" fn(*mut c_void, *const c_void) -> *mut c_void =
        std::mem::transmute(pcore::function(module, "mj_compile").unwrap());
    let delete_spec: unsafe extern "C" fn(*mut c_void) =
        std::mem::transmute(pcore::function(module, "mj_deleteSpec").unwrap());
    let delete_model: unsafe extern "C" fn(*mut c_void) =
        std::mem::transmute(pcore::function(module, "mj_deleteModel").unwrap());
    let save_model: unsafe extern "C" fn(*const c_void, *const c_char, *mut c_void, c_int) =
        std::mem::transmute(pcore::function(module, "mj_saveModel").unwrap());
    let xml = CString::new(std::fs::read(xml).unwrap()).unwrap();
    let mut error = [0i8; 2048];
    let started = Instant::now();
    let spec = parse(
        xml.as_ptr(),
        std::ptr::null(),
        error.as_mut_ptr(),
        error.len() as c_int,
    );
    assert!(
        !spec.is_null(),
        "parse failed: {}",
        std::ffi::CStr::from_ptr(error.as_ptr()).to_string_lossy()
    );
    let parse_time = started.elapsed();
    let model = compile(spec, std::ptr::null());
    assert!(!model.is_null(), "compile failed");
    println!(
        "{label}: parse={:.3}s compile={:.3}s total={:.3}s",
        parse_time.as_secs_f64(),
        started.elapsed().as_secs_f64() - parse_time.as_secs_f64(),
        started.elapsed().as_secs_f64()
    );
    if let Some(path) = save {
        let name = CString::new(path.to_string_lossy().as_bytes()).unwrap();
        save_model(model, name.as_ptr(), std::ptr::null_mut(), 0);
        assert!(path.is_file());
    }
    let rebuilt = compile(spec, std::ptr::null());
    assert!(!rebuilt.is_null(), "retained-spec recompile failed");
    println!("{label}: retained-spec recompile passed");
    delete_model(rebuilt);
    delete_model(model);
    delete_spec(spec);
}
fn main() {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    assert_eq!(
        args.len(),
        4,
        "pcore.dll initial.xml respawn.xml output_directory"
    );
    let output = Path::new(&args[3]);
    std::fs::create_dir_all(output).unwrap();
    let baseline = output.join("baseline.mjb");
    let optimized = output.join("optimized.mjb");
    assert!(
        !baseline.exists() && !optimized.exists(),
        "use a fresh output directory"
    );
    unsafe {
        use std::os::windows::ffi::OsStrExt;
        let path: Vec<u16> = args[0].encode_wide().chain(Some(0)).collect();
        let module = LoadLibraryW(path.as_ptr());
        assert!(!module.is_null());
        assert_eq!(pcore::version(module).unwrap(), "3.3.6");
        fast_parse::enable(module).unwrap();
        build(
            module,
            Path::new(&args[2]),
            Some(&baseline),
            "existing fast-load baseline",
        );
        println!("Tree pairs: {}", scene::find_tree_lists(module).unwrap());
        scene::enable_mesh_cache(module, 3usize << 30).unwrap();
        cache::enable(module, None, 4.0).unwrap();
        build(module, Path::new(&args[1]), None, "optimized initial load");
        build(
            module,
            Path::new(&args[2]),
            Some(&optimized),
            "optimized respawn after object placement",
        );
    }
}
