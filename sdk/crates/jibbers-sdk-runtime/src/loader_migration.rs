//! Bridge for older SDK updaters. They already accept signed files under sdk/, so a release can
//! deliver the new bootstrap there without widening the old manifest's installation permissions.
//! A verified helper waits for Jibbers to exit before atomically replacing the locked proxy DLL.
use crate::manifest;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const DIRECTORY: &str = "sdk/loader";
pub const BOOTSTRAP: &str = "version.dll";
pub const IMPLEMENTATION: &str = "jibbers_loader.dll";
pub const HELPER: &str = "jibbers-migrate.exe";

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Plan { pub schema: u32, pub version: String, pub files: Vec<Payload> }
#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(deny_unknown_fields)]
pub struct Payload { pub name: String, pub size: u64, pub sha256: String }

fn checked(root: &Path, relative: &str) -> Result<PathBuf, String> {
    if !root.is_absolute() || relative.split('/').any(|p| p.is_empty() || matches!(p, "." | "..") || p.contains(['\\', ':'])) { return Err("unsafe loader migration path".into()); }
    let mut path = root.to_path_buf();
    for part in relative.split('/') {
        path.push(part);
        match std::fs::symlink_metadata(&path) {
            Ok(meta) => {
                if meta.file_type().is_symlink() { return Err("loader path contains a symlink".into()); }
                #[cfg(windows)]
                { use std::os::windows::fs::MetadataExt; if meta.file_attributes() & 0x400 != 0 { return Err("loader path contains a reparse point".into()); } }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.to_string()),
        }
    }
    Ok(path)
}

pub fn verify(game: &Path, keys: &[[u8; 32]]) -> Result<Plan, String> {
    let staged = checked(game, &format!("JibbersMods/{DIRECTORY}"))?;
    let bytes = std::fs::read(staged.join("install.json")).map_err(|e| e.to_string())?;
    if bytes.len() > 16 * 1024 { return Err("loader install plan too large".into()); }
    let signature = std::fs::read_to_string(staged.join("install.json.sig")).map_err(|e| e.to_string())?;
    manifest::verify_signature(keys, &bytes, &signature)?;
    let plan: Plan = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
    if plan.schema != 1 || manifest::parse_version(&plan.version).is_none() || plan.files.len() != 3 { return Err("invalid loader install plan".into()); }
    for name in [BOOTSTRAP, IMPLEMENTATION, HELPER] {
        let file = plan.files.iter().find(|p| p.name == name).ok_or("loader install plan is incomplete")?;
        if file.size == 0 || file.size > manifest::MAX_FILE_BYTES { return Err("invalid loader payload size".into()); }
        let path = checked(game, &format!("JibbersMods/{DIRECTORY}/{name}"))?;
        let bytes = std::fs::read(path).map_err(|e| e.to_string())?;
        if bytes.len() as u64 != file.size || !bytes.starts_with(b"MZ") || manifest::sha256_hex(&bytes) != file.sha256 { return Err(format!("loader payload {name} failed verification")); }
    }
    Ok(plan)
}

pub fn needed(game: &Path, plan: &Plan) -> bool {
    let expected = plan.files.iter().find(|f| f.name == BOOTSTRAP).map(|f| f.sha256.as_str());
    // Once this bootstrap is installed, its implementation can move ahead through the public
    // channel. Never repeatedly overwrite that newer implementation with the migration seed.
    std::fs::read(game.join(BOOTSTRAP)).ok().map(|b| manifest::sha256_hex(&b)).as_deref() != expected
        || !game.join("JibbersMods/system/jibbers_loader.dll").exists()
}

