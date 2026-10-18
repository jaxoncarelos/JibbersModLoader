//! Maintainer tool for SDK releases. Not shipped to players.
//!
//! ```text
//! sdk-publish keygen [--out <file>]
//!     Creates the update signing key (default %USERPROFILE%\.jibbers-sdk\signing.key) and prints the
//!     public key to paste into crates/jibbers-sdk-runtime/src/trust.rs. Never overwrites a key.
//!
//! sdk-publish validate-build --game-dir <dir> --compat <compat.json> [--label <text>] [--note <text>]
//!     Fingerprints an installed game (reads GameAssembly.dll and UnityPlayer.dll; runs nothing) and
//!     records it in the compatibility list as a build the mods were validated on. Run it only after
//!     testing the mods on that build. The list is what the loader checks before hooking anything.
//!
//! sdk-publish release --version <x.y.z> --runtime <jibbers_sdk.dll> --bundle <dir> --compat <compat.json>
//!                     --out <dir> [--key <file>] [--notes <text>] [--base-url <https://...>] [--abi <n>]
//!     Builds a signed release folder: manifest.json, manifest.json.sig and one flat asset per file.
//!     The compatibility list ships inside it as sdk/compat.json (stamped with the release version),
//!     so every release re-publishes which game builds are validated. Upload every file in <out> to
//!     the release host (for GitHub: all files of one release).
//!
//! sdk-publish verify --dir <release dir>
//!     Checks a release folder against the trusted keys compiled into this build.
//! ```

use ed25519_dalek::{Signer, SigningKey};
use jibbers_mod_sdk::gamebuild::{self, CompatList, KnownBuild};
use jibbers_sdk::manifest::{self, FileEntry, Manifest};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

type R<T> = Result<T, String>;

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn unhex32(s: &str) -> R<[u8; 32]> {
    let s = s.trim();
    if s.len() != 64 || !s.is_ascii() {
        return Err("signing key must be 64 hex digits".into());
    }
    let mut out = [0u8; 32];
    for (i, b) in out.iter_mut().enumerate() {
        *b = u8::from_str_radix(&s[2 * i..2 * i + 2], 16).map_err(|_| "signing key must be hex")?;
    }
    Ok(out)
}

fn default_key_path() -> R<PathBuf> {
    let home = std::env::var_os("USERPROFILE").ok_or("USERPROFILE is not set; pass --out/--key")?;
    Ok(PathBuf::from(home).join(".jibbers-sdk").join("signing.key"))
}

fn options(args: &[String]) -> R<HashMap<String, String>> {
    let mut map = HashMap::new();
    let mut it = args.iter();
    while let Some(k) = it.next() {
        let name = k.strip_prefix("--").ok_or_else(|| format!("unexpected argument {k:?}"))?;
        map.insert(name.to_string(), it.next().ok_or_else(|| format!("--{name} needs a value"))?.clone());
    }
    Ok(map)
}

fn need<'a>(o: &'a HashMap<String, String>, k: &str) -> R<&'a String> {
    o.get(k).ok_or_else(|| format!("missing --{k}"))
}

fn keygen(o: &HashMap<String, String>) -> R<()> {
    let path = match o.get("out") {
        Some(p) => PathBuf::from(p),
        None => default_key_path()?,
    };
    if path.exists() {
        return Err(format!("{} already exists; refusing to overwrite a signing key", path.display()));
    }
    let mut seed = [0u8; 32];
    getrandom::fill(&mut seed).map_err(|e| format!("no system randomness: {e}"))?;
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    std::fs::write(&path, hex(&seed)).map_err(|e| format!("{}: {e}", path.display()))?;
    let public = SigningKey::from_bytes(&seed).verifying_key().to_bytes();
    println!("Signing key written to {} (keep it secret and back it up; losing it strands installed SDKs).", path.display());
    println!("Public key (hex): {}", hex(&public));
    println!("Paste into crates/jibbers-sdk-runtime/src/trust.rs:");
    println!("    [{}],", public.iter().map(|b| format!("0x{b:02x}")).collect::<Vec<_>>().join(", "));
    Ok(())
}

