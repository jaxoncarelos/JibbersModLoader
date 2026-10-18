//! The updater: fetch the signed manifest, and if it is newer than what is installed, download,
//! verify and install the files it lists. Pure logic over a [`Fetch`] implementation, so it is
//! tested without a network.
//!
//! Safety properties:
//! - Nothing is parsed or downloaded on trust: the manifest signature is checked first
//!   (see `manifest`), then every file's size and SHA-256.
//! - Only a *greater* `sdkVersion` installs, so replaying an older signed release cannot downgrade.
//! - All files are downloaded and verified into a staging folder before any installed file is
//!   touched; if replacing fails part-way, the files already replaced are restored.
//! - Installation is confined to `JibbersMods\jibbers_sdk.dll` and `JibbersMods\sdk\`.
//! - The running DLL is a temp copy made by the loader, so replacing the file on disk is safe.
//!   The new runtime takes effect at the next game start; docs and sources are usable at once.

use crate::manifest::{self, Manifest};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::path::{Path, PathBuf};

pub trait Fetch {
    /// GET `url`; fail on any non-200 status or a body over `max` bytes.
    fn get(&self, url: &str, max: usize) -> Result<Vec<u8>, String>;
}

pub struct Config<'a> {
    /// Where to look, in order: the host the last release named, then the compiled-in default.
    pub base_urls: Vec<String>,
    /// `<game>\JibbersMods`.
    pub mods_dir: PathBuf,
    pub keys: &'a [[u8; 32]],
    /// ABI of the loader this runtime is running under.
    pub loader_abi: u32,
}

/// `JibbersMods\sdk\installed.json`: what the last successful update installed.
#[derive(Serialize, Deserialize, Default, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Installed {
    pub sdk_version: String,
    pub files: Vec<String>,
    #[serde(default)]
    pub base_url: Option<String>,
    #[serde(default)]
    pub updated: String,
}

#[derive(Clone, Debug, PartialEq)]
pub enum Outcome {
    UpToDate { version: String },
    Updated { from: String, to: String, downloaded: usize, restart: bool },
    /// A release exists but must not be installed (needs a newer loader).
    Skipped(String),
    Failed(String),
}

impl Outcome {
    pub fn to_json(&self) -> Value {
        match self {
            Outcome::UpToDate { version } => json!({"state": "upToDate", "version": version}),
            Outcome::Updated { from, to, downloaded, restart } => json!({"state": "updated", "from": from, "to": to, "downloaded": downloaded, "restartRequired": restart}),
            Outcome::Skipped(why) => json!({"state": "skipped", "reason": why}),
            Outcome::Failed(why) => json!({"state": "failed", "error": why}),
        }
    }
}

pub fn installed_path(mods_dir: &Path) -> PathBuf {
    mods_dir.join("sdk").join("installed.json")
}

pub fn read_installed(mods_dir: &Path) -> Option<Installed> {
    serde_json::from_slice(&std::fs::read(installed_path(mods_dir)).ok()?).ok()
}

fn joined(base: &str, name: &str) -> String {
    format!("{}/{}", base.trim_end_matches('/'), name)
}

/// Fetches and verifies the manifest from the first base URL that answers with a validly signed one.
fn fetch_manifest(cfg: &Config, net: &dyn Fetch) -> Result<(Manifest, String), String> {
    let mut errors = vec![];
    for base in &cfg.base_urls {
        let attempt = (|| {
            let bytes = net.get(&joined(base, "manifest.json"), manifest::MAX_MANIFEST_BYTES)?;
            let sig = net.get(&joined(base, "manifest.json.sig"), 1024)?;
            manifest::parse_signed(cfg.keys, &bytes, &String::from_utf8_lossy(&sig))
        })();
        match attempt {
            Ok(m) => return Ok((m, base.clone())),
            Err(e) => errors.push(format!("{base}: {e}")),
        }
    }
    Err(if errors.is_empty() { "no update URL configured".into() } else { errors.join("; ") })
}

fn file_hash(path: &Path) -> Option<String> {
    std::fs::read(path).ok().map(|b| manifest::sha256_hex(&b))
}

