//! The SDK runtime plugin (`JibbersMods\jibbers_sdk.dll`).
//!
//! It is an ordinary plugin built with the SDK, with two jobs:
//!
//! 1. **Shared services** for other mods, as `sdk.*` commands ([`jibbers_mod_sdk::runtime`] is the
//!    client). New services ship by updating this DLL, so mods gain features without being rebuilt.
//! 2. **The auto-updater**: shortly after the game starts it checks a signed manifest and installs a
//!    newer SDK release (this DLL, the docs and the SDK sources) under `JibbersMods\`. See `update`.
//!
//! The runtime does nothing to the game itself: it hooks nothing and touches no Unity objects. The
//! `sdk.symbols.*` commands only read the game's IL2CPP metadata (class, field and method lists).

pub mod http;
pub mod manifest;
pub mod trust;
pub mod update;
pub mod loader_migration;

use jibbers_mod_sdk::plugin::{Info, Plugin};
use jibbers_mod_sdk::{declare_plugin, host, json, log, settings, symbols, Setting, Value};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use std::time::Duration;

/// Version of this runtime (the SDK release it belongs to).
pub const RUNTIME_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Commands this runtime answers. Mods can test for one with `jibbers_mod_sdk::runtime::has_feature`.
pub const FEATURES: &[&str] = &["sdk.info", "sdk.plugins", "sdk.paths", "sdk.log", "sdk.build", "sdk.update.status", "sdk.update.check", "sdk.symbols.report", "sdk.symbols.dump"];

/// The compatibility list this runtime shipped with (`sdk/compat/compat.json`). Like the loader's
/// own copy, it is superseded by a newer one the updater installed.
const EMBEDDED_COMPAT: &str = include_str!("../../../compat/compat.json");

/// Release host, as a folder URL that serves `manifest.json`, `manifest.json.sig` and the assets.
/// Set it for a build with `JIBBERS_SDK_UPDATE_URL`; later releases can move it (`baseUrl` in the manifest).
const DEFAULT_UPDATE_URL: &str = match option_env!("JIBBERS_SDK_UPDATE_URL") {
    Some(url) => url,
    None => "https://github.com/OWNER/REPO/releases/latest/download",
};

/// How long after loading the first check waits, so it never competes with the game's start-up.
const START_DELAY: Duration = Duration::from_secs(15);

static STATUS: Mutex<Option<Value>> = Mutex::new(None);
static RUNNING: AtomicBool = AtomicBool::new(false);

fn update_url_configured() -> bool {
    DEFAULT_UPDATE_URL.starts_with("https://") && !DEFAULT_UPDATE_URL.contains("OWNER/REPO")
}

fn now() -> u64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

fn set_status(v: Value) {
    *STATUS.lock().unwrap() = Some(v);
}

fn status() -> Value {
    STATUS.lock().unwrap().clone().unwrap_or_else(|| {
        if update_url_configured() {
            json!({"state": "idle"})
        } else {
            json!({"state": "notConfigured", "reason": "this build has no update URL"})
        }
    })
}

fn run_check() {
    let mods_dir = jibbers_mod_sdk::mods_dir();
    let mut base_urls: Vec<String> = update::read_installed(&mods_dir).and_then(|i| i.base_url).into_iter().collect();
    if update_url_configured() {
        base_urls.push(DEFAULT_UPDATE_URL.to_string());
    }
    base_urls.dedup();
    if base_urls.is_empty() {
        set_status(status());
        return;
    }
    set_status(json!({"state": "checking", "at": now()}));
    let cfg = update::Config { base_urls, mods_dir, keys: trust::KEYS, loader_abi: jibbers_mod_sdk::abi::ABI_VERSION };
    let outcome = std::panic::catch_unwind(|| update::check_and_apply(&cfg, &http::WinHttp)).unwrap_or_else(|_| update::Outcome::Failed("internal error (panic) while updating".into()));
    log::info(format!("sdk update: {}", outcome.to_json()));
    if matches!(outcome, update::Outcome::Updated { .. } | update::Outcome::UpToDate { .. }) {
        schedule_loader_update();
    }
    if matches!(outcome, update::Outcome::Updated { .. }) {
        // The loader decided at launch; a list that now covers this game build takes effect at the next one.
        let b = game_build();
        if b["allowsMods"] == json!(true) && b["loadedWithMods"] == json!(false) {
            log::info("sdk update: this game build is now validated; restart the game to turn the mods on");
        }
    }
    let mut v = outcome.to_json();
    v["at"] = json!(now());
    set_status(v);
}

/// Starts a check on its own thread unless one is running. Returns whether one was started.
fn spawn_check(delay: Duration) -> bool {
    if RUNNING.swap(true, Ordering::SeqCst) {
        return false;
    }
    let started = std::thread::Builder::new().name("jibbers-sdk-update".into()).spawn(move || {
        std::thread::sleep(delay);
        run_check();
        RUNNING.store(false, Ordering::SeqCst);
    });
    if started.is_err() {
        RUNNING.store(false, Ordering::SeqCst);
    }
    started.is_ok()
}

