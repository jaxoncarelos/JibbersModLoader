//! Startup updates finish before any replaceable code is loaded. Menu operations only change
//! subscriptions or refresh catalog metadata; installation/removal happens on the next launch.
use crate::{catalog::{self, Catalog}, install::{self, Fetch, Installed}, STATE_DIR};
use serde::{Deserialize, Serialize};
use std::{collections::HashSet, path::Path, sync::{Mutex, OnceLock}};

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Config { pub auto_update: bool, pub wanted: Vec<String>, pub catalog_url: String }
impl Default for Config {
    fn default() -> Self { Self { auto_update: true, wanted: vec![], catalog_url: crate::CATALOG_URL.into() } }
}
#[derive(Clone)]
pub struct Snapshot { pub catalog: Option<Catalog>, pub config: Config, pub installed: Installed, pub message: String, pub busy: bool }
static SESSION: OnceLock<Mutex<Snapshot>> = OnceLock::new();

fn path(root: &Path, name: &str) -> Result<std::path::PathBuf, String> { install::safe_path(root, &format!("{STATE_DIR}/{name}")) }
pub fn config(root: &Path) -> Result<Config, String> {
    let p = path(root, "preferences.json")?;
    if p.exists() { install::read_json(&p) } else { Ok(Config::default()) }
}
pub fn cached(root: &Path) -> Result<Catalog, String> {
    catalog::verify(&std::fs::read(path(root, "catalog.json")?).map_err(|e| e.to_string())?,
        &std::fs::read_to_string(path(root, "catalog.json.sig")?).map_err(|e| e.to_string())?, crate::trust::KEYS)
}

pub fn fetch_catalog(root: &Path, net: &dyn Fetch) -> Result<Catalog, String> {
    let cfg = config(root)?;
    crate::http::split_url(&cfg.catalog_url)?;
    let bytes = net.get(&cfg.catalog_url, catalog::MAX_CATALOG)?;
    let sig = net.get(&format!("{}.sig", cfg.catalog_url), 1024)?;
    let cat = catalog::verify(&bytes, &String::from_utf8_lossy(&sig), crate::trust::KEYS)?;
    let installed: Installed = install::read_json(&path(root, "installed.json")?).unwrap_or_default();
    let have = cached(root).map(|c| c.sequence).unwrap_or(0).max(installed.sequence);
    if cat.sequence < have { return Err("refusing an older catalog".into()); }
    if let Ok(previous) = cached(root) {
        if cat.sequence == previous.sequence {
            let old_bytes = std::fs::read(path(root, "catalog.json")?).map_err(|e| e.to_string())?;
            if bytes != old_bytes { return Err("catalog sequence was reused for different content".into()); }
        } else { catalog::advance(&previous, &cat)?; }
    }
    let _lock = install::Lock::acquire(root)?;
    install::atomic_write(&path(root, "catalog.json")?, &bytes)?;
    install::atomic_write(&path(root, "catalog.json.sig")?, &sig)?;
    Ok(cat)
}

/// Called by the stable bootstrap, before LoadLibrary on the implementation DLL.
pub fn startup(root: &Path, net: &dyn Fetch) -> Result<String, String> {
    let result = startup_inner(root, net);
    let message = match &result { Ok(message) => message.clone(), Err(error) => format!("Updates were not applied: {error}") };
    if let Ok(path) = path(root, "status.json") { let _ = install::write_json(&path, &message); }
    result
}

fn startup_inner(root: &Path, net: &dyn Fetch) -> Result<String, String> {
    { let _lock = install::Lock::acquire(root)?; install::recover(root)?; }
    let (cat, offline) = match fetch_catalog(root, net) {
        Ok(c) => (c, false),
        Err(network) => match cached(root) {
            Ok(c) => (c, true),
            Err(_) => return Err(format!("Updates unavailable; keeping installed files: {network}")),
        }
    };
    enroll_legacy(root, &cat)?;
    let cfg = config(root)?;
    let result = install::apply(root, &cat, &cfg.wanted, cfg.auto_update, net)?;
    let message = format!("{}; {} managed packages installed", if offline { "Using cached catalog" } else { "Up to date" }, result.packages.len());
    Ok(message)
}

/// The signed SDK migration is the opt-in bridge for pre-manager installs. Adopt only existing
/// catalog DLLs on that first bootstrap launch; unrelated local mods and settings are excluded.
fn enroll_legacy(root: &Path, cat: &Catalog) -> Result<(), String> {
    let prefs = path(root, "preferences.json")?;
    let record = path(root, "installed.json")?;
    if prefs.exists() || record.exists() { return Ok(()); }
    let mut cfg = Config::default();
    let mut installed = Installed::default();
    for package in &cat.packages {
        if package.id != "loader" && !package.files.iter().any(|file| file.path.ends_with(".dll") && root.join(&file.path).is_file()) { continue; }
        let mut files = vec![];
        for file in &package.files {
            let path = install::safe_path(root, &file.path)?;
            if path.exists() {
                let bytes = std::fs::read(path).map_err(|e| e.to_string())?;
                let mut file = file.clone(); file.sha256 = catalog::hash(&bytes); file.size = bytes.len() as u64;
                files.push(file);
            }
        }
        if !files.is_empty() {
            installed.packages.insert(package.id.clone(), install::InstalledPackage { version: "0.0.0".into(), files });
            if package.id != "loader" { cfg.wanted.push(package.id.clone()); }
        }
    }
    let _lock = install::Lock::acquire(root)?;
    install::write_json(&record, &installed)?;
    install::write_json(&prefs, &cfg)
}

