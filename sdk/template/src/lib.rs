//! Starter mod. Copy this folder, rename the crate in Cargo.toml, change `INFO.id`, and edit.
//!
//! What it shows:
//! - settings (toggle, slider, choice, key) that appear under Options > Mods and are saved for you;
//! - a hook that runs on the game's main thread once per frame;
//! - a custom command other tools (and your agent) can call;
//! - reading a setting from anywhere with `plugin::store()`.
//!
//! Build: `cargo build --release` -> `target\release\my_mod.dll`; copy it into `<game>\JibbersMods\`.

use jibbers_mod_sdk::plugin::{self, Info, Plugin};
use jibbers_mod_sdk::{declare_plugin, host, json, log, Setting, Value};
use std::ffi::c_void;
use std::sync::atomic::{AtomicU64, Ordering};

static FRAMES: AtomicU64 = AtomicU64::new(0);

struct MyMod;

impl Plugin for MyMod {
    const INFO: Info = Info {
        id: "my_mod", // lowercase letters, digits, underscore. Names JibbersMods\my_mod.settings.json
        name: "My Mod",
        version: env!("CARGO_PKG_VERSION"),
        author: "you",
        description: "Starter mod from the Jibbers SDK.",
    };

    fn settings() -> Vec<Setting> {
        // An "enabled" toggle is added for you as the first setting.
        vec![
            Setting::slider("power", "Power", 1.0, 0.0, 2.0, 0.05).section("Tuning").tooltip("Example slider."),
            Setting::choice("mode", "Mode", &["Gentle", "Normal", "Wild"], "Normal").section("Tuning"),
            Setting::key("hotkey", "Hotkey", 0x78).section("Controls"), // 0x78 = F9
        ]
    }

    fn init() {
        // Look up the game's per-frame method and run `on_frame` before each call.
        // Resolve by class and method NAME; never hard-code addresses.
        let hooked = unsafe { host::pre_hook_method("Assembly-CSharp.dll", "", "EnvironmentManager", "Update", 0, on_frame) };
        match hooked {
            Ok(()) => log::info("my_mod: hooked EnvironmentManager.Update"),
            Err(e) => log::info(format!("my_mod: {e}")),
        }
    }

    fn changed(key: &str, value: &Value) {
        log::info(format!("my_mod: {key} = {value}"));
    }

    fn command(cmd: &str, _req: &Value) -> Option<Result<Value, String>> {
        match cmd {
            // Namespace your commands with your id.
            "my_mod.status" => Some(Ok(json!({"frames": FRAMES.load(Ordering::Relaxed), "values": plugin::store().map(|s| s.values())}))),
            _ => None,
        }
    }
}

declare_plugin!(MyMod);

/// Runs on the game's main thread before every `EnvironmentManager.Update`. Keep it fast and never
/// let a panic escape (the SDK does not wrap hook callbacks): do the work in a helper that returns.
unsafe extern "C" fn on_frame(this: *mut c_void, _arg: *mut c_void) {
    let _ = this;
    let enabled = plugin::store().and_then(|s| s.get("enabled")).and_then(|v| v.as_bool()).unwrap_or(true);
    if enabled {
        FRAMES.fetch_add(1, Ordering::Relaxed);
    }
}
