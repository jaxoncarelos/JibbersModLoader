// From Fast Map Load by xhail39 (GPL-3.0-only), built into the loader; see perf/mod.rs.
//! Remembers each map's finished physics so the next load can skip building it.
//!
//! When a map loads, the game turns its physics into XML, then calls two MuJoCo functions:
//!   mj_parseXMLString(xml) -> spec     (slow: reads the whole XML)
//!   mj_compile(spec)       -> model    (the finished physics the game uses)
//!
//! We hook both. The first time we see a piece of XML we let them run as normal and, if that took
//! a while, save the model to a file named after a hash of the XML. Next time the same XML comes in we load that
//! file instead (mj_loadModel, MuJoCo's own save format), give the game an empty spec to hold,
//! and hand back the saved model when it compiles. If the map changes, its XML changes, so it
//! just gets a new file.
//!
//! Another mod that rebuilds the physics over and over with one-off scenes (e.g. only the collision
//! near the rider) can set FAST_MAP_LOAD_SKIP_CACHE while it does, so those aren't saved.

use super::{pcore, say};
use retour::GenericDetour;
use std::collections::HashMap;
use std::ffi::{c_char, c_int, c_void, CStr, CString};
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime};
use windows_sys::Win32::Foundation::HMODULE;

type Parse = unsafe extern "C" fn(*const c_char, *const c_void, *mut c_char, c_int) -> *mut c_void;
type Compile = unsafe extern "C" fn(*mut c_void, *const c_void) -> *mut c_void;
type FindElement = unsafe extern "C" fn(*mut c_void, c_int, *const c_char) -> *mut c_void;
type DeleteElement = unsafe extern "C" fn(*mut c_void, *mut c_void) -> c_int;
type DeleteSpec = unsafe extern "C" fn(*mut c_void);
type LoadModel = unsafe extern "C" fn(*const c_char, *const c_void) -> *mut c_void;
type SaveModel = unsafe extern "C" fn(*const c_void, *const c_char, *mut c_void, c_int);

static PARSE: OnceLock<GenericDetour<Parse>> = OnceLock::new();
static COMPILE: OnceLock<GenericDetour<Compile>> = OnceLock::new();
static FIND_ELEMENT: OnceLock<GenericDetour<FindElement>> = OnceLock::new();
static DELETE_ELEMENT: OnceLock<GenericDetour<DeleteElement>> = OnceLock::new();
static DELETE_SPEC: OnceLock<GenericDetour<DeleteSpec>> = OnceLock::new();
static LOAD_MODEL: OnceLock<LoadModel> = OnceLock::new();
static SAVE_MODEL: OnceLock<SaveModel> = OnceLock::new();

struct Settings {
    folder: Option<PathBuf>,
    limit_bytes: u64,
    mujoco_version: String,
}
static SETTINGS: OnceLock<Settings> = OnceLock::new();

/// Maps that build faster than this aren't worth the disk space.
const WORTH_REMEMBERING: Duration = Duration::from_secs(2);

/// While this environment variable is set, nothing is loaded from or saved to the cache.
const SKIP_CACHE: &str = "FAST_MAP_LOAD_SKIP_CACHE";

/// A map load in progress: between the parse and the compile of one spec.
struct Load {
    started: Instant,
    xml_mb: f64,
    save_as: Option<PathBuf>,
    saved_model: Option<usize>,
    edited: bool,
}
static LOADS: Mutex<Option<HashMap<usize, Load>>> = Mutex::new(None);

fn with_loads<T>(f: impl FnOnce(&mut HashMap<usize, Load>) -> T) -> T {
    let mut loads = LOADS.lock().unwrap_or_else(|e| e.into_inner());
    f(loads.get_or_insert_with(HashMap::new))
}

pub fn folder() -> Option<PathBuf> {
    std::env::var_os("LOCALAPPDATA").map(|d| PathBuf::from(d).join("Jibbers").join("FastMapLoad"))
}