/// Mod DLLs the loader or the C# SDK would load (`JibbersMods\*.dll`, `JibbersMods\cs\*.dll`,
/// `JibbersMods\cs\<name>\<name>.dll`) whose bytes match no file from the signed catalog: installed
/// by hand or modified since. Legacy-enrolled records (version 0.0.0) hashed whatever was on disk,
/// so only the catalog vouches for those. Paths are relative to `root`, with `/`.
pub fn unverified_mods(root: &Path) -> Vec<String> {
    let mut known: HashSet<(String, String)> = HashSet::new();
    let installed: Installed = path(root, "installed.json").and_then(|p| install::read_json(&p)).unwrap_or_default();
    let recorded = installed.packages.into_values().filter(|p| p.version != "0.0.0").flat_map(|p| p.files);
    let signed = cached(root).map(|c| c.packages.into_iter().flat_map(|p| p.files).collect()).unwrap_or_else(|_| vec![]);
    for f in recorded.chain(signed) { known.insert((f.path.to_ascii_lowercase(), f.sha256)); }
    let entries = |rel: &str, dirs: bool| -> Vec<String> {
        let Ok(rd) = std::fs::read_dir(root.join(rel)) else { return vec![] };
        let mut out: Vec<String> = rd.flatten().filter(|e| e.path().is_dir() == dirs).filter_map(|e| e.file_name().to_str().map(String::from)).collect();
        out.sort(); out
    };
    let mut candidates: Vec<String> = ["JibbersMods", "JibbersMods/cs"].iter()
        .flat_map(|rel| entries(rel, false).into_iter().filter(|n| n.to_ascii_lowercase().ends_with(".dll")).map(move |n| format!("{rel}/{n}")))
        .collect();
    candidates.extend(entries("JibbersMods/cs", true).into_iter().map(|d| format!("JibbersMods/cs/{d}/{d}.dll")).filter(|rel| root.join(rel).is_file()));
    candidates.into_iter().filter(|rel| {
        let Ok(bytes) = std::fs::read(root.join(rel)) else { return true };
        !known.contains(&(rel.to_ascii_lowercase(), catalog::hash(&bytes)))
    }).collect()
}

fn session(root: &Path) -> &'static Mutex<Snapshot> {
    SESSION.get_or_init(|| Mutex::new(Snapshot {
        catalog: cached(root).ok(), config: config(root).unwrap_or_default(),
        installed: path(root, "installed.json").and_then(|p| install::read_json(&p)).unwrap_or_default(),
        message: path(root, "status.json").and_then(|p| install::read_json(&p)).unwrap_or_else(|_| "Check for updates to load the public mod catalog".into()), busy: false,
    }))
}
pub fn snapshot(root: &Path) -> Snapshot { session(root).lock().unwrap().clone() }
pub fn message(root: &Path) -> String { session(root).lock().unwrap().message.clone() }
pub fn selected(root: &Path, id: Option<&str>) -> bool {
    let state = session(root).lock().unwrap();
    match id {
        Some(id) => state.config.wanted.iter().any(|p| p == id)
            || state.catalog.as_ref().and_then(|c| c.resolve(&state.config.wanted).ok()).is_some_and(|packages| packages.iter().any(|p| p.id == id)),
        None => state.config.auto_update,
    }
}
pub fn select(root: &Path, id: &str, wanted: bool) -> Result<(), String> {
    let mut state = session(root).lock().unwrap();
    let cat = state.catalog.as_ref().ok_or("public catalog is unavailable")?;
    if !cat.selectable_packages()?.iter().any(|p| p.id == id) { return Err("unknown or required package".into()); }
    let mut cfg = state.config.clone();
    cfg.wanted.retain(|p| p != id);
    if wanted { cfg.wanted.push(id.to_string()); }
    // A selected dependency cannot be removed while a selected mod still needs it.
    if !wanted && cat.resolve(&cfg.wanted)?.iter().any(|p| p.id == id) { return Err("another selected mod needs this package".into()); }
    let _lock = install::Lock::acquire(root)?;
    install::write_json(&path(root, "preferences.json")?, &cfg)?;
    state.config = cfg;
    state.message = "Selection saved. Mods will install or uninstall at the next game launch.".into();
    Ok(())
}
pub fn set_auto_update(root: &Path, enabled: bool) -> Result<(), String> {
    let mut state = session(root).lock().unwrap();
    let mut cfg = state.config.clone(); cfg.auto_update = enabled;
    let _lock = install::Lock::acquire(root)?;
    install::write_json(&path(root, "preferences.json")?, &cfg)?;
    state.config = cfg; Ok(())
}
pub fn check(root: &Path) {
    {
        let mut state = session(root).lock().unwrap();
        if state.busy { return; } state.busy = true; state.message = "Checking the public mod catalog...".into();
    }
    let root = root.to_path_buf();
    std::thread::spawn(move || {
        let result = fetch_catalog(&root, &crate::http::WinHttp);
        let mut state = session(&root).lock().unwrap(); state.busy = false;
        match result {
            Ok(cat) => { state.catalog = Some(cat); state.message = "Catalog refreshed. Updates install at the next game launch.".into(); }
            Err(error) => state.message = format!("Could not check updates: {error}"),
        }
    });
}