fn write_atomic(path: &Path, bytes: &[u8]) -> Result<(), String> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| format!("{}: {e}", dir.display()))?;
    }
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, bytes).map_err(|e| format!("{}: {e}", tmp.display()))?;
    std::fs::rename(&tmp, path).map_err(|e| {
        let _ = std::fs::remove_file(&tmp);
        format!("{}: {e}", path.display())
    })
}

/// One check, and the update if there is one. Never panics on bad network data.
pub fn check_and_apply(cfg: &Config, net: &dyn Fetch) -> Outcome {
    let installed = read_installed(&cfg.mods_dir).unwrap_or_default();
    let have_text = if installed.sdk_version.is_empty() { "0.0.0".to_string() } else { installed.sdk_version.clone() };
    let Some(have) = manifest::parse_version(&have_text) else {
        return Outcome::Failed(format!("installed.json has a bad version {have_text:?}"));
    };
    let (m, base) = match fetch_manifest(cfg, net) {
        Ok(x) => x,
        Err(e) => return Outcome::Failed(e),
    };
    if m.min_loader_abi > cfg.loader_abi {
        return Outcome::Skipped(format!("SDK {} needs loader ABI {}, this loader is ABI {}: update the loader (version.dll)", m.sdk_version, m.min_loader_abi, cfg.loader_abi));
    }
    // validate() has already parsed this
    let new = manifest::parse_version(&m.sdk_version).unwrap_or((0, 0, 0));
    if new <= have {
        return Outcome::UpToDate { version: have_text };
    }
    match apply(cfg, net, &m, &base, &installed) {
        Ok((downloaded, restart)) => Outcome::Updated { from: have_text, to: m.sdk_version.clone(), downloaded, restart },
        Err(e) => Outcome::Failed(e),
    }
}

/// Returns how many files were downloaded and whether one of them was the runtime DLL.
fn apply(cfg: &Config, net: &dyn Fetch, m: &Manifest, base: &str, old: &Installed) -> Result<(usize, bool), String> {
    let sdk_dir = cfg.mods_dir.join("sdk");
    let staging = sdk_dir.join(".staging");
    let _ = std::fs::remove_dir_all(&staging);
    std::fs::create_dir_all(&staging).map_err(|e| format!("{}: {e}", staging.display()))?;
    let result = stage_and_commit(cfg, net, m, base, old, &staging);
    let _ = std::fs::remove_dir_all(&staging);
    result
}