/// The running game build against the compatibility list (what the loader checked at launch).
fn game_build() -> Value {
    use jibbers_mod_sdk::gamebuild::{self, Verdict};
    let fp = gamebuild::fingerprint_loaded();
    let (verdict, notes) = gamebuild::assess(EMBEDDED_COMPAT, &jibbers_mod_sdk::mods_dir(), &fp);
    let kind = match &verdict {
        Verdict::Validated { .. } => "validated",
        Verdict::Unvalidated { .. } => "unvalidated",
        Verdict::Unidentified { .. } => "unidentified",
        Verdict::Forced { .. } => "forced",
    };
    // The loader decided at launch and left `MODS-DISABLED.txt` if it refused the build.
    let loaded_with_mods = !jibbers_mod_sdk::mods_dir().join("MODS-DISABLED.txt").exists();
    json!({
        "verdict": kind,
        "allowsMods": verdict.allows_mods(),
        "loadedWithMods": loaded_with_mods,
        "message": verdict.message(),
        "fingerprint": fp.as_ref().ok().map(|f| json!({"assemblySize": f.assembly_size, "peTimestamp": f.pe_timestamp, "headerHash": format!("{:016x}", f.header_hash), "unity": f.unity})),
        "error": fp.as_ref().err(),
        "notes": notes,
    })
}

/// `sdk.symbols.report`: what every catalogue symbol resolves to on the running build.
fn symbols_report() -> Result<Value, String> {
    host::pump::run(|| unsafe { symbols::report() })?
}

/// `sdk.symbols.dump`: writes the game's class metadata for the offline `sdk-symbols` tool.
fn symbols_dump(req: &Value) -> Result<Value, String> {
    let path = match req.get("path").and_then(Value::as_str) {
        Some(p) => std::path::PathBuf::from(p),
        None => jibbers_mod_sdk::game_dir().join("jibbers_symbols_dump.json"),
    };
    let images: Vec<String> = match req.get("images") {
        Some(Value::Array(a)) => a.iter().map(|v| v.as_str().map(str::to_string).ok_or("images must be strings")).collect::<Result<_, _>>()?,
        Some(_) => return Err("images must be an array of image names".into()),
        None => vec![],
    };
    let p = path.clone();
    let classes = host::pump::run(move || unsafe { symbols::dump(&p, &images.iter().map(String::as_str).collect::<Vec<_>>()) })??;
    Ok(json!({"path": path, "classes": classes}))
}

fn info() -> Value {
    json!({
        "sdkVersion": RUNTIME_VERSION,
        "installedVersion": update::read_installed(&jibbers_mod_sdk::mods_dir()).map(|i| i.sdk_version),
        "abi": jibbers_mod_sdk::abi::ABI_VERSION,
        "features": FEATURES,
        "update": status(),
        "autoUpdate": settings::get_bool("autoUpdate"),
        "gameBuild": game_build(),
    })
}

/// Every other loaded plugin with its `plugin.info`. (Not this one: a plugin must not call itself
/// through the loader.)
fn plugins() -> Value {
    let me = jibbers_mod_sdk::runtime::RUNTIME_FILE;
    let list: Vec<Value> = host::list_plugins()
        .into_iter()
        .filter(|p| !p.file.eq_ignore_ascii_case(me))
        .map(|p| {
            let info = host::call_plugin(p.id, &json!({"cmd": "plugin.info"})).unwrap_or(Value::Null);
            json!({"id": p.id, "file": p.file, "developer": p.developer, "info": info})
        })
        .collect();
    json!({"plugins": list})
}

fn paths() -> Value {
    let game = jibbers_mod_sdk::game_dir();
    let mods = jibbers_mod_sdk::mods_dir();
    json!({
        "gameDir": game,
        "modsDir": mods,
        "sdkDir": mods.join("sdk"),
        "log": game.join("jibbers_hotload.log"),
        "settingsPattern": mods.join("<plugin id>.settings.json"),
    })
}

fn write_log(req: &Value) -> Result<Value, String> {
    let msg = req.get("msg").and_then(Value::as_str).ok_or("sdk.log needs \"msg\"")?;
    let from = req.get("from").and_then(Value::as_str).unwrap_or("mod");
    let clip = |s: &str, n: usize| s.chars().take(n).collect::<String>();
    log::info(format!("[{}] {}", clip(from, 40), clip(msg, 2000)));
    Ok(json!({}))
}

pub struct SdkRuntime;

static LOADER_SCHEDULED: AtomicBool = AtomicBool::new(false);
fn schedule_loader_update() {
    if LOADER_SCHEDULED.swap(true, Ordering::SeqCst) { return; }
    match loader_migration::schedule(&jibbers_mod_sdk::game_dir(), trust::KEYS) {
        Ok(true) => log::info("sdk: verified loader update scheduled for game exit; new phone UI takes effect next launch"),
        Ok(false) => { LOADER_SCHEDULED.store(false, Ordering::SeqCst); }
        Err(error) => { LOADER_SCHEDULED.store(false, Ordering::SeqCst); log::info(format!("sdk: loader update was not scheduled: {error}")); }
    }
}

