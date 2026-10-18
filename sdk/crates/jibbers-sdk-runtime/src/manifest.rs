//! The signed update manifest: format, path rules and signature check.
//!
//! A release is a flat folder of files:
//!
//! - `manifest.json`: this struct as JSON.
//! - `manifest.json.sig`: the Ed25519 signature of the exact bytes of `manifest.json`, hex encoded.
//! - one file per manifest entry, named by its `asset`.
//!
//! Nothing downloaded is trusted until the signature verifies against a public key compiled into
//! the runtime; every file is then checked against the size and SHA-256 listed in the manifest.
//! HTTPS protects the transport only, so a compromised host cannot push code to players.

use ed25519_dalek::{Signature, VerifyingKey};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

pub const SCHEMA: u32 = 1;
pub const MAX_MANIFEST_BYTES: usize = 256 * 1024;
pub const MAX_FILE_BYTES: u64 = 32 * 1024 * 1024;
pub const MAX_TOTAL_BYTES: u64 = 96 * 1024 * 1024;
pub const MAX_FILES: usize = 500;
/// The runtime DLL's place under `JibbersMods\` (everything else must be under `sdk/`).
pub const RUNTIME_PATH: &str = "jibbers_sdk.dll";

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    pub schema: u32,
    /// `major.minor.patch`. Installed only if greater than what is installed.
    pub sdk_version: String,
    /// Loader ABI the runtime in this release needs; releases that need a newer loader are skipped.
    pub min_loader_abi: u32,
    pub published: String,
    #[serde(default)]
    pub notes: String,
    /// Where later checks should look (https). Lets a release move to a new host.
    #[serde(default)]
    pub base_url: Option<String>,
    pub files: Vec<FileEntry>,
}

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct FileEntry {
    /// Install location relative to `<game>\JibbersMods\`, with `/` separators.
    pub path: String,
    /// File name of the download next to the manifest (flat: GitHub release assets have no folders).
    pub asset: String,
    pub size: u64,
    /// Lowercase hex SHA-256.
    pub sha256: String,
}

/// `1.2.3` as a comparable tuple.
pub fn parse_version(v: &str) -> Option<(u64, u64, u64)> {
    let mut it = v.trim().split('.');
    let a = it.next()?.parse().ok()?;
    let b = it.next()?.parse().ok()?;
    let c = it.next()?.parse().ok()?;
    it.next().is_none().then_some((a, b, c))
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect()
}

fn safe_component(c: &str) -> bool {
    !c.is_empty()
        && c != "."
        && c != ".."
        && !c.ends_with('.')
        && !c.ends_with(' ')
        && c.chars().all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '.' | '_' | '-' | ' '))
}

/// Install paths: `jibbers_sdk.dll`, or a file under `sdk/`. Plain names only: no `..`, drive
/// letters, backslashes or other characters Windows treats specially.
pub fn validate_install_path(path: &str) -> Result<(), String> {
    if path != RUNTIME_PATH && !(path.starts_with("sdk/") && path.len() > 4) {
        return Err(format!("{path:?} is outside the SDK's install area"));
    }
    if path.split('/').all(safe_component) {
        Ok(())
    } else {
        Err(format!("{path:?} is not a safe relative path"))
    }
}

pub fn validate_asset(asset: &str) -> Result<(), String> {
    if safe_component(asset) && !asset.contains(' ') {
        Ok(())
    } else {
        Err(format!("{asset:?} is not a safe asset name"))
    }
}

impl Manifest {
    /// Structural checks that every consumer applies after the signature has verified.
    pub fn validate(&self) -> Result<(), String> {
        if self.schema != SCHEMA {
            return Err(format!("manifest schema {} is not understood (expected {SCHEMA})", self.schema));
        }
        parse_version(&self.sdk_version).ok_or_else(|| format!("bad sdkVersion {:?}", self.sdk_version))?;
        if self.files.is_empty() || self.files.len() > MAX_FILES {
            return Err(format!("manifest lists {} files", self.files.len()));
        }
        if let Some(b) = &self.base_url {
            if !b.starts_with("https://") {
                return Err("baseUrl must be https".into());
            }
        }
        let mut total = 0u64;
        for (i, f) in self.files.iter().enumerate() {
            validate_install_path(&f.path)?;
            validate_asset(&f.asset)?;
            if f.size > MAX_FILE_BYTES {
                return Err(format!("{} is too large", f.path));
            }
            total += f.size;
            if f.sha256.len() != 64 || !f.sha256.chars().all(|c| matches!(c, '0'..='9' | 'a'..='f')) {
                return Err(format!("{}: sha256 must be 64 lowercase hex digits", f.path));
            }
            if self.files[..i].iter().any(|o| o.path.eq_ignore_ascii_case(&f.path)) {
                return Err(format!("{} is listed twice", f.path));
            }
        }
        if total > MAX_TOTAL_BYTES {
            return Err("release is too large".into());
        }
        Ok(())
    }
}

fn unhex(s: &str) -> Option<Vec<u8>> {
    let s = s.trim();
    if s.len() % 2 != 0 || !s.is_ascii() {
        return None;
    }
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).ok()).collect()
}

/// Verifies `sig_hex` (128 hex digits) over `manifest` against any of `keys`.
pub fn verify_signature(keys: &[[u8; 32]], manifest: &[u8], sig_hex: &str) -> Result<(), String> {
    let sig: [u8; 64] = unhex(sig_hex).and_then(|b| b.try_into().ok()).ok_or("signature is not 128 hex digits")?;
    let sig = Signature::from_bytes(&sig);
    for k in keys {
        if let Ok(key) = VerifyingKey::from_bytes(k) {
            if key.verify_strict(manifest, &sig).is_ok() {
                return Ok(());
            }
        }
    }
    Err("signature does not match any trusted key".into())
}