fn stage_and_commit(cfg: &Config, net: &dyn Fetch, m: &Manifest, base: &str, old: &Installed, staging: &Path) -> Result<(usize, bool), String> {
    // 1. Download and verify everything that differs from what is on disk. Nothing installed yet.
    let mut staged: Vec<(PathBuf, PathBuf, &manifest::FileEntry)> = vec![];
    for (i, f) in m.files.iter().enumerate() {
        let dest = cfg.mods_dir.join(&f.path);
        if file_hash(&dest).as_deref() == Some(f.sha256.as_str()) {
            continue;
        }
        let data = net.get(&joined(base, &f.asset), f.size as usize + 1).map_err(|e| format!("{}: {e}", f.asset))?;
        if data.len() as u64 != f.size || manifest::sha256_hex(&data) != f.sha256 {
            return Err(format!("{} does not match the signed manifest; nothing was installed", f.asset));
        }
        if f.path == manifest::RUNTIME_PATH && !data.starts_with(b"MZ") {
            return Err(format!("{} is not a Windows DLL; nothing was installed", f.asset));
        }
        let part = staging.join(format!("{i}.part"));
        write_atomic(&part, &data)?;
        staged.push((part, dest, f));
    }
    // 2. Replace. Keep each overwritten file so a failure part-way can be undone.
    let mut done: Vec<(PathBuf, Option<PathBuf>)> = vec![];
    let commit = (|| -> Result<(), String> {
        for (n, (part, dest, f)) in staged.iter().enumerate() {
            if let Some(dir) = dest.parent() {
                std::fs::create_dir_all(dir).map_err(|e| format!("{}: {e}", dir.display()))?;
            }
            let undo = dest.exists().then(|| staging.join(format!("{n}.old")));
            if let Some(undo) = &undo {
                std::fs::copy(dest, undo).map_err(|e| format!("backup of {}: {e}", f.path))?;
                if f.path == manifest::RUNTIME_PATH {
                    let _ = std::fs::copy(dest, dest.with_extension("dll.bak"));
                }
            }
            std::fs::rename(part, dest).map_err(|e| format!("replacing {}: {e}", f.path))?;
            done.push((dest.clone(), undo));
        }
        Ok(())
    })();
    if let Err(e) = commit {
        for (dest, undo) in done.iter().rev() {
            match undo {
                Some(u) => {
                    let _ = std::fs::copy(u, dest);
                }
                None => {
                    let _ = std::fs::remove_file(dest);
                }
            }
        }
        return Err(format!("{e}; previous files restored"));
    }
    // 3. Remove files the previous release had under sdk/ and this one dropped.
    for stale in &old.files {
        if stale.starts_with("sdk/") && manifest::validate_install_path(stale).is_ok() && !m.files.iter().any(|f| &f.path == stale) {
            let _ = std::fs::remove_file(cfg.mods_dir.join(stale));
        }
    }
    // 4. Record it, last, so a half-finished update is retried at the next start.
    let record = Installed {
        sdk_version: m.sdk_version.clone(),
        files: m.files.iter().map(|f| f.path.clone()).collect(),
        base_url: m.base_url.clone().or_else(|| old.base_url.clone()),
        updated: m.published.clone(),
    };
    write_atomic(&installed_path(&cfg.mods_dir), serde_json::to_string_pretty(&record).unwrap_or_default().as_bytes())?;
    Ok((staged.len(), staged.iter().any(|(_, _, f)| f.path == manifest::RUNTIME_PATH)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::manifest::{sha256_hex, FileEntry};
    use ed25519_dalek::{Signer, SigningKey};
    use std::cell::RefCell;
    use std::collections::HashMap;

    struct Fake {
        files: HashMap<String, Vec<u8>>,
        requests: RefCell<Vec<String>>,
    }
    impl Fetch for Fake {
        fn get(&self, url: &str, max: usize) -> Result<Vec<u8>, String> {
            self.requests.borrow_mut().push(url.to_string());
            let b = self.files.get(url).cloned().ok_or_else(|| format!("HTTP 404 {url}"))?;
            if b.len() > max {
                return Err("too big".into());
            }
            Ok(b)
        }
    }

    const BASE: &str = "https://example.test/rel";
    const DLL: &[u8] = b"MZ new runtime";

    fn signer() -> SigningKey {
        SigningKey::from_bytes(&[7; 32])
    }
    fn keys() -> Vec<[u8; 32]> {
        vec![signer().verifying_key().to_bytes()]
    }

    /// A release at `version` with the given (path, content) files.
    fn release(base: &str, version: &str, files: &[(&str, &[u8])]) -> Fake {
        let entries: Vec<FileEntry> = files
            .iter()
            .map(|(p, c)| FileEntry { path: p.to_string(), asset: p.replace('/', "__"), size: c.len() as u64, sha256: sha256_hex(c) })
            .collect();
        let m = Manifest { schema: 1, sdk_version: version.into(), min_loader_abi: 4, published: "2026-10-18T00:00:00Z".into(), notes: String::new(), base_url: None, files: entries.clone() };
        let bytes = serde_json::to_vec_pretty(&m).unwrap();
        let sig: String = signer().sign(&bytes).to_bytes().iter().map(|b| format!("{b:02x}")).collect();
        let mut map = HashMap::new();
        map.insert(format!("{base}/manifest.json"), bytes);
        map.insert(format!("{base}/manifest.json.sig"), sig.into_bytes());
        for (e, (_, c)) in entries.iter().zip(files) {
            map.insert(format!("{base}/{}", e.asset), c.to_vec());
        }
        Fake { files: map, requests: RefCell::new(vec![]) }
    }

    fn temp(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("jibbers-sdk-upd-{}-{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn cfg<'a>(dir: &Path, keys: &'a [[u8; 32]]) -> Config<'a> {
        Config { base_urls: vec![BASE.into()], mods_dir: dir.to_path_buf(), keys, loader_abi: 4 }
    }

    const FILES: &[(&str, &[u8])] = &[("jibbers_sdk.dll", DLL), ("sdk/docs/SDK.md", b"# docs v2"), ("sdk/docs/RUNTIME.md", b"runtime v2")];

    #[test]
    fn fresh_install_downloads_verifies_and_records() {
        let dir = temp("fresh");
        let k = keys();
        let out = check_and_apply(&cfg(&dir, &k), &release(BASE, "0.2.0", FILES));
        assert_eq!(out, Outcome::Updated { from: "0.0.0".into(), to: "0.2.0".into(), downloaded: 3, restart: true });
        assert_eq!(std::fs::read(dir.join("jibbers_sdk.dll")).unwrap(), DLL);
        assert_eq!(std::fs::read(dir.join("sdk/docs/SDK.md")).unwrap(), b"# docs v2");
        let rec = read_installed(&dir).unwrap();
        assert_eq!(rec.sdk_version, "0.2.0");
        assert_eq!(rec.files.len(), 3);
        assert!(!dir.join("sdk/.staging").exists(), "staging cleaned up");
    }

    #[test]
    fn same_or_older_releases_do_nothing() {
        let dir = temp("uptodate");
        let k = keys();
        check_and_apply(&cfg(&dir, &k), &release(BASE, "0.2.0", FILES));
        let net = release(BASE, "0.2.0", FILES);
        assert_eq!(check_and_apply(&cfg(&dir, &k), &net), Outcome::UpToDate { version: "0.2.0".into() });
        assert_eq!(net.requests.borrow().len(), 2, "only the manifest and signature are fetched");
        // a validly signed but older release (replay) must not downgrade
        let old = release(BASE, "0.1.0", &[("jibbers_sdk.dll", b"MZ old"), ("sdk/docs/SDK.md", b"old")]);
        assert_eq!(check_and_apply(&cfg(&dir, &k), &old), Outcome::UpToDate { version: "0.2.0".into() });
        assert_eq!(std::fs::read(dir.join("sdk/docs/SDK.md")).unwrap(), b"# docs v2");
    }

    #[test]
    fn unchanged_files_are_not_downloaded_again_and_old_files_are_backed_up_and_removed() {
        let dir = temp("incremental");
        let k = keys();
        check_and_apply(&cfg(&dir, &k), &release(BASE, "0.2.0", FILES));
        let next = release(BASE, "0.3.0", &[("jibbers_sdk.dll", b"MZ v3"), ("sdk/docs/SDK.md", b"# docs v2"), ("sdk/docs/NEW.md", b"new")]);
        let out = check_and_apply(&cfg(&dir, &k), &next);
        assert_eq!(out, Outcome::Updated { from: "0.2.0".into(), to: "0.3.0".into(), downloaded: 2, restart: true });
        assert!(!next.requests.borrow().iter().any(|u| u.ends_with("sdk__docs__SDK.md")), "unchanged file skipped");
        assert_eq!(std::fs::read(dir.join("jibbers_sdk.dll.bak")).unwrap(), DLL, "previous runtime kept");
        assert!(!dir.join("sdk/docs/RUNTIME.md").exists(), "file dropped by the new release is removed");
        assert!(dir.join("sdk/docs/NEW.md").exists());

        // a docs-only release needs no restart
        let docs = release(BASE, "0.3.1", &[("jibbers_sdk.dll", b"MZ v3"), ("sdk/docs/SDK.md", b"# docs v3")]);
        assert_eq!(check_and_apply(&cfg(&dir, &k), &docs), Outcome::Updated { from: "0.3.0".into(), to: "0.3.1".into(), downloaded: 1, restart: false });
    }

    #[test]
    fn untrusted_or_tampered_releases_install_nothing() {
        let dir = temp("untrusted");
        let other = vec![SigningKey::from_bytes(&[9; 32]).verifying_key().to_bytes()];
        let out = check_and_apply(&cfg(&dir, &other), &release(BASE, "0.2.0", FILES));
        assert!(matches!(&out, Outcome::Failed(e) if e.contains("trusted key")), "{out:?}");
        assert!(!dir.join("jibbers_sdk.dll").exists());

        // an asset swapped on the host after signing
        let k = keys();
        let mut net = release(BASE, "0.2.0", FILES);
        net.files.insert(format!("{BASE}/sdk__docs__SDK.md"), b"# evil!!!".to_vec());
        let out = check_and_apply(&cfg(&dir, &k), &net);
        assert!(matches!(&out, Outcome::Failed(e) if e.contains("does not match")), "{out:?}");
        assert!(!dir.join("jibbers_sdk.dll").exists(), "nothing installed, not even the valid files");
        assert!(read_installed(&dir).is_none());

        // an oversized asset
        let mut net = release(BASE, "0.2.0", FILES);
        net.files.insert(format!("{BASE}/jibbers_sdk.dll"), vec![b'M'; 1000]);
        assert!(matches!(check_and_apply(&cfg(&dir, &k), &net), Outcome::Failed(_)));

        // a runtime that isn't a DLL, even though signed and hash-correct
        let net = release(BASE, "0.2.0", &[("jibbers_sdk.dll", b"#!/bin/sh")]);
        assert!(matches!(check_and_apply(&cfg(&dir, &k), &net), Outcome::Failed(e) if e.contains("not a Windows DLL")));
    }

    #[test]
    fn failures_are_reported_not_fatal() {
        let dir = temp("offline");
        let k = keys();
        let empty = Fake { files: HashMap::new(), requests: RefCell::new(vec![]) };
        assert!(matches!(check_and_apply(&cfg(&dir, &k), &empty), Outcome::Failed(_)));
        let none = Config { base_urls: vec![], ..cfg(&dir, &k) };
        assert!(matches!(check_and_apply(&none, &empty), Outcome::Failed(e) if e.contains("no update URL")));
    }

    #[test]
    fn falls_back_to_the_next_base_url_and_skips_releases_needing_a_newer_loader() {
        let dir = temp("fallback");
        let k = keys();
        let mut c = cfg(&dir, &k);
        c.base_urls = vec!["https://dead.test/x".into(), BASE.into()];
        assert!(matches!(check_and_apply(&c, &release(BASE, "0.2.0", FILES)), Outcome::Updated { .. }));

        let dir = temp("abi");
        let mut c = cfg(&dir, &k);
        c.loader_abi = 3;
        let out = check_and_apply(&c, &release(BASE, "0.2.0", FILES));
        assert!(matches!(&out, Outcome::Skipped(e) if e.contains("ABI")), "{out:?}");
        assert!(!dir.join("jibbers_sdk.dll").exists());
    }

    #[test]
    fn a_failed_replace_restores_what_was_replaced() {
        let dir = temp("rollback");
        let k = keys();
        check_and_apply(&cfg(&dir, &k), &release(BASE, "0.2.0", FILES));
        // The second file's destination is a directory, so renaming a file over it fails after the
        // first file (the DLL) has already been replaced.
        std::fs::remove_file(dir.join("sdk/docs/SDK.md")).unwrap();
        std::fs::create_dir_all(dir.join("sdk/docs/SDK.md")).unwrap();
        let next = release(BASE, "0.3.0", &[("jibbers_sdk.dll", b"MZ v3"), ("sdk/docs/SDK.md", b"v3")]);
        let out = check_and_apply(&cfg(&dir, &k), &next);
        assert!(matches!(&out, Outcome::Failed(e) if e.contains("restored")), "{out:?}");
        assert_eq!(std::fs::read(dir.join("jibbers_sdk.dll")).unwrap(), DLL, "DLL rolled back");
        assert_eq!(read_installed(&dir).unwrap().sdk_version, "0.2.0", "still recorded as the old version");
    }
}
