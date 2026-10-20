use crate::catalog::{hash, version, Catalog, File, Package};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub trait Fetch { fn get(&self, url: &str, max: usize) -> Result<Vec<u8>, String>; }

#[derive(Clone, Default, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Installed {
    pub sequence: u64,
    pub packages: BTreeMap<String, InstalledPackage>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct InstalledPackage { pub version: String, pub files: Vec<File> }

pub fn read_json<T: serde::de::DeserializeOwned>(path: &Path) -> Result<T, String> {
    serde_json::from_slice(&std::fs::read(path).map_err(|e| e.to_string())?).map_err(|e| e.to_string())
}
pub fn write_json(path: &Path, value: &impl Serialize) -> Result<(), String> {
    atomic_write(path, &serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?)
}
pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let tmp = path.with_extension("new");
    let parent = path.parent().ok_or("path has no parent")?;
    std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    let mut file = std::fs::File::create(&tmp).map_err(|e| e.to_string())?;
    use std::io::Write;
    file.write_all(bytes).and_then(|_| file.sync_all()).map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, path).map_err(|e| format!("{}: {e}", path.display()))
}

/// Reject junctions/symlinks and path escape before every write, including local state paths.
pub fn safe_path(root: &Path, rel: &str) -> Result<PathBuf, String> {
    if !root.is_absolute() || rel.is_empty() || rel.contains(['\\', ':']) || rel.starts_with('/') { return Err("invalid installation root or path".into()); }
    let mut out = root.to_path_buf();
    for part in rel.split('/') {
        if part.is_empty() || matches!(part, "." | "..") { return Err("path traversal".into()); }
        out.push(part);
        match std::fs::symlink_metadata(&out) {
            Ok(meta) => {
                #[cfg(windows)]
                { use std::os::windows::fs::MetadataExt; if meta.file_attributes() & 0x400 != 0 { return Err("install path contains a reparse point".into()); } }
                if meta.file_type().is_symlink() { return Err("install path contains a symlink".into()); }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.to_string()),
        }
    }
    Ok(out)
}

pub struct Lock { path: PathBuf, _file: std::fs::File }
impl Lock {
    pub fn acquire(root: &Path) -> Result<Self, String> {
        let path = safe_path(root, &format!("{}/install.lock", crate::STATE_DIR))?;
        std::fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
        // File sharing, rather than a stale sentinel, releases the lock when the process exits.
        #[cfg(windows)]
        let file = { use std::os::windows::fs::OpenOptionsExt; std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).share_mode(0).open(&path) };
        #[cfg(not(windows))]
        let file = std::fs::OpenOptions::new().create_new(true).write(true).open(&path);
        Ok(Self { path, _file: file.map_err(|_| "another installer is running")? })
    }
}
impl Drop for Lock {
    fn drop(&mut self) {
        #[cfg(not(windows))]
        let _ = std::fs::remove_file(&self.path);
        #[cfg(windows)]
        let _ = &self.path;
    }
}

#[derive(Serialize, Deserialize)]
struct Change { path: String, old: Option<String>, new: Option<String> }
#[derive(Serialize, Deserialize)]
struct Journal { changes: Vec<Change> }

fn state(root: &Path, name: &str) -> Result<PathBuf, String> { safe_path(root, &format!("{}/{name}", crate::STATE_DIR)) }

/// Undo an interrupted transaction before loading any plugin. Backups are retained if recovery
/// fails; the bootstrap must not load a partially installed runtime.
pub fn recover(root: &Path) -> Result<(), String> {
    let journal = state(root, "journal.json")?;
    if !journal.exists() { return Ok(()); }
    let j: Journal = read_json(&journal)?;
    rollback(root, &j)?;
    std::fs::remove_file(journal).map_err(|e| e.to_string())?;
    cleanup(root, &j);
    Ok(())
}
fn rollback(root: &Path, journal: &Journal) -> Result<(), String> {
    for c in journal.changes.iter().rev() {
        let dest = safe_path(root, &c.path)?;
        if let Some(old) = &c.old {
            let backup = std::fs::read(safe_path(root, old)?).map_err(|e| e.to_string())?;
            // A locked destination may be the write that failed. If it already contains the
            // old bytes, leave it alone and continue restoring the other files.
            if std::fs::read(&dest).ok().as_ref() != Some(&backup) { atomic_write(&dest, &backup)?; }
        }
        else if dest.exists() { std::fs::remove_file(&dest).map_err(|e| e.to_string())?; }
    }
    Ok(())
}
fn cleanup(root: &Path, journal: &Journal) {
    for c in &journal.changes {
        for file in [&c.old, &c.new].into_iter().flatten() {
            if let Ok(path) = safe_path(root, file) { let _ = std::fs::remove_file(path); }
        }
    }
}