/// Every file under `dir`, as (path relative with `/`, absolute path), skipping build output.
fn walk(dir: &Path, rel: &str, out: &mut Vec<(String, PathBuf)>) -> R<()> {
    let mut entries: Vec<_> = std::fs::read_dir(dir).map_err(|e| format!("{}: {e}", dir.display()))?.flatten().collect();
    entries.sort_by_key(|e| e.file_name());
    for e in entries {
        let name = e.file_name().to_string_lossy().into_owned();
        if matches!(name.as_str(), "target" | ".git" | ".staging" | "node_modules") {
            continue;
        }
        let path = e.path();
        let rel = if rel.is_empty() { name.clone() } else { format!("{rel}/{name}") };
        if path.is_dir() {
            walk(&path, &rel, out)?;
        } else {
            out.push((rel, path));
        }
    }
    Ok(())
}

fn release(o: &HashMap<String, String>) -> R<()> {
    let version = need(o, "version")?;
    manifest::parse_version(version).ok_or("--version must be major.minor.patch")?;
    let runtime = PathBuf::from(need(o, "runtime")?);
    let bundle = PathBuf::from(need(o, "bundle")?);
    let compat_path = PathBuf::from(need(o, "compat")?);
    let out = PathBuf::from(need(o, "out")?);
    let key_path = match o.get("key") {
        Some(p) => PathBuf::from(p),
        None => default_key_path()?,
    };
    let signing = SigningKey::from_bytes(&unhex32(&std::fs::read_to_string(&key_path).map_err(|e| format!("{}: {e}", key_path.display()))?)?);
    if !jibbers_sdk::trust::KEYS.contains(&signing.verifying_key().to_bytes()) {
        return Err("this signing key is not in trust.rs: installed SDKs would reject the release. Rebuild the runtime with the matching public key.".into());
    }
    let published = utc_now();

    // The compatibility list ships with every release, stamped with this release's version so the
    // newest one always wins over older installed or built-in copies.
    let mut compat = CompatList::parse(&std::fs::read_to_string(&compat_path).map_err(|e| format!("{}: {e}", compat_path.display()))?)?;
    if compat.builds.is_empty() {
        return Err("the compatibility list has no validated builds: run validate-build first".into());
    }
    compat.sdk_version = version.clone();
    compat.generated = published.clone();

    let mut inputs: Vec<(String, Vec<u8>)> = vec![];
    let read = |p: &Path| std::fs::read(p).map_err(|e| format!("{}: {e}", p.display()));
    inputs.push((manifest::RUNTIME_PATH.to_string(), read(&runtime)?));
    let mut found = vec![];
    walk(&bundle, "", &mut found)?;
    for (rel, p) in found {
        if rel == "compat.json" {
            return Err("the bundle must not contain compat.json: it is generated from --compat".into());
        }
        inputs.push((format!("sdk/{rel}"), read(&p)?));
    }
    inputs.push(("sdk/compat.json".into(), serde_json::to_vec_pretty(&compat.to_json()).map_err(|e| e.to_string())?));

    if out.exists() && std::fs::read_dir(&out).map_err(|e| e.to_string())?.next().is_some() {
        return Err(format!("{} is not empty", out.display()));
    }
    std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
    let mut files = vec![];
    for (path, data) in &inputs {
        let entry = FileEntry { path: path.clone(), asset: path.replace('/', "__"), size: data.len() as u64, sha256: manifest::sha256_hex(data) };
        std::fs::write(out.join(&entry.asset), data).map_err(|e| e.to_string())?;
        files.push(entry);
    }
    let m = Manifest {
        schema: manifest::SCHEMA,
        sdk_version: version.clone(),
        min_loader_abi: o.get("abi").map(|a| a.parse().map_err(|_| "--abi must be a number")).transpose()?.unwrap_or(4),
        published,
        notes: o.get("notes").cloned().unwrap_or_default(),
        base_url: o.get("base-url").cloned(),
        files,
    };
    m.validate()?;
    let bytes = serde_json::to_vec_pretty(&m).map_err(|e| e.to_string())?;
    std::fs::write(out.join("manifest.json"), &bytes).map_err(|e| e.to_string())?;
    std::fs::write(out.join("manifest.json.sig"), hex(&signing.sign(&bytes).to_bytes())).map_err(|e| e.to_string())?;
    verify_dir(&out)?;
    println!("SDK {version}: {} files + manifest signed into {}", m.files.len(), out.display());
    println!("Validated game builds in this release:");
    for b in &compat.builds {
        println!("  {} ({})", b.label, b.fingerprint.describe());
    }
    Ok(())
}

