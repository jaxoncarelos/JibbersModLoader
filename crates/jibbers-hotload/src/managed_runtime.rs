//! Expands the signed C# runtime into a versioned cache before the bridge loads.
//! The archive stays inside paths accepted by the already-installed bootstrap.
use jibbers_distribution::{catalog::{self, hash, Catalog}, install::{atomic_write, safe_path}, trust, STATE_DIR};
use std::{collections::{BTreeMap, HashSet}, io::{Cursor, Read}, path::{Path, PathBuf}};

const PACKAGE: &str = "JibbersMods/packages/jibbers_dotnet";
const ARCHIVE: &str = "JibbersMods/packages/jibbers_dotnet/runtime.zip";
const BRIDGE: &str = "JibbersMods/packages/jibbers_dotnet/jibbers_dotnet.dll";
const MAX_EXPANDED: u64 = 512 * 1024 * 1024;

fn read_limited(path: &Path, maximum: u64) -> Result<Vec<u8>, String> {
    let file = std::fs::File::open(path).map_err(|error| error.to_string())?;
    if file.metadata().map_err(|error| error.to_string())?.len() > maximum { return Err("runtime file exceeds size limit".into()); }
    let mut bytes = Vec::new();
    file.take(maximum + 1).read_to_end(&mut bytes).map_err(|error| error.to_string())?;
    if bytes.len() as u64 > maximum { return Err("runtime file exceeds size limit".into()); }
    Ok(bytes)
}

pub fn prepare(root: &Path) -> Result<Option<PathBuf>, String> {
    let archive_path = safe_path(root, ARCHIVE)?;
    if !archive_path.exists() { return Ok(None); }
    let archive = read_limited(&archive_path, catalog::MAX_FILE)?;
    let bridge = read_limited(&safe_path(root, BRIDGE)?, catalog::MAX_FILE)?;
    let identity = hash(&archive);
    // Keep native-host paths short on Windows. Full hashes still authenticate every file;
    // the directory uses a 128-bit prefix, never as a substitute for verification.
    let destination = format!("JibbersMods/system/clr/{}", &identity[..32]);
    // A newer catalog may be cached even when its downloads failed. In that case the
    // previous installed runtime must still work, authenticated by its signed receipt.
    let receipt = authenticated_receipt(root, &format!("{STATE_DIR}/catalog.json"), &archive, &bridge)
        .or_else(|_| authenticated_receipt(root, &format!("{destination}/catalog.json"), &archive, &bridge))?;
    unpack(root, &destination, &archive)?;
    atomic_write(&safe_path(root, &format!("{destination}/catalog.json"))?, &receipt.0)?;
    atomic_write(&safe_path(root, &format!("{destination}/catalog.json.sig"))?, &receipt.1)?;
    atomic_write(&safe_path(root, &format!("{PACKAGE}/active.txt"))?, identity.as_bytes())?;
    Ok(Some(safe_path(root, BRIDGE)?))
}

fn authenticated_receipt(root: &Path, relative: &str, archive: &[u8], bridge: &[u8]) -> Result<(Vec<u8>, Vec<u8>), String> {
    let bytes = read_limited(&safe_path(root, relative)?, catalog::MAX_CATALOG as u64)?;
    let signature = read_limited(&safe_path(root, &format!("{relative}.sig"))?, 1024)?;
    let catalog = catalog::verify(&bytes, &String::from_utf8_lossy(&signature), trust::KEYS)?;
    verify_runtime_files(&catalog, archive, bridge)?;
    Ok((bytes, signature))
}

fn verify_runtime_files(catalog: &Catalog, archive: &[u8], bridge: &[u8]) -> Result<(), String> {
    let package = catalog.packages.iter().find(|package| package.id == "jibbers_dotnet").ok_or("C# runtime is absent from the signed catalog")?;
    for (relative, bytes) in [(ARCHIVE, archive), (BRIDGE, bridge)] {
        let expected = package.files.iter().find(|file| file.path == relative).ok_or("C# runtime file is absent from the signed catalog")?;
        if bytes.len() as u64 != expected.size || hash(bytes) != expected.sha256 { return Err(format!("C# runtime verification failed: {relative}")); }
    }
    Ok(())
}

