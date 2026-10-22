use crate::{catalog::{self, Catalog, File, Package}, install::{self, Fetch}};
use ed25519_dalek::{Signer, SigningKey};
use std::{collections::HashMap, path::PathBuf};

struct Sandbox(PathBuf);
impl Sandbox {
    fn new() -> Self {
        static ID: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
        let root = std::env::temp_dir().join(format!("jibbers-distribution-{}-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos(), ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)));
        std::fs::create_dir(&root).unwrap(); Self(root)
    }
    fn bytes(&self, path: &str) -> Vec<u8> { std::fs::read(self.0.join(path)).unwrap() }
    fn write(&self, path: &str, bytes: &[u8]) { install::atomic_write(&self.0.join(path), bytes).unwrap(); }
}
impl Drop for Sandbox { fn drop(&mut self) { assert!(self.0.starts_with(std::env::temp_dir())); let _ = std::fs::remove_dir_all(&self.0); } }
#[derive(Default)]
struct Net(HashMap<String, Vec<u8>>);
impl Fetch for Net {
    fn get(&self, url: &str, _: usize) -> Result<Vec<u8>, String> { self.0.get(url).cloned().ok_or_else(|| "offline".into()) }
}
fn package(net: &mut Net, id: &str, version: &str, bytes: &[u8]) -> Package {
    let path = if id == "loader" { crate::LOADER_PATH.to_string() } else { format!("JibbersMods/{id}.dll") };
    let url = format!("https://cdn.example/{id}-{version}.dll");
    net.0.insert(url.clone(), bytes.to_vec());
    Package { id: id.into(), name: id.into(), description: "test".into(), version: version.into(), dependencies: vec![], files: vec![File { path, url, size: bytes.len() as u64, sha256: catalog::hash(bytes) }] }
}
fn setup() -> (Catalog, Net) {
    let mut net = Net::default();
    let loader = package(&mut net, "loader", "1.0.0", b"MZ loader v1");
    let a = package(&mut net, "example", "1.0.0", b"MZ example v1");
    (Catalog { schema: 1, sequence: 1, packages: vec![loader, a] }, net)
}

#[test]
fn signature_tampering_wrong_keys_and_bad_json_are_rejected() {
    let (cat, _) = setup(); let key = SigningKey::from_bytes(&[42; 32]);
    let bytes = serde_json::to_vec(&cat).unwrap();
    let sig: String = key.sign(&bytes).to_bytes().iter().map(|b| format!("{b:02x}")).collect();
    let keys = [key.verifying_key().to_bytes()];
    assert!(catalog::verify(&bytes, &sig, &keys).is_ok());
    assert!(catalog::verify(&bytes, &sig, &[[0;32]]).is_err());
    assert!(catalog::verify(b"{}", &sig, &keys).is_err());
    assert!(catalog::verify(&bytes, "", &keys).is_err());
}

#[test]
fn catalog_cannot_write_game_bootstrap_settings_or_another_mod() {
    for path in ["version.dll", "GameAssembly.dll", "../evil.dll", "JibbersMods/../version.dll", "JibbersMods/other.dll", "JibbersMods/system/jibbers_loader.dll", "JibbersMods/example.json", "JibbersMods/packages/example/CON.txt", "JibbersMods/packages/example/LPT1.dll", "JibbersMods/packages/example/file.", "JibbersMods/packages/example/x:evil"] {
        assert!(!catalog::valid_path("example", path), "{path}");
    }
    assert!(catalog::valid_path("example", "JibbersMods/packages/example/assets/foo.bundle"));
    let (mut cat, _) = setup(); cat.packages[1].files[0].path = crate::LOADER_PATH.into();
    assert!(cat.validate().is_err());
}

#[test]
fn dependencies_resolve_and_cycles_fail() {
    let (mut cat, mut net) = setup(); cat.packages.push(package(&mut net, "dependency", "1.0.0", b"dependency"));
    cat.packages[1].dependencies.push("dependency".into());
    let ids: Vec<_> = cat.resolve(&["example".into()]).unwrap().iter().map(|p| p.id.as_str()).collect();
    assert_eq!(ids, ["dependency", "example"]);
    cat.packages[2].dependencies.push("example".into()); assert!(cat.validate().is_err());
}

#[test]
fn startup_installs_loader_and_selected_mod_then_updates_both() {
    let root = Sandbox::new(); let (mut cat, mut net) = setup();
    install::apply(&root.0, &cat, &["example".into()], true, &net).unwrap();
    assert_eq!(root.bytes(crate::LOADER_PATH), b"MZ loader v1");
    assert_eq!(root.bytes("JibbersMods/example.dll"), b"MZ example v1");
    cat.sequence = 2; cat.packages[0] = package(&mut net, "loader", "1.0.1", b"MZ loader v2");
    cat.packages[1] = package(&mut net, "example", "1.0.1", b"MZ example v2");
    let installed = install::apply(&root.0, &cat, &["example".into()], true, &net).unwrap();
    assert_eq!(installed.packages["loader"].version, "1.0.1");
    assert_eq!(root.bytes("JibbersMods/example.dll"), b"MZ example v2");
}

#[test]
fn native_subscription_migrates_to_managed_with_required_runtime_and_keeps_settings() {
    let root = Sandbox::new(); let mut net = Net::default();
    let loader = package(&mut net, "loader", "0.2.1", b"old loader");
    let native = package(&mut net, "jibbers_fpv", "0.1.0", b"old native FPV");
    let mut catalog = Catalog { schema: 1, sequence: 200, packages: vec![loader, native] };
    let wanted = vec!["jibbers_fpv".into()];
    install::apply(&root.0, &catalog, &wanted, true, &net).unwrap();
    root.write("JibbersMods/fpv.settings.json", b"saved settings");
    root.write("JibbersMods/local.dll", b"local mod");
    catalog.sequence = 1001;
    catalog.packages[0] = package(&mut net, "loader", "0.3.0", b"managed-aware loader");
    catalog.packages[1] = package(&mut net, "jibbers_fpv", "0.3.0", b"C# FPV assembly");
    let mut runtime = package(&mut net, "jibbers_dotnet", "0.3.0", b"bridge");
    runtime.files[0].path = "JibbersMods/packages/jibbers_dotnet/jibbers_dotnet.dll".into();
    let url = "https://cdn.example/runtime.zip".to_string();
    net.0.insert(url.clone(), b"runtime archive".to_vec());
    runtime.files.push(File { path: "JibbersMods/packages/jibbers_dotnet/runtime.zip".into(), url: url.clone(), size: 15, sha256: catalog::hash(b"runtime archive") });
    catalog.packages[0].dependencies.push(runtime.id.clone());
    catalog.packages[1].dependencies.push(runtime.id.clone());
    catalog.packages.push(runtime);
    // A failed shared-runtime download must leave both the native mod and loader in place.
    let bytes = net.0.remove(&url).unwrap();
    assert!(install::apply(&root.0, &catalog, &wanted, true, &net).is_err());
    assert_eq!(root.bytes("JibbersMods/jibbers_fpv.dll"), b"old native FPV");
    assert_eq!(root.bytes(crate::LOADER_PATH), b"old loader");
    net.0.insert(url, bytes);
    let installed = install::apply(&root.0, &catalog, &wanted, true, &net).unwrap();
    assert_eq!(root.bytes("JibbersMods/jibbers_fpv.dll"), b"C# FPV assembly");
    assert_eq!(installed.packages.len(), 3);
    assert_eq!(root.bytes("JibbersMods/fpv.settings.json"), b"saved settings");
    assert_eq!(root.bytes("JibbersMods/local.dll"), b"local mod");
}

#[test]
fn bad_download_never_changes_any_installed_file() {
    let root = Sandbox::new(); let (mut cat, mut net) = setup();
    install::apply(&root.0, &cat, &["example".into()], true, &net).unwrap();
    let record = root.bytes(&format!("{}/installed.json", crate::STATE_DIR));
    cat.sequence = 2; cat.packages[0] = package(&mut net, "loader", "1.0.1", b"MZ loader v2");
    cat.packages[1] = package(&mut net, "example", "1.0.1", b"MZ example v2");
    net.0.insert(cat.packages[1].files[0].url.clone(), b"malicious".to_vec());
    assert!(install::apply(&root.0, &cat, &["example".into()], true, &net).is_err());
    assert_eq!(root.bytes(crate::LOADER_PATH), b"MZ loader v1");
    assert_eq!(root.bytes(&format!("{}/installed.json", crate::STATE_DIR)), record);
}

#[test]
fn offline_launch_keeps_installed_mods_and_downgrades_are_refused() {
    let root = Sandbox::new(); let (mut cat, net) = setup();
    install::apply(&root.0, &cat, &["example".into()], true, &net).unwrap();
    assert!(install::apply(&root.0, &cat, &["example".into()], true, &Net::default()).is_ok(), "no downloads when installed hashes match");
    cat.sequence = 0; assert!(install::apply(&root.0, &cat, &[], true, &net).is_err());
    cat.sequence = 2; cat.packages[0].version = "0.9.0".into();
    assert!(install::apply(&root.0, &cat, &["example".into()], true, &net).is_err());
    assert_eq!(root.bytes(crate::LOADER_PATH), b"MZ loader v1");
}

#[test]
fn local_mods_and_user_settings_are_never_overwritten_or_removed() {
    let root = Sandbox::new(); let (cat, net) = setup();
    root.write("JibbersMods/example.dll", b"manual mod");
    assert!(install::apply(&root.0, &cat, &["example".into()], true, &net).is_err());
    assert_eq!(root.bytes("JibbersMods/example.dll"), b"manual mod");
    std::fs::remove_file(root.0.join("JibbersMods/example.dll")).unwrap();
    install::apply(&root.0, &cat, &["example".into()], true, &net).unwrap();
    root.write("example.json", b"player preferences");
    root.write("JibbersMods/other.dll", b"another mod");
    install::apply(&root.0, &cat, &[], true, &net).unwrap();
    assert!(!root.0.join("JibbersMods/example.dll").exists());
    assert_eq!(root.bytes("example.json"), b"player preferences");
    assert_eq!(root.bytes("JibbersMods/other.dll"), b"another mod");
}

#[test]
fn interrupted_transaction_restores_backup_before_next_load() {
    let root = Sandbox::new(); let path = "JibbersMods/example.dll";
    root.write(path, b"partially installed update");
    let backup = format!("{}/transactions/test.old", crate::STATE_DIR);
    root.write(&backup, b"previous version");
    root.write(&format!("{}/journal.json", crate::STATE_DIR), serde_json::to_string(&serde_json::json!({"changes":[{"path":path,"old":backup,"new":null}]})).unwrap().as_bytes());
    install::recover(&root.0).unwrap();
    assert_eq!(root.bytes(path), b"previous version");
    assert!(!root.0.join(crate::STATE_DIR).join("journal.json").exists());
}

#[test]
fn changed_payload_requires_a_new_package_version() {
    let root = Sandbox::new(); let (mut cat, mut net) = setup();
    install::apply(&root.0, &cat, &["example".into()], true, &net).unwrap();
    cat.sequence = 2; cat.packages[1] = package(&mut net, "example", "1.0.0", b"changed bytes");
    assert!(install::apply(&root.0, &cat, &["example".into()], true, &net).is_err());
}

#[test]
fn publishing_rejects_sequence_reuse_and_changed_bytes_at_same_version() {
    let (old, mut net) = setup(); let mut next = old.clone();
    assert!(catalog::advance(&old, &next).is_err());
    next.sequence += 1;
    assert!(catalog::advance(&old, &next).is_ok());
    next.packages[1] = package(&mut net, "example", "1.0.0", b"new payload");
    assert!(catalog::advance(&old, &next).is_err());
    next.packages[1].version = "1.0.1".into();
    assert!(catalog::advance(&old, &next).is_ok());
}

#[test]
fn disabled_auto_update_retains_versions_but_new_installs_still_work() {
    let root = Sandbox::new(); let (mut cat, mut net) = setup();
    install::apply(&root.0, &cat, &[], true, &net).unwrap();
    cat.sequence = 2; cat.packages[0] = package(&mut net, "loader", "1.0.1", b"new loader");
    let next = install::apply(&root.0, &cat, &["example".into()], false, &net).unwrap();
    assert_eq!(next.packages["loader"].version, "1.0.0");
    assert!(root.0.join("JibbersMods/example.dll").exists());
}

#[cfg(windows)]
#[test]
fn locked_dll_rolls_back_other_writes_and_record() {
    use std::os::windows::fs::OpenOptionsExt;
    let root = Sandbox::new(); let (mut cat, mut net) = setup();
    install::apply(&root.0, &cat, &["example".into()], true, &net).unwrap();
    let record = root.bytes(&format!("{}/installed.json", crate::STATE_DIR));
    cat.sequence = 2; cat.packages[0] = package(&mut net, "loader", "1.0.1", b"loader2");
    cat.packages[1] = package(&mut net, "example", "1.0.1", b"example2");
    // Allow reads and writes, but deny deletion/rename like a loaded Windows DLL.
    let locked = std::fs::OpenOptions::new().read(true).share_mode(3).open(root.0.join(crate::LOADER_PATH)).unwrap();
    assert!(install::apply(&root.0, &cat, &["example".into()], true, &net).is_err());
    drop(locked);
    install::recover(&root.0).unwrap();
    assert_eq!(root.bytes("JibbersMods/example.dll"), b"MZ example v1");
    assert_eq!(root.bytes(&format!("{}/installed.json", crate::STATE_DIR)), record);
}