/// Verifies the signature, then parses and validates the manifest. Never parses unsigned bytes.
pub fn parse_signed(keys: &[[u8; 32]], manifest: &[u8], sig_hex: &str) -> Result<Manifest, String> {
    if manifest.len() > MAX_MANIFEST_BYTES {
        return Err("manifest is too large".into());
    }
    verify_signature(keys, manifest, sig_hex)?;
    let m: Manifest = serde_json::from_slice(manifest).map_err(|e| format!("manifest: {e}"))?;
    m.validate()?;
    Ok(m)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ed25519_dalek::{Signer, SigningKey};

    pub fn key(seed: u8) -> SigningKey {
        SigningKey::from_bytes(&[seed; 32])
    }

    pub fn sign(k: &SigningKey, bytes: &[u8]) -> String {
        k.sign(bytes).to_bytes().iter().map(|b| format!("{b:02x}")).collect()
    }

    fn sample() -> Manifest {
        Manifest {
            schema: 1,
            sdk_version: "0.2.0".into(),
            min_loader_abi: 4,
            published: "2026-10-18T00:00:00Z".into(),
            notes: String::new(),
            base_url: None,
            files: vec![
                FileEntry { path: "jibbers_sdk.dll".into(), asset: "jibbers_sdk.dll".into(), size: 3, sha256: sha256_hex(b"abc") },
                FileEntry { path: "sdk/docs/SDK.md".into(), asset: "sdk__docs__SDK.md".into(), size: 1, sha256: sha256_hex(b"x") },
            ],
        }
    }

    #[test]
    fn versions_compare_numerically() {
        assert!(parse_version("0.10.0") > parse_version("0.9.9"));
        assert!(parse_version("1.0.0") > parse_version("0.99.99"));
        assert_eq!(parse_version("1.2"), None);
        assert_eq!(parse_version("1.2.3.4"), None);
        assert_eq!(parse_version("a.b.c"), None);
    }

    #[test]
    fn sha256_is_correct() {
        assert_eq!(sha256_hex(b"abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    #[test]
    fn install_paths_are_confined() {
        for ok in ["jibbers_sdk.dll", "sdk/docs/SDK.md", "sdk/crates/jibbers-mod-sdk/src/lib.rs", "sdk/a b.txt"] {
            assert!(validate_install_path(ok).is_ok(), "{ok}");
        }
        for bad in [
            "../evil.dll", "sdk/../evil.dll", "sdk/..", "/abs.dll", "C:/x.dll", "sdk\\x.dll", "sdk/", "sdk", "version.dll",
            "JibbersMods/x.dll", "other.dll", "sdk//x", "sdk/x:stream", "sdk/con.", "sdk/x ", "sdk/ünï.txt", "jibbers_sdk.dll/x",
        ] {
            assert!(validate_install_path(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn manifests_are_validated() {
        assert!(sample().validate().is_ok());
        let mut m = sample();
        m.schema = 2;
        assert!(m.validate().is_err());
        let mut m = sample();
        m.sdk_version = "1.0".into();
        assert!(m.validate().is_err());
        let mut m = sample();
        m.files[1].sha256 = "ABC".into();
        assert!(m.validate().is_err());
        let mut m = sample();
        m.files[1].path = "sdk/JIBBERS_SDK.DLL".into();
        m.files.push(FileEntry { path: "sdk/jibbers_sdk.dll".into(), ..m.files[1].clone() });
        assert!(m.validate().is_err(), "case-insensitive duplicate");
        let mut m = sample();
        m.base_url = Some("http://insecure".into());
        assert!(m.validate().is_err());
        let mut m = sample();
        m.files[0].asset = "a/b".into();
        assert!(m.validate().is_err());
        let mut m = sample();
        m.files.clear();
        assert!(m.validate().is_err());
    }

    #[test]
    fn only_signed_manifests_are_accepted() {
        let (good, other) = (key(1), key(2));
        let bytes = serde_json::to_vec_pretty(&sample()).unwrap();
        let keys = [good.verifying_key().to_bytes()];
        assert_eq!(parse_signed(&keys, &bytes, &sign(&good, &bytes)).unwrap(), sample());
        assert!(parse_signed(&keys, &bytes, &sign(&other, &bytes)).is_err(), "wrong key");
        let mut tampered = bytes.clone();
        tampered[bytes.len() / 2] ^= 1;
        assert!(parse_signed(&keys, &tampered, &sign(&good, &bytes)).is_err(), "tampered bytes");
        assert!(parse_signed(&keys, &bytes, "").is_err());
        assert!(parse_signed(&keys, &bytes, "zz").is_err());
        assert!(parse_signed(&[], &bytes, &sign(&good, &bytes)).is_err(), "no trusted keys");
        // a rotated key list still accepts the old signer
        let rotated = [other.verifying_key().to_bytes(), good.verifying_key().to_bytes()];
        assert!(parse_signed(&rotated, &bytes, &sign(&good, &bytes)).is_ok());
        // signed but structurally invalid is still rejected
        let mut bad = sample();
        bad.files[0].path = "../x".into();
        let bad_bytes = serde_json::to_vec(&bad).unwrap();
        assert!(parse_signed(&keys, &bad_bytes, &sign(&good, &bad_bytes)).is_err());
    }
}