/// Apply only before the replaceable loader and plugins have loaded. All downloads and backup
/// files are complete before the first installed file changes. State commits last.
pub fn apply(root: &Path, cat: &Catalog, wanted: &[String], auto_update: bool, net: &dyn Fetch) -> Result<Installed, String> {
    cat.validate()?;
    let _lock = Lock::acquire(root)?;
    recover(root)?;
    let record_path = state(root, "installed.json")?;
    let old: Installed = if record_path.exists() { read_json(&record_path)? } else { Installed::default() };
    for (id, package) in &old.packages {
        version(&package.version)?;
        if package.files.iter().any(|f| !crate::catalog::valid_path(id, &f.path)) { return Err("unsafe installed-package record".into()); }
    }
    if cat.sequence < old.sequence { return Err("refusing an older catalog".into()); }
    let mut selected = wanted.to_vec();
    selected.push("loader".into());
    let packages = cat.resolve(&selected)?;
    let mut next = old.clone();
    let mut replacements: BTreeMap<String, Option<Vec<u8>>> = BTreeMap::new();
    // Do not remove a dependency while another selected package still needs it.
    for (id, installed) in &old.packages {
        if !packages.iter().any(|p| &p.id == id) {
            for f in &installed.files { remove_owned(root, f, &mut replacements)?; }
            next.packages.remove(id);
        }
    }
    for package in packages {
        let previous = old.packages.get(&package.id);
        if let Some(p) = previous {
            if version(&package.version)? < version(&p.version)? { return Err(format!("refusing to downgrade {}", package.id)); }
            if !auto_update && p.version != package.version { continue; }
            if p.version == package.version && (p.files.len() != package.files.len() || p.files.iter().any(|f| !package.files.iter().any(|new| new.path == f.path && new.sha256 == f.sha256))) {
                return Err(format!("{} changed without a version bump", package.id));
            }
        }
        stage_package(root, package, previous, net, &mut replacements)?;
        next.packages.insert(package.id.clone(), InstalledPackage { version: package.version.clone(), files: package.files.clone() });
    }
    next.sequence = cat.sequence;
    replacements.insert(format!("{}/installed.json", crate::STATE_DIR), Some(serde_json::to_vec_pretty(&next).map_err(|e| e.to_string())?));
    let mut journal = Journal { changes: vec![] };
    let stamp = format!("{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_nanos());
    let record_rel = format!("{}/installed.json", crate::STATE_DIR);
    let ordered: Vec<_> = replacements.iter().filter(|(path, _)| *path != &record_rel)
        .chain(replacements.iter().filter(|(path, _)| *path == &record_rel)).collect();
    for (i, (path, bytes)) in ordered.into_iter().enumerate() {
        let dest = safe_path(root, path)?;
        let prefix = format!("{}/transactions/{stamp}-{i}", crate::STATE_DIR);
        let old = if dest.exists() {
            let backup = format!("{prefix}.old");
            atomic_write(&safe_path(root, &backup)?, &std::fs::read(&dest).map_err(|e| e.to_string())?)?;
            Some(backup)
        } else { None };
        let new = if let Some(bytes) = bytes {
            let staged = format!("{prefix}.stage"); atomic_write(&safe_path(root, &staged)?, bytes)?; Some(staged)
        } else { None };
        journal.changes.push(Change { path: path.clone(), old, new });
    }
    write_json(&state(root, "journal.json")?, &journal)?;
    let commit = (|| {
        for c in &journal.changes {
            let dest = safe_path(root, &c.path)?;
            if let Some(new) = &c.new { atomic_write(&dest, &std::fs::read(safe_path(root, new)?).map_err(|e| e.to_string())?)?; }
            else if dest.exists() { std::fs::remove_file(dest).map_err(|e| e.to_string())?; }
        }
        Ok::<_, String>(())
    })();
    if let Err(error) = commit {
        rollback(root, &journal).map_err(|undo| format!("{error}; recovery required: {undo}"))?;
        std::fs::remove_file(state(root, "journal.json")?).map_err(|e| e.to_string())?;
        cleanup(root, &journal);
        return Err(format!("{error}; previous files restored"));
    }
    std::fs::remove_file(state(root, "journal.json")?).map_err(|e| e.to_string())?;
    cleanup(root, &journal);
    Ok(next)
}

fn remove_owned(root: &Path, file: &File, changes: &mut BTreeMap<String, Option<Vec<u8>>>) -> Result<(), String> {
    let path = safe_path(root, &file.path)?;
    if path.exists() {
        let bytes = std::fs::read(path).map_err(|e| e.to_string())?;
        if hash(&bytes) != file.sha256 { return Err(format!("{} was modified locally; leaving it untouched", file.path)); }
        changes.insert(file.path.clone(), None);
    }
    Ok(())
}

fn stage_package(root: &Path, package: &Package, previous: Option<&InstalledPackage>, net: &dyn Fetch, changes: &mut BTreeMap<String, Option<Vec<u8>>>) -> Result<(), String> {
    if let Some(old) = previous {
        for f in &old.files { if !package.files.iter().any(|new| new.path == f.path) { remove_owned(root, f, changes)?; } }
    }
    for file in &package.files {
        let path = safe_path(root, &file.path)?;
        if path.exists() {
            let existing = std::fs::read(&path).map_err(|e| e.to_string())?;
            if hash(&existing) == file.sha256 { continue; }
            let owned = previous.and_then(|p| p.files.iter().find(|f| f.path == file.path));
            if !owned.is_some_and(|old| hash(&existing) == old.sha256) { return Err(format!("{} is an unmanaged or modified file; leaving it untouched", file.path)); }
        }
        let bytes = net.get(&file.url, file.size as usize)?;
        if bytes.len() as u64 != file.size || hash(&bytes) != file.sha256 { return Err(format!("{} failed download verification", file.path)); }
        changes.insert(file.path.clone(), Some(bytes));
    }
    Ok(())
}