fn validate_build(o: &HashMap<String, String>) -> R<()> {
    let dir = PathBuf::from(need(o, "game-dir")?);
    let path = PathBuf::from(need(o, "compat")?);
    let fp = gamebuild::fingerprint_dir(&dir)?;
    let mut list = match std::fs::read_to_string(&path) {
        Ok(text) => CompatList::parse(&text).map_err(|e| format!("{}: {e}", path.display()))?,
        Err(_) => CompatList { sdk_version: "0.0.0".into(), ..Default::default() },
    };
    let existing = list.find(&fp).is_some();
    let label = o.get("label").cloned().unwrap_or_else(|| format!("Jibbers Playtest, Unity {}", fp.unity));
    list.generated = utc_now();
    list.upsert(KnownBuild { label: label.clone(), fingerprint: fp.clone(), validated: list.generated[..10].to_string(), note: o.get("note").cloned().unwrap_or_default() });
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    std::fs::write(&path, serde_json::to_string_pretty(&list.to_json()).map_err(|e| e.to_string())? + "\n").map_err(|e| format!("{}: {e}", path.display()))?;
    println!("{} build: {label}\n  {}\n  list now holds {} build(s): {}", if existing { "Re-validated" } else { "Added" }, fp.describe(), list.builds.len(), path.display());
    Ok(())
}

fn verify_dir(dir: &Path) -> R<()> {
    let bytes = std::fs::read(dir.join("manifest.json")).map_err(|e| format!("manifest.json: {e}"))?;
    let sig = std::fs::read_to_string(dir.join("manifest.json.sig")).map_err(|e| format!("manifest.json.sig: {e}"))?;
    let m = manifest::parse_signed(jibbers_sdk::trust::KEYS, &bytes, &sig)?;
    for f in &m.files {
        let data = std::fs::read(dir.join(&f.asset)).map_err(|e| format!("{}: {e}", f.asset))?;
        if data.len() as u64 != f.size || manifest::sha256_hex(&data) != f.sha256 {
            return Err(format!("{} does not match the manifest", f.asset));
        }
    }
    println!("OK: SDK {} signed by a trusted key, {} files match", m.sdk_version, m.files.len());
    Ok(())
}

/// `2026-10-18T12:34:56Z` without a date library.
fn utc_now() -> String {
    let secs = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0) as i64;
    let (days, rem) = (secs.div_euclid(86_400), secs.rem_euclid(86_400));
    // civil-from-days (Howard Hinnant)
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z", rem / 3600, rem % 3600 / 60, rem % 60)
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("keygen") => options(&args[1..]).and_then(|o| keygen(&o)),
        Some("release") => options(&args[1..]).and_then(|o| release(&o)),
        Some("validate-build") => options(&args[1..]).and_then(|o| validate_build(&o)),
        Some("verify") => options(&args[1..]).and_then(|o| verify_dir(Path::new(need(&o, "dir")?))),
        _ => Err("usage: sdk-publish keygen | validate-build | release | verify (see the header of crates/jibbers-sdk-publish/src/main.rs)".into()),
    };
    if let Err(e) = result {
        eprintln!("error: {e}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn utc_formatting_is_right() {
        // 2026-10-18T00:00:00Z = 1792281600 seconds after the epoch
        assert_eq!(hex(&[0, 255, 16]), "00ff10");
        assert_eq!(unhex32(&"ab".repeat(32)).unwrap()[0], 0xab);
        assert!(unhex32("zz").is_err());
        assert!(utc_now().ends_with('Z') && utc_now().len() == 20);
    }
}
