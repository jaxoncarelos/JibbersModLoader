//! Player-build ownership of the shared Mods tab. No developer core or TCP required.

use crate::{core, game_dir, log, modmenu};
use std::sync::OnceLock;

static BOOTSTRAP: OnceLock<()> = OnceLock::new();

/// Only called after core::attach returns, never from a plugin init under CORES.write().
pub fn bootstrap() {
    BOOTSTRAP.get_or_init(|| {
        if game_dir().join(core::CORE_FILE).exists() {
            log::info("release mods menu: skipped; developer core DLL present, it owns the Mods tab");
            return;
        }
        // The watcher builds the tab once the menu exists and rebuilds it every time the main
        // menu is recreated (returning from a server). All Unity work runs on the main thread.
        modmenu::watch();
    });
}

// The shared source's unused developer test driver references probe::describe unconditionally.
// Satisfy that reference without linking a developer inspector or ever reading the object.
pub(crate) mod probe {
    pub unsafe fn describe(_object: *mut crate::il2cpp::Object, _depth: u32) -> serde_json::Value {
        serde_json::json!({"error": "developer inspection unavailable in release loader"})
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn host_is_the_plugin_host_table() {
        assert!(std::ptr::eq(crate::host(), core::host()));
        assert_eq!(crate::host().abi_version, crate::abi::ABI_VERSION);
    }
}