/// Hooks MuJoCo. With no folder the cache is off and we only log how long each load took.
pub fn enable(pcore: HMODULE, folder: Option<PathBuf>, limit_gb: f64) -> Result<(), String> {
    if let Some(folder) = &folder {
        std::fs::create_dir_all(folder).map_err(|e| format!("{}: {e}", folder.display()))?;
        remove_unfinished_files(folder);
    }
    let _ = SETTINGS.set(Settings {
        folder: folder.clone(),
        limit_bytes: (limit_gb * 1e9) as u64,
        mujoco_version: pcore::version(pcore)?,
    });

    unsafe {
        let _ = LOAD_MODEL.set(std::mem::transmute(pcore::function(pcore, "mj_loadModel")?));
        let _ = SAVE_MODEL.set(std::mem::transmute(pcore::function(pcore, "mj_saveModel")?));
        hook(&PARSE, pcore, "mj_parseXMLString", parse as Parse)?;
        hook(&COMPILE, pcore, "mj_compile", compile as Compile)?;
        hook(&FIND_ELEMENT, pcore, "mjs_findElement", find_element as FindElement)?;
        hook(&DELETE_ELEMENT, pcore, "mjs_delete", delete_element as DeleteElement)?;
        hook(&DELETE_SPEC, pcore, "mj_deleteSpec", delete_spec as DeleteSpec)?;
    }
    match folder {
        Some(f) => say(format!("cache on: {}", f.display())),
        None => say("cache off"),
    }
    Ok(())
}

unsafe fn hook<F: retour::Function>(
    slot: &OnceLock<GenericDetour<F>>,
    pcore: HMODULE,
    name: &str,
    ours: F,
) -> Result<(), String> {
    let theirs: F = F::from_ptr(pcore::function(pcore, name)? as *const ());
    let detour = GenericDetour::new(theirs, ours).map_err(|e| format!("{name}: {e}"))?;
    detour.enable().map_err(|e| format!("{name}: {e}"))?;
    let _ = slot.set(detour);
    Ok(())
}

unsafe extern "C" fn parse(
    xml: *const c_char,
    vfs: *const c_void,
    error: *mut c_char,
    error_size: c_int,
) -> *mut c_void {
    let started = Instant::now();
    let original = PARSE.get().unwrap();
    let xml_bytes = if xml.is_null() { &[][..] } else { CStr::from_ptr(xml).to_bytes() };
    let xml_mb = xml_bytes.len() as f64 / 1e6;
    let file = if std::env::var_os(SKIP_CACHE).is_some() { None } else { cache_file(xml_bytes) };

    if let Some(model) = file.as_deref().and_then(load_saved) {
        let empty_spec = original.call(c"<mujoco/>".as_ptr(), vfs, error, error_size);
        if !empty_spec.is_null() {
            with_loads(|l| {
                l.insert(
                    empty_spec as usize,
                    Load { started, xml_mb, save_as: None, saved_model: Some(model), edited: false },
                )
            });
            return empty_spec;
        }
    }

    let spec = original.call(xml, vfs, error, error_size);
    if !spec.is_null() {
        with_loads(|l| {
            l.insert(spec as usize, Load { started, xml_mb, save_as: file, saved_model: None, edited: false })
        });
    }
    spec
}

unsafe extern "C" fn compile(spec: *mut c_void, vfs: *const c_void) -> *mut c_void {
    let Some(load) = with_loads(|l| l.remove(&(spec as usize))) else {
        return COMPILE.get().unwrap().call(spec, vfs);
    };
    if let Some(model) = load.saved_model {
        say(format!(
            "{:.0} MB map: physics loaded from cache in {:.2} s",
            load.xml_mb,
            load.started.elapsed().as_secs_f64()
        ));
        return model as *mut c_void;
    }

    let model = COMPILE.get().unwrap().call(spec, vfs);
    let mut note = String::new();
    if let (false, Some(file)) = (model.is_null(), load.save_as) {
        // If the game changed the spec after reading the XML, the XML alone doesn't describe this model.
        note = if load.edited {
            ", not cached (the game edited it)".into()
        } else if load.started.elapsed() < WORTH_REMEMBERING {
            ", quick enough not to cache".into()
        } else {
            save(model, &file)
        };
    }
    say(format!(
        "{:.0} MB map: physics built in {:.1} s{note}",
        load.xml_mb,
        load.started.elapsed().as_secs_f64()
    ));
    model
}

