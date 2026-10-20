use ed25519_dalek::{Signature, VerifyingKey};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::HashSet;

pub const MAX_CATALOG: usize = 512 * 1024;
pub const MAX_FILE: u64 = 128 * 1024 * 1024;
pub const MAX_DOWNLOAD: u64 = 512 * 1024 * 1024;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Catalog {
    pub schema: u32,
    pub sequence: u64,
    pub packages: Vec<Package>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Package {
    pub id: String,
    pub name: String,
    pub description: String,
    pub version: String,
    #[serde(default)]
    pub dependencies: Vec<String>,
    pub files: Vec<File>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct File {
    pub path: String,
    pub url: String,
    pub size: u64,
    pub sha256: String,
}

pub fn version(s: &str) -> Result<(u64, u64, u64), String> {
    let parts: Vec<_> = s.split('.').collect();
    if parts.len() != 3 || parts.iter().any(|p| p.is_empty() || !p.bytes().all(|b| b.is_ascii_digit())) {
        return Err(format!("invalid version {s}"));
    }
    Ok((parts[0].parse().map_err(|_| "version overflow")?, parts[1].parse().map_err(|_| "version overflow")?, parts[2].parse().map_err(|_| "version overflow")?))
}

pub fn hash(bytes: &[u8]) -> String { Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect() }

fn component(s: &str) -> bool {
    let stem = s.split('.').next().unwrap_or("").to_ascii_lowercase();
    !s.is_empty() && !s.ends_with(['.', ' ']) && !matches!(s, "." | "..")
        && s.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
        && !matches!(stem.as_str(), "con" | "prn" | "aux" | "nul")
        && !(stem.len() == 4 && (stem.starts_with("com") || stem.starts_with("lpt")) && matches!(stem.as_bytes()[3], b'1'..=b'9'))
}

pub fn valid_path(id: &str, path: &str) -> bool {
    if !path.split('/').all(component) { return false; }
    if id == "loader" { return path == crate::LOADER_PATH; }
    // The catalog is curated, but even a signed package cannot replace game files, settings,
    // the bootstrap, distribution state, or another package's native plugin.
    path == format!("JibbersMods/{id}.dll")
        || path == format!("JibbersMods/{id}.bundle")
        || path == format!("{id}.bundle") // legacy Atmosphere assets
        || (path.starts_with(&format!("JibbersMods/packages/{id}/")) && !path.ends_with(".json"))
        || path == format!("JibbersMods/cs/{id}.dll")
}

impl Catalog {
    pub fn validate(&self) -> Result<(), String> {
        if self.schema != 1 || self.sequence == 0 || self.packages.len() > 100 { return Err("unsupported or invalid catalog".into()); }
        let mut ids = HashSet::new();
        let mut paths = HashSet::new();
        let mut total = 0;
        for p in &self.packages {
            if p.id.is_empty() || p.id.len() > 80 || !p.id.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'_') || !ids.insert(&p.id) {
                return Err("invalid or duplicate package id".into());
            }
            version(&p.version)?;
            if p.name.len() > 100 || p.description.len() > 2000 || p.files.is_empty() || p.files.len() > 100 { return Err("invalid package metadata".into()); }
            for f in &p.files {
                crate::http::split_url(&f.url)?;
                if !valid_path(&p.id, &f.path) || !paths.insert(f.path.to_ascii_lowercase()) { return Err(format!("unsafe or overlapping install path {}", f.path)); }
                if f.size == 0 || f.size > MAX_FILE || f.sha256.len() != 64 || !f.sha256.bytes().all(|b| b.is_ascii_digit() || matches!(b, b'a'..=b'f')) { return Err("invalid file metadata".into()); }
                total += f.size;
            }
        }
        if total > MAX_DOWNLOAD || !ids.contains(&"loader".to_string()) { return Err("catalog too large or missing loader".into()); }
        for p in &self.packages {
            for d in &p.dependencies {
                if !ids.contains(d) || d == &p.id { return Err(format!("invalid dependency {d}")); }
            }
            self.resolve(&[p.id.clone()])?;
        }
        Ok(())
    }

    pub fn resolve<'a>(&'a self, wanted: &[String]) -> Result<Vec<&'a Package>, String> {
        fn visit<'a>(cat: &'a Catalog, id: &str, stack: &mut HashSet<String>, done: &mut HashSet<String>, out: &mut Vec<&'a Package>) -> Result<(), String> {
            if done.contains(id) { return Ok(()); }
            if !stack.insert(id.to_string()) { return Err("dependency cycle".into()); }
            let p = cat.packages.iter().find(|p| p.id == id).ok_or_else(|| format!("package {id} is unavailable"))?;
            for d in &p.dependencies { visit(cat, d, stack, done, out)?; }
            stack.remove(id); done.insert(id.to_string()); out.push(p); Ok(())
        }
        let mut out = vec![];
        for id in wanted { visit(self, id, &mut HashSet::new(), &mut HashSet::new(), &mut out)?; }
        // De-duplicate shared dependencies across requested packages.
        let mut seen = HashSet::new(); out.retain(|p| seen.insert(p.id.clone())); Ok(out)
    }
}

pub fn verify(bytes: &[u8], signature: &str, keys: &[[u8; 32]]) -> Result<Catalog, String> {
    if bytes.len() > MAX_CATALOG { return Err("catalog too large".into()); }
    verify_signature(bytes, signature, keys)?;
    let catalog: Catalog = serde_json::from_slice(bytes).map_err(|e| e.to_string())?;
    catalog.validate()?; Ok(catalog)
}

pub fn verify_signature(bytes: &[u8], signature: &str, keys: &[[u8; 32]]) -> Result<(), String> {
    let sig = signature.trim();
    if sig.len() != 128 || !sig.is_ascii() { return Err("invalid catalog signature".into()); }
    let mut raw = [0u8; 64];
    for (i, byte) in raw.iter_mut().enumerate() { *byte = u8::from_str_radix(&sig[i * 2..i * 2 + 2], 16).map_err(|_| "invalid catalog signature")?; }
    if !keys.iter().any(|key| VerifyingKey::from_bytes(key).is_ok_and(|k| k.verify_strict(bytes, &Signature::from_bytes(&raw)).is_ok())) { return Err("catalog signature is not trusted".into()); }
    Ok(())
}

pub fn advance(previous: &Catalog, next: &Catalog) -> Result<(), String> {
    if next.sequence <= previous.sequence { return Err("catalog sequence must increase".into()); }
    for old in &previous.packages {
        if let Some(new) = next.packages.iter().find(|p| p.id == old.id) {
            if version(&new.version)? < version(&old.version)? { return Err(format!("{} would downgrade", old.id)); }
            if old.version == new.version && (old.files.len() != new.files.len() || old.files.iter().any(|f| !new.files.iter().any(|n| n.path == f.path && n.sha256 == f.sha256))) {
                return Err(format!("{} changed without a version bump", old.id));
            }
        }
    }
    Ok(())
}
