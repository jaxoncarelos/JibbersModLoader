//! Runs the production signed-runtime preparation against a temporary release fixture.
//! No game is started and no native plugin is loaded.
#[path = "../src/managed_runtime.rs"]
#[allow(dead_code)]
mod managed_runtime;

fn main() {
    let root = std::path::PathBuf::from(std::env::args().nth(1).expect("fixture root"));
    if let Some(channel) = std::env::args().nth(2) {
        struct LocalAssets(std::path::PathBuf);
        impl jibbers_distribution::install::Fetch for LocalAssets {
            fn get(&self, url: &str, max: usize) -> Result<Vec<u8>, String> {
                let filename = url.rsplit('/').next().ok_or("missing asset name")?;
                if filename.contains(['\\', ':']) || matches!(filename, "." | "..") { return Err("unsafe asset name".into()); }
                let bytes = std::fs::read(self.0.join(filename)).map_err(|error| error.to_string())?;
                if bytes.len() > max { return Err("asset exceeds download limit".into()); }
                Ok(bytes)
            }
        }
        println!("{}", jibbers_distribution::manager::startup(&root, &LocalAssets(channel.into())).expect("startup migration"));
        for name in ["jibbers_fpv", "jibbers_atmosphere", "jibbers_reshred"] {
            let bytes = std::fs::read(root.join("JibbersMods").join(format!("{name}.dll"))).unwrap();
            assert!(managed_runtime::is_managed(&bytes), "{name} was not migrated to C#");
        }
    }
    match managed_runtime::prepare(&root) {
        Ok(Some(bridge)) => println!("Verified and prepared {}", bridge.display()),
        result => panic!("Managed runtime preparation failed: {result:?}"),
    }
}
