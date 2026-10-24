//! Signed public mod catalog and startup installer shared by the stable bootstrap and menu.
pub mod catalog;
pub mod install;
pub mod manager;
// Reuse the existing HTTPS transport and signing identity without changing the SDK updater.
pub mod update { pub use crate::install::Fetch; }
#[cfg(windows)]
#[path = "../../../sdk/crates/jibbers-sdk-runtime/src/http.rs"]
pub mod http;
#[cfg(not(windows))]
pub mod http {
    // Native previews bundle their own platform files. Never consume the Windows catalog.
    pub struct WinHttp;
    pub fn split_url(url: &str) -> Result<(String, u16, String), String> {
        let rest = url.strip_prefix("https://").ok_or("HTTPS required")?;
        let (host, path) = rest.split_once('/').unwrap_or((rest, ""));
        if host.is_empty() || host.contains('@') || host.contains(char::is_whitespace) { return Err("Invalid HTTPS URL".into()); }
        Ok((host.to_owned(), 443, format!("/{path}")))
    }
    impl crate::install::Fetch for WinHttp {
        fn get(&self, _: &str, _: usize) -> Result<Vec<u8>, String> {
            Err("The native macOS preview bundles mods; the Windows update catalog is disabled.".into())
        }
    }
}
#[path = "../../../sdk/crates/jibbers-sdk-runtime/src/trust.rs"]
pub mod trust;

pub const CATALOG_URL: &str = "https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json";
pub const LOADER_PATH: &str = "JibbersMods/system/jibbers_loader.dll";
pub const STATE_DIR: &str = "JibbersMods/system/distribution";
#[cfg(test)]
mod tests;
