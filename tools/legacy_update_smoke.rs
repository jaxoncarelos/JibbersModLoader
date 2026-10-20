//! Compiled against the unmodified SDK 0.1.1 updater sources by verify_loader_bridge.ps1.
#![allow(dead_code)]
mod manifest;
mod trust;
mod update;
struct Local(std::path::PathBuf);
impl update::Fetch for Local {
    fn get(&self, url: &str, max: usize) -> Result<Vec<u8>, String> {
        let asset = url.rsplit('/').next().ok_or("missing asset")?;
        let bytes = std::fs::read(self.0.join(asset)).map_err(|e| e.to_string())?;
        if bytes.len() > max { return Err("asset is too large".into()); } Ok(bytes)
    }
}
fn main() {
    let args: Vec<_> = std::env::args().skip(1).collect();
    let root = std::path::PathBuf::from(&args[1]);
    let cfg = update::Config { mods_dir: root.join("JibbersMods"), base_urls: vec!["https://fixture.invalid".into()], keys: trust::KEYS, loader_abi: 4 };
    std::fs::create_dir_all(cfg.mods_dir.join("sdk")).unwrap();
    let old = update::Installed { sdk_version: "0.1.1".into(), ..Default::default() };
    std::fs::write(update::installed_path(&cfg.mods_dir), serde_json::to_vec(&old).unwrap()).unwrap();
    let result = update::check_and_apply(&cfg, &Local(args[0].clone().into()));
    assert!(matches!(result, update::Outcome::Updated { ref to, .. } if to == "0.1.2"), "{result:?}");
    assert_eq!(std::fs::read(root.join("version.dll")).unwrap(), b"MZ previous loader fixture");
    assert!(cfg.mods_dir.join("sdk/loader/install.json").exists());
    println!("Original SDK 0.1.1 updater accepted and installed the signed loader bridge.");
}