unsafe extern "C" fn find_element(spec: *mut c_void, kind: c_int, name: *const c_char) -> *mut c_void {
    mark_edited(spec);
    FIND_ELEMENT.get().unwrap().call(spec, kind, name)
}

unsafe extern "C" fn delete_element(spec: *mut c_void, element: *mut c_void) -> c_int {
    mark_edited(spec);
    DELETE_ELEMENT.get().unwrap().call(spec, element)
}

unsafe extern "C" fn delete_spec(spec: *mut c_void) {
    with_loads(|l| l.remove(&(spec as usize)));
    DELETE_SPEC.get().unwrap().call(spec)
}

fn mark_edited(spec: *mut c_void) {
    with_loads(|l| {
        if let Some(load) = l.get_mut(&(spec as usize)) {
            load.edited = true;
        }
    });
}

/// Where the model for this XML is (or will be) saved. None when the cache is off.
fn cache_file(xml: &[u8]) -> Option<PathBuf> {
    let settings = SETTINGS.get()?;
    let folder = settings.folder.as_ref()?;
    if xml.is_empty() {
        return None;
    }
    let mut hash = Fnv::new();
    hash.add(settings.mujoco_version.as_bytes());
    hash.add(xml);
    Some(folder.join(format!("{:016x}-{}.mjb", hash.0, xml.len())))
}

fn load_saved(file: &Path) -> Option<usize> {
    if !file.exists() {
        return None;
    }
    let path = CString::new(file.to_string_lossy().as_bytes()).ok()?;
    let model = unsafe { LOAD_MODEL.get()?(path.as_ptr(), std::ptr::null()) };
    if model.is_null() {
        say(format!("couldn't read {}, building normally", file.display()));
        return None;
    }
    // Mark it as recently used so the size limit removes other maps first.
    let _ = std::fs::File::open(file).and_then(|f| f.set_modified(SystemTime::now()));
    Some(model as usize)
}

fn save(model: *mut c_void, file: &Path) -> String {
    // Write to a temporary name first so a crash never leaves half a file behind.
    let unfinished = file.with_extension("tmp");
    let (Some(save_model), Ok(path)) =
        (SAVE_MODEL.get(), CString::new(unfinished.to_string_lossy().as_bytes()))
    else {
        return String::new();
    };
    unsafe { save_model(model, path.as_ptr(), std::ptr::null_mut(), 0) };
    if std::fs::rename(&unfinished, file).is_err() {
        return ", couldn't save to cache".into();
    }
    if let Some(settings) = SETTINGS.get() {
        keep_under_limit(file.parent().unwrap_or(file), settings.limit_bytes);
    }
    ", saved to cache".to_string()
}

/// Deletes the least recently loaded maps until the cache fits the size limit.
fn keep_under_limit(folder: &Path, limit_bytes: u64) {
    let Ok(entries) = std::fs::read_dir(folder) else { return };
    let mut files: Vec<(SystemTime, u64, PathBuf)> = entries
        .flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x == "mjb"))
        .filter_map(|e| {
            let meta = e.metadata().ok()?;
            Some((meta.modified().ok()?, meta.len(), e.path()))
        })
        .collect();
    files.sort();
    let mut total: u64 = files.iter().map(|f| f.1).sum();
    for (_, size, path) in files {
        if total <= limit_bytes {
            break;
        }
        if std::fs::remove_file(&path).is_ok() {
            total -= size;
        }
    }
}

fn remove_unfinished_files(folder: &Path) {
    for entry in std::fs::read_dir(folder).into_iter().flatten().flatten() {
        if entry.path().extension().is_some_and(|x| x == "tmp") {
            let _ = std::fs::remove_file(entry.path());
        }
    }
}

/// FNV-1a: a simple, stable 64-bit hash. Fast enough for a 50 MB map (about 50 ms).
struct Fnv(u64);

impl Fnv {
    fn new() -> Self {
        Fnv(0xcbf2_9ce4_8422_2325)
    }
    fn add(&mut self, bytes: &[u8]) {
        for &b in bytes {
            self.0 = (self.0 ^ b as u64).wrapping_mul(0x100_0000_01b3);
        }
    }
}
