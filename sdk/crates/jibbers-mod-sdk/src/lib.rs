//! Jibbers mod SDK.
//!
//! A mod is a Windows x64 `cdylib` that the Jibbers mod loader (`version.dll`) loads from
//! `<game>\JibbersMods\`. This crate gives you:
//!
//! - [`declare_plugin!`] / [`plugin::Plugin`]: the exports the loader expects, the settings
//!   protocol the in-game **Options > Mods** tab uses, and JSON persistence of the settings.
//! - [`settings`]: describe settings (toggle, slider, choice, key) and read them anywhere.
//! - [`host`]: run code on Unity's main thread ([`host::pump`]), hook game methods
//!   ([`host::pre_hook_method`]), talk to other plugins, including the SDK runtime ([`runtime`]).
//! - [`il2cpp`] / [`unity`]: raw IL2CPP lookups and thin Unity helpers. All unsafe, main thread only.
//! - [`symbols`] / [`shape`]: stable names for the game's obfuscated classes and members, found
//!   by shape (field types, signatures) so they survive game updates. See `docs/SYMBOLS.md`.
//! - [`events`]: subscribe to the game's own events (spawn, map load, tricks, replay, pause...).
//! - [`vars`]: the game's named settings store: read, watch and write options.
//! - [`delegates`]: game delegates backed by native functions, and generic-type reflection.
//! - [`inspect`]: JSON descriptions of live objects, for logs and probes.
//! - [`log`]: append to `jibbers_hotload.log` beside the game executable.
//! - [`gamebuild`]: fingerprint of the running game and the list of builds the mods are validated for.
//!
//! See `docs/SDK.md` in the SDK folder for the full guide.

pub mod abi;
pub mod delegates;
pub mod events;
pub mod gamebuild;
pub mod host;
pub mod il2cpp;
pub mod inspect;
pub mod log;
pub mod plugin;
pub mod runtime;
pub mod settings;
pub mod shape;
pub mod symbols;
pub mod unity;
pub mod vars;

pub use serde_json::{self, json, Value};
pub use settings::Setting;

use std::path::PathBuf;
use windows_sys::Win32::System::LibraryLoader::GetModuleFileNameW;

/// Version of this SDK crate.
pub const SDK_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Folder containing the game executable.
pub fn game_dir() -> PathBuf {
    let mut buf = [0u16; 1024];
    let n = unsafe { GetModuleFileNameW(std::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32) } as usize;
    let exe = PathBuf::from(String::from_utf16_lossy(&buf[..n]));
    exe.parent().map(|p| p.to_path_buf()).unwrap_or_default()
}

/// `<game>\JibbersMods`, where plugin DLLs and their settings files live.
pub fn mods_dir() -> PathBuf {
    game_dir().join("JibbersMods")
}