impl Plugin for SdkRuntime {
    const INFO: Info = Info {
        id: "sdk",
        name: "Mod SDK",
        version: RUNTIME_VERSION,
        author: "Jaxon",
        description: "Shared services for mods, and updates for the SDK itself.",
    };
    // Services stay available whatever the user does in the Mods tab.
    const ENABLED_TOGGLE: bool = false;

    fn settings() -> Vec<Setting> {
        vec![Setting::toggle("autoUpdate", "Auto-update SDK", true)
            .section("Updates")
            .tooltip("At game start, fetch newer SDK files (runtime, docs) from the release host. Takes effect at the next start.")]
    }

    fn init() {
        if !settings::get_bool("autoUpdate") {
            log::info("sdk: auto-update is off");
        } else if std::env::var_os("JIBBERS_SDK_NO_UPDATE").is_some() {
            log::info("sdk: auto-update disabled by JIBBERS_SDK_NO_UPDATE");
        } else if update_url_configured() || update::read_installed(&jibbers_mod_sdk::mods_dir()).is_some_and(|i| i.base_url.is_some()) {
            // A legacy updater may have delivered this runtime and loader payload last launch.
            // Filesystem work only; no host/plugin calls while the loader holds its core lock.
            std::thread::spawn(schedule_loader_update);
            spawn_check(START_DELAY);
        } else {
            log::info("sdk: this build has no update URL; auto-update is idle");
        }
    }

    fn command(cmd: &str, req: &Value) -> Option<Result<Value, String>> {
        Some(match cmd {
            "sdk.info" => Ok(info()),
            "sdk.plugins" => Ok(plugins()),
            "sdk.paths" => Ok(paths()),
            "sdk.log" => write_log(req),
            "sdk.build" => Ok(game_build()),
            "sdk.update.status" => Ok(json!({"update": status(), "running": RUNNING.load(Ordering::SeqCst)})),
            "sdk.update.check" => Ok(json!({"started": spawn_check(Duration::ZERO), "update": status()})),
            "sdk.symbols.report" => symbols_report(),
            "sdk.symbols.dump" => symbols_dump(req),
            _ => return None,
        })
    }
}

declare_plugin!(SdkRuntime);

#[cfg(test)]
mod tests {
    use super::*;
    use jibbers_mod_sdk::plugin::Runtime;

    #[test]
    fn declares_a_valid_plugin() {
        let rt = Runtime::new::<SdkRuntime>(None).unwrap();
        let info = rt.handle(&json!({"cmd": "plugin.info"})).unwrap();
        assert_eq!((&info["id"], &info["name"]), (&json!("sdk"), &json!("Mod SDK")));
        let keys: Vec<&str> = info["settings"].as_array().unwrap().iter().map(|s| s["key"].as_str().unwrap()).collect();
        assert_eq!(keys, ["autoUpdate"], "no enabled toggle");
    }

    #[test]
    fn answers_its_commands() {
        let rt = Runtime::new::<SdkRuntime>(None).unwrap();
        for f in FEATURES {
            if matches!(*f, "sdk.plugins" | "sdk.update.check" | "sdk.symbols.report" | "sdk.symbols.dump") {
                continue; // need the loader / the network / the game
            }
            let r = rt.handle(&json!({"cmd": f, "msg": "hello"}));
            assert!(r.is_ok(), "{f}: {r:?}");
        }
        let info = rt.handle(&json!({"cmd": "sdk.info"})).unwrap();
        assert_eq!(info["sdkVersion"], json!(RUNTIME_VERSION));
        assert!(info["features"].as_array().unwrap().iter().any(|f| f == "sdk.log"));
        assert!(rt.handle(&json!({"cmd": "sdk.nope"})).is_err());
        assert!(rt.handle(&json!({"cmd": "sdk.log"})).is_err());
    }

    #[test]
    fn build_reports_a_verdict_and_the_embedded_list_parses() {
        assert!(jibbers_mod_sdk::gamebuild::CompatList::parse(EMBEDDED_COMPAT).is_ok());
        let rt = Runtime::new::<SdkRuntime>(None).unwrap();
        let b = rt.handle(&json!({"cmd": "sdk.build"})).unwrap();
        // not inside the game here: the build can't be identified, so the verdict fails closed
        assert_eq!(b["verdict"], json!("unidentified"));
        assert_eq!(b["allowsMods"], json!(false));
        assert!(b["message"].as_str().unwrap().contains("mods disabled"));
    }

    #[test]
    fn placeholder_update_url_is_inert() {
        // Release builds set JIBBERS_SDK_UPDATE_URL; the placeholder must never be fetched.
        if DEFAULT_UPDATE_URL.contains("OWNER/REPO") {
            assert!(!update_url_configured());
        }
    }
}
