//! Signed public mod catalog and startup installer shared by the stable bootstrap and menu.
pub mod catalog;
pub mod install;
pub mod manager;
// Reuse the existing HTTPS transport and signing identity without changing the SDK updater.
pub mod update { pub use crate::install::Fetch; }
#[path = "../../../sdk/crates/jibbers-sdk-runtime/src/http.rs"]
pub mod http;
#[path = "../../../sdk/crates/jibbers-sdk-runtime/src/trust.rs"]
pub mod trust;

pub const CATALOG_URL: &str = "https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json";
pub const LOADER_PATH: &str = "JibbersMods/system/jibbers_loader.dll";
pub const STATE_DIR: &str = "JibbersMods/system/distribution";
#[cfg(test)]
mod tests;