pub fn set_message(root: &Path, message: String) { session(root).lock().unwrap().message = message; }

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn first_bootstrap_launch_enrolls_existing_public_mods_only() {
        let root = std::env::temp_dir().join(format!("jibbers-enroll-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        std::fs::create_dir_all(root.join("JibbersMods")).unwrap();
        std::fs::write(root.join("JibbersMods/example.dll"), b"existing public mod").unwrap();
        std::fs::write(root.join("JibbersMods/local_only.dll"), b"private mod").unwrap();
        let cat: Catalog = serde_json::from_value(serde_json::json!({"schema":1,"sequence":1,"packages":[{"id":"example","name":"Example","description":"","version":"1.0.0","files":[{"path":"JibbersMods/example.dll","url":"https://example.com/mod.bin","size":1,"sha256":"unused in enrollment"}]}]})).unwrap();
        enroll_legacy(&root, &cat).unwrap();
        assert_eq!(config(&root).unwrap().wanted, ["example"]);
        let record: Installed = install::read_json(&path(&root, "installed.json").unwrap()).unwrap();
        assert_eq!(record.packages["example"].version, "0.0.0");
        assert_eq!(record.packages["example"].files[0].sha256, catalog::hash(b"existing public mod"));
        assert!(!record.packages.contains_key("local_only"));
        assert_eq!(std::fs::read(root.join("JibbersMods/local_only.dll")).unwrap(), b"private mod");
        assert!(root.starts_with(std::env::temp_dir())); std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn only_dlls_matching_signed_catalog_files_are_verified() {
        let root = std::env::temp_dir().join(format!("jibbers-unverified-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        for dir in ["JibbersMods/cs/Sub", "JibbersMods/cs/Empty"] { std::fs::create_dir_all(root.join(dir)).unwrap(); }
        let write = |rel: &str, bytes: &[u8]| std::fs::write(root.join(rel), bytes).unwrap();
        write("JibbersMods/example.dll", b"catalog mod");
        write("JibbersMods/old.dll", b"old catalog mod");
        write("JibbersMods/legacy.dll", b"enrolled from disk");
        write("JibbersMods/tampered.dll", b"edited");
        write("JibbersMods/local.dll", b"hand installed");
        write("JibbersMods/notes.txt", b"not a dll");
        write("JibbersMods/cs/Loose.dll", b"loose C# mod");
        write("JibbersMods/cs/Sub/Sub.dll", b"folder C# mod");
        write("JibbersMods/cs/Sub/Dependency.dll", b"loaded by Sub, not by the SDK");
        let file = |path: &str, bytes: &[u8]| catalog::File { path: path.into(), url: "https://example.com/x".into(), size: bytes.len() as u64, sha256: catalog::hash(bytes) };
        let mut installed = Installed::default();
        installed.packages.insert("old".into(), install::InstalledPackage { version: "1.0.0".into(), files: vec![file("JibbersMods/old.dll", b"old catalog mod")] });
        installed.packages.insert("legacy".into(), install::InstalledPackage { version: "0.0.0".into(), files: vec![file("JibbersMods/legacy.dll", b"enrolled from disk")] });
        installed.packages.insert("tampered".into(), install::InstalledPackage { version: "1.0.0".into(), files: vec![file("JibbersMods/tampered.dll", b"original")] });
        installed.packages.insert("loose".into(), install::InstalledPackage { version: "1.0.0".into(), files: vec![file("JibbersMods/cs/Loose.dll", b"loose C# mod")] });
        install::write_json(&path(&root, "installed.json").unwrap(), &installed).unwrap();
        // No signed catalog is cached here, so example.dll has nothing vouching for it.
        assert_eq!(unverified_mods(&root), ["JibbersMods/example.dll", "JibbersMods/legacy.dll", "JibbersMods/local.dll", "JibbersMods/tampered.dll", "JibbersMods/cs/Sub/Sub.dll"]);
        assert!(root.starts_with(std::env::temp_dir())); std::fs::remove_dir_all(root).unwrap();
    }
}
