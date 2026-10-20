//! Maintainer-only signed channel builder. Private keys are read from a file, never printed.
use ed25519_dalek::{Signer, SigningKey};
use jibbers_distribution::{catalog::{self, Catalog, File, Package}, install, trust};
use serde::Deserialize;
use std::{collections::HashMap, path::{Path, PathBuf}};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Spec { sequence: u64, base_url: String, packages: Vec<Input> }
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Input { id: String, name: String, description: String, version: String, #[serde(default)] dependencies: Vec<String>, files: Vec<String> }

fn run() -> Result<(), String> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() == 2 && args[0] == "verify" { return verify(Path::new(&args[1])); }
    if args.len() % 2 != 0 { return Err("usage: jibbers-publish --spec spec.json --root staging --out channel --key signing.key; or verify channel".into()); }
    let options: HashMap<_, _> = args.chunks(2).map(|p| (p[0].as_str(), p[1].as_str())).collect();
    let need = |name| options.get(name).copied().ok_or_else(|| format!("missing {name}"));
    let spec: Spec = install::read_json(Path::new(need("--spec")?))?;
    let root = std::fs::canonicalize(need("--root")?).map_err(|e| e.to_string())?;
    let out = PathBuf::from(need("--out")?);
    if out.exists() && std::fs::read_dir(&out).map_err(|e| e.to_string())?.next().is_some() { return Err("output directory must be empty".into()); }
    let key_text = std::fs::read_to_string(need("--key")?).map_err(|e| e.to_string())?;
    let key_text = key_text.trim();
    if key_text.len() != 64 || !key_text.is_ascii() { return Err("invalid signing key file".into()); }
    let mut key = [0u8; 32];
    for (i, byte) in key.iter_mut().enumerate() { *byte = u8::from_str_radix(&key_text[2*i..2*i+2],16).map_err(|_| "invalid signing key file")?; }
    let signing = SigningKey::from_bytes(&key);
    if !trust::KEYS.contains(&signing.verifying_key().to_bytes()) { return Err("signing key is not trusted by installed bootstraps".into()); }
    let mut packages = vec![]; let mut assets = vec![];
    for p in spec.packages {
        let mut files = vec![];
        for rel in p.files {
            if !catalog::valid_path(&p.id, &rel) { return Err(format!("invalid package path {rel}")); }
            let source = install::safe_path(&root, &rel)?;
            let data = std::fs::read(source).map_err(|e| e.to_string())?;
            let sha256 = catalog::hash(&data);
            let asset = format!("{}-{}-{}.bin", p.id, p.version, sha256);
            let url = format!("{}/{}", spec.base_url.trim_end_matches('/'), asset);
            files.push(File { path: rel, url, size: data.len() as u64, sha256 });
            assets.push((asset, data));
        }
        packages.push(Package { id: p.id, name: p.name, description: p.description, version: p.version, dependencies: p.dependencies, files });
    }
    let cat = Catalog { schema: 1, sequence: spec.sequence, packages }; cat.validate()?;
    if let Some(previous) = options.get("--previous") {
        let previous_path = Path::new(previous);
        let previous_bytes = std::fs::read(previous_path).map_err(|e| e.to_string())?;
        let sig = std::fs::read_to_string(previous_path.with_extension("json.sig")).map_err(|e| e.to_string())?;
        let previous_cat = catalog::verify(&previous_bytes, &sig, trust::KEYS)?;
        catalog::advance(&previous_cat, &cat)?;
    }
    let bytes = serde_json::to_vec_pretty(&cat).map_err(|e| e.to_string())?;
    let sig: String = signing.sign(&bytes).to_bytes().iter().map(|b| format!("{b:02x}")).collect();
    std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
    for (name, bytes) in assets { std::fs::write(out.join(name), bytes).map_err(|e| e.to_string())?; }
    std::fs::write(out.join("catalog.json"), bytes).map_err(|e| e.to_string())?;
    std::fs::write(out.join("catalog.json.sig"), sig).map_err(|e| e.to_string())?;
    verify(&out)
}
fn verify(out: &Path) -> Result<(), String> {
    let cat = catalog::verify(&std::fs::read(out.join("catalog.json")).map_err(|e| e.to_string())?, &std::fs::read_to_string(out.join("catalog.json.sig")).map_err(|e| e.to_string())?, trust::KEYS)?;
    for p in &cat.packages { for f in &p.files {
        let filename = f.url.rsplit('/').next().ok_or("asset URL has no filename")?;
        if filename.contains(['\\', ':']) || matches!(filename, "." | "..") { return Err("unsafe asset filename".into()); }
        let bytes = std::fs::read(out.join(filename)).map_err(|e| e.to_string())?;
        if bytes.len() as u64 != f.size || catalog::hash(&bytes) != f.sha256 { return Err(format!("bad asset {}", f.path)); }
    }}
    println!("Verified signed catalog sequence {} ({} packages)", cat.sequence, cat.packages.len()); Ok(())
}
fn main() { if let Err(error) = run() { eprintln!("{error}"); std::process::exit(1); } }