#[cfg(test)]
mod tests {
    use super::*;
    use ed25519_dalek::{Signer, SigningKey};
    struct Fixture { root: PathBuf, keys: Vec<[u8; 32]> }
    impl Fixture {
        fn new() -> Self {
            static ID: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
            let root = std::env::temp_dir().join(format!("jibbers-migration-{}-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos(), ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)));
            let staged = root.join(format!("JibbersMods/{DIRECTORY}")); std::fs::create_dir_all(&staged).unwrap();
            let key = SigningKey::from_bytes(&[87;32]);
            let mut files = vec![];
            for name in [BOOTSTRAP, IMPLEMENTATION, HELPER] {
                let bytes = format!("MZ {name} new").into_bytes(); std::fs::write(staged.join(name), &bytes).unwrap();
                files.push(Payload { name: name.into(), size: bytes.len() as u64, sha256: manifest::sha256_hex(&bytes) });
            }
            let plan = serde_json::to_vec(&Plan { schema: 1, version: "0.2.0".into(), files }).unwrap();
            let sig: String = key.sign(&plan).to_bytes().iter().map(|b| format!("{b:02x}")).collect();
            std::fs::write(staged.join("install.json"), plan).unwrap(); std::fs::write(staged.join("install.json.sig"), sig).unwrap();
            std::fs::write(root.join(BOOTSTRAP), b"MZ old loader").unwrap();
            Self { root, keys: vec![key.verifying_key().to_bytes()] }
        }
    }
    impl Drop for Fixture { fn drop(&mut self) { assert!(self.root.starts_with(std::env::temp_dir())); let _ = std::fs::remove_dir_all(&self.root); } }
    #[test]
    fn verified_migration_backs_up_old_proxy_and_preserves_settings() {
        let f = Fixture::new(); std::fs::write(f.root.join("jibbers_atmosphere.json"), b"my settings").unwrap();
        assert!(apply(&f.root, &f.keys).unwrap());
        assert_eq!(std::fs::read(f.root.join(BOOTSTRAP)).unwrap(), b"MZ version.dll new");
        assert_eq!(std::fs::read(f.root.join("JibbersMods/system/version.dll.before-sdk-migration")).unwrap(), b"MZ old loader");
        assert_eq!(std::fs::read(f.root.join("jibbers_atmosphere.json")).unwrap(), b"my settings");
        assert!(!apply(&f.root, &f.keys).unwrap());
    }
    #[test]
    fn tampered_payload_and_signature_never_change_proxy() {
        let f = Fixture::new();
        assert!(verify(&f.root, &[[0;32]]).is_err());
        std::fs::write(f.root.join(format!("JibbersMods/{DIRECTORY}/{HELPER}")), b"MZ tampered").unwrap();
        assert!(apply(&f.root, &f.keys).is_err());
        assert_eq!(std::fs::read(f.root.join(BOOTSTRAP)).unwrap(), b"MZ old loader");
    }
    #[test]
    fn does_not_downgrade_implementation_after_bootstrap_migration() {
        let f = Fixture::new(); apply(&f.root, &f.keys).unwrap();
        let implementation = f.root.join("JibbersMods/system/jibbers_loader.dll");
        std::fs::write(&implementation, b"MZ newer public channel implementation").unwrap();
        assert!(!apply(&f.root, &f.keys).unwrap());
        assert_eq!(std::fs::read(implementation).unwrap(), b"MZ newer public channel implementation");
    }
    #[cfg(windows)]
    #[test]
    fn locked_proxy_stays_intact_and_installation_retries_after_exit() {
        use std::os::windows::fs::OpenOptionsExt;
        let f = Fixture::new();
        let locked = std::fs::OpenOptions::new().read(true).share_mode(3).open(f.root.join(BOOTSTRAP)).unwrap();
        assert!(apply(&f.root, &f.keys).is_err());
        assert_eq!(std::fs::read(f.root.join(BOOTSTRAP)).unwrap(), b"MZ old loader");
        drop(locked);
        assert!(apply(&f.root, &f.keys).unwrap());
    }
}

pub fn schedule(game: &Path, keys: &[[u8; 32]]) -> Result<bool, String> {
    if !game.join(format!("JibbersMods/{DIRECTORY}/install.json")).exists() { return Ok(false); }
    let plan = verify(game, keys)?;
    if !needed(game, &plan) { return Ok(false); }
    let bytes = std::fs::read(checked(game, &format!("JibbersMods/{DIRECTORY}/{HELPER}"))?).map_err(|e| e.to_string())?;
    // Run a private copy; an updater can still replace the original helper on disk next time.
    let helper = checked(game, &format!("JibbersMods/{DIRECTORY}/running/migrate-{}.exe", std::process::id()))?;
    std::fs::create_dir_all(helper.parent().unwrap()).map_err(|e| e.to_string())?;
    std::fs::write(&helper, bytes).map_err(|e| e.to_string())?;
    let mut command = std::process::Command::new(helper);
    command.arg("--game").arg(game).arg("--wait-pid").arg(std::process::id().to_string());
    #[cfg(windows)]
    { use std::os::windows::process::CommandExt; command.creation_flags(0x08000000); }
    command.spawn().map_err(|e| e.to_string())?;
    Ok(true)
}

fn replace(path: &Path, bytes: &[u8]) -> Result<(), String> {
    use std::io::Write;
    std::fs::create_dir_all(path.parent().ok_or("missing loader destination")?).map_err(|e| e.to_string())?;
    let temp = path.with_extension("migration-new");
    let mut file = std::fs::File::create(&temp).map_err(|e| e.to_string())?;
    file.write_all(bytes).and_then(|_| file.sync_all()).map_err(|e| e.to_string())?;
    drop(file);
    std::fs::rename(temp, path).map_err(|e| format!("{}: {e}", path.display()))
}

/// Only the helper calls this, after waiting for the launching game process to terminate.
pub fn apply(game: &Path, keys: &[[u8; 32]]) -> Result<bool, String> {
    let plan = verify(game, keys)?;
    if !needed(game, &plan) { return Ok(false); }
    let source = checked(game, &format!("JibbersMods/{DIRECTORY}"))?;
    // Install the implementation first. A crash before swapping the proxy leaves the old loader
    // working; after the atomic proxy replacement the new implementation is already present.
    let implementation = checked(game, "JibbersMods/system/jibbers_loader.dll")?;
    replace(&implementation, &std::fs::read(source.join(IMPLEMENTATION)).map_err(|e| e.to_string())?)?;
    let proxy = checked(game, BOOTSTRAP)?;
    if proxy.exists() {
        let previous = checked(game, "JibbersMods/system/version.dll.before-sdk-migration")?;
        if !previous.exists() { replace(&previous, &std::fs::read(&proxy).map_err(|e| e.to_string())?)?; }
    }
    replace(&proxy, &std::fs::read(source.join(BOOTSTRAP)).map_err(|e| e.to_string())?)?;
    Ok(true)
}