fn unpack(root: &Path, destination: &str, bytes: &[u8]) -> Result<(), String> {
    let mut archive = zip::ZipArchive::new(Cursor::new(bytes)).map_err(|error| error.to_string())?;
    if archive.len() > 2048 { return Err("C# runtime archive has too many entries".into()); }
    let manifest: BTreeMap<String, String> = {
        let file = archive.by_name("manifest.json").map_err(|error| error.to_string())?;
        if file.size() > 512 * 1024 { return Err("C# runtime manifest is too large".into()); }
        serde_json::from_reader(file).map_err(|error| error.to_string())?
    };
    if manifest.is_empty() { return Err("C# runtime archive is empty".into()); }
    let mut seen = HashSet::new();
    let mut total = 0u64;
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|error| error.to_string())?;
        let name = entry.name().replace('\\', "/");
        if name == "manifest.json" { continue; }
        if entry.is_dir() { continue; }
        if !name.starts_with("dotnet/") || !seen.insert(name.to_ascii_lowercase())
            || entry.unix_mode().is_some_and(|mode| mode & 0o170000 == 0o120000) {
            return Err(format!("unsafe C# runtime entry: {name}"));
        }
        let expected = manifest.get(&name).ok_or("C# runtime entry is not in its manifest")?;
        let relative = name.strip_prefix("dotnet/").ok_or("invalid runtime root")?;
        let target = safe_path(root, &format!("{destination}/{relative}"))?;
        total = total.checked_add(entry.size()).ok_or("C# runtime archive size overflow")?;
        if entry.size() > 128 * 1024 * 1024 || total > MAX_EXPANDED { return Err("C# runtime archive is too large".into()); }
        // The signed manifest authenticates the cache on every launch; repair only changed files.
        if read_limited(&target, entry.size()).is_ok_and(|existing| hash(&existing) == *expected) { continue; }
        let mut contents = Vec::new();
        (&mut entry).take(128 * 1024 * 1024 + 1).read_to_end(&mut contents).map_err(|error| error.to_string())?;
        if contents.len() as u64 != entry.size() || hash(&contents) != *expected { return Err(format!("C# runtime entry failed verification: {name}")); }
        atomic_write(&target, &contents)?;
    }
    if seen.len() != manifest.len() { return Err("C# runtime archive is incomplete".into()); }
    for required in ["dotnet/Jibbers.Sdk.dll", "dotnet/Jibbers.Sdk.runtimeconfig.json"] {
        if !manifest.contains_key(required) { return Err(format!("C# runtime archive is missing {required}")); }
    }
    Ok(())
}

/// PE's CLR data-directory entry distinguishes managed assemblies from native plugin DLLs.
pub fn is_managed(bytes: &[u8]) -> bool {
    let read_u32 = |offset: usize| bytes.get(offset..offset + 4).map(|value| u32::from_le_bytes(value.try_into().unwrap()));
    if bytes.get(..2) != Some(b"MZ") { return false; }
    let Some(header) = read_u32(0x3c).map(|offset| offset as usize) else { return false };
    if header > bytes.len().saturating_sub(256) || bytes.get(header..header + 4) != Some(b"PE\0\0") { return false; }
    let optional = header + 24;
    let directory = match bytes.get(optional..optional + 2) {
        Some([0x0b, 0x01]) => optional + 96,
        Some([0x0b, 0x02]) => optional + 112,
        _ => return false,
    };
    read_u32(directory + 14 * 8).is_some_and(|address| address != 0)
        && read_u32(directory + 14 * 8 + 4).is_some_and(|size| size != 0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    fn archive(files: &[(&str, &[u8])]) -> Vec<u8> {
        let mut output = zip::ZipWriter::new(Cursor::new(Vec::new()));
        let options = zip::write::SimpleFileOptions::default();
        let manifest: BTreeMap<_, _> = files.iter().map(|(name, bytes)| (*name, hash(bytes))).collect();
        output.start_file("manifest.json", options).unwrap();
        output.write_all(&serde_json::to_vec(&manifest).unwrap()).unwrap();
        for (name, bytes) in files { output.start_file(*name, options).unwrap(); output.write_all(bytes).unwrap(); }
        output.finish().unwrap().into_inner()
    }
    fn sandbox() -> PathBuf {
        let root = std::env::temp_dir().join(format!("jibbers-managed-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        std::fs::create_dir_all(&root).unwrap(); root
    }
    #[test]
    fn expands_and_repairs_cache_without_touching_settings() {
        let root = sandbox();
        std::fs::write(root.join("player.settings.json"), b"keep").unwrap();
        let bytes = archive(&[("dotnet/Jibbers.Sdk.dll", b"sdk"), ("dotnet/Jibbers.Sdk.runtimeconfig.json", b"{}")]);
        unpack(&root, "cache", &bytes).unwrap();
        std::fs::write(root.join("cache/Jibbers.Sdk.dll"), b"changed").unwrap();
        unpack(&root, "cache", &bytes).unwrap();
        assert_eq!(std::fs::read(root.join("cache/Jibbers.Sdk.dll")).unwrap(), b"sdk");
        assert_eq!(std::fs::read(root.join("player.settings.json")).unwrap(), b"keep");
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn rejects_escape_and_incomplete_runtime_archives() {
        let root = sandbox();
        for name in ["dotnet/../../escape.dll", "../escape.dll", "dotnet/C:/escape.dll", "other/file.dll"] {
            assert!(unpack(&root, "cache", &archive(&[(name, b"bad")])).is_err());
        }
        assert!(unpack(&root, "cache", &archive(&[("dotnet/Jibbers.Sdk.dll", b"sdk")])).is_err());
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn detects_clr_header_without_accepting_native_or_truncated_files() {
        let mut bytes = vec![0; 512]; bytes[..2].copy_from_slice(b"MZ"); bytes[0x3c] = 64;
        bytes[64..68].copy_from_slice(b"PE\0\0"); bytes[88..90].copy_from_slice(&[0x0b, 0x02]);
        assert!(!is_managed(&bytes)); bytes[88 + 112 + 112] = 1; bytes[88 + 112 + 116] = 72;
        assert!(is_managed(&bytes)); assert!(!is_managed(&bytes[..100])); assert!(!is_managed(b"not a PE"));
    }
}
