//! The plugin skeleton: implement [`Plugin`], call [`declare_plugin!`], and the SDK provides the
//! DLL exports, the `plugin.*` protocol the Mods tab speaks, and settings persistence.
//!
//! ```ignore
//! use jibbers_mod_sdk::{declare_plugin, plugin::{Info, Plugin}, Setting, Value};
//!
//! struct MyMod;
//! impl Plugin for MyMod {
//!     const INFO: Info = Info { id: "my_mod", name: "My Mod", version: env!("CARGO_PKG_VERSION"), author: "me", description: "Does a thing." };
//!     fn settings() -> Vec<Setting> { vec![Setting::slider("power", "Power", 1.0, 0.0, 2.0, 0.1)] }
//!     fn init() { /* install hooks, read settings */ }
//! }
//! declare_plugin!(MyMod);
//! ```
//!
//! Lifecycle: the loader calls `jh_core_init` once at startup (on its worker thread, with the
//! plugin list locked), then routes JSON requests to `jh_core_handle` from any thread. Plugins load
//! once per game launch; a restart is needed to pick up a new DLL.

use crate::settings::{Setting, Store};
use crate::{abi, host, il2cpp, log};
use serde_json::{json, Value};
use std::path::PathBuf;
use std::sync::OnceLock;

/// Identity of a plugin, shown in the Mods tab.
#[derive(Clone, Copy, Debug)]
pub struct Info {
    /// Stable machine id: lowercase letters, digits and underscores. Names the settings file
    /// (`JibbersMods\<id>.settings.json`) and prefixes your custom commands (`<id>.something`).
    pub id: &'static str,
    /// Heading of the plugin's section in the Mods tab.
    pub name: &'static str,
    pub version: &'static str,
    pub author: &'static str,
    pub description: &'static str,
}

/// What a mod implements. Everything except `INFO` and `settings` is optional.
pub trait Plugin: 'static {
    const INFO: Info;

    /// Adds an `enabled` toggle (labelled with the plugin's name) as the first setting. Turn it off
    /// only for services that should always run. Read it with `settings::enabled()`.
    const ENABLED_TOGGLE: bool = true;
    /// Whether this plugin has a player-facing settings section. Internal services can hide
    /// themselves without hiding the gameplay mods they register.
    const SHOW_IN_MENU: bool = true;
    /// The plugin's settings. Called once at load. (`enabled` is added for you.)
    fn settings() -> Vec<Setting>;

    /// Runs once at load, after the settings are available: look up game classes and install hooks
    /// here ([`host::pre_hook_method`]). Don't call other plugins from here, and don't block.
    fn init() {}

    /// A setting was changed (from the Mods tab or `plugin.set`); `value` is the stored value.
    /// May run on any thread: use [`host::pump::run`] before touching Unity.
    fn changed(_key: &str, _value: &Value) {}

    /// A custom request, `{"cmd": "<id>.something", ...}`. Return None if `cmd` isn't yours. The
    /// reply gets `"ok": true` added unless you set it. Namespace commands with your id.
    fn command(_cmd: &str, _req: &Value) -> Option<Result<Value, String>> {
        None
    }

    /// The loader is about to free the DLL (developer reload; normally only on exit). Remove any
    /// game objects you created whose callbacks point into your code. Don't call other plugins.
    fn unload() {}
}

type ChangedFn = fn(&str, &Value);
type CommandFn = fn(&str, &Value) -> Option<Result<Value, String>>;

/// A plugin's identity, settings and callbacks. Separate from the global so it can be tested.
pub struct Runtime {
    info: Info,
    show_in_menu: bool,
    store: Store,
    changed: ChangedFn,
    command: CommandFn,
    unload: fn(),
}

impl Runtime {
    pub fn new<P: Plugin>(path: Option<PathBuf>) -> Result<Runtime, String> {
        let info = P::INFO;
        if info.id.is_empty() || !info.id.chars().all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '_') {
            return Err(format!("plugin id {:?} must be lowercase letters, digits and underscores", info.id));
        }
        let store = Store::new(P::settings(), P::ENABLED_TOGGLE.then_some(info.name), path)?;
        Ok(Runtime { info, show_in_menu: P::SHOW_IN_MENU, store, changed: P::changed, command: P::command, unload: P::unload })
    }

    pub fn store(&self) -> &Store {
        &self.store
    }

    fn info_json(&self) -> Value {
        json!({
            "ok": true,
            "id": self.info.id,
            "name": self.info.name,
            "version": self.info.version,
            "author": self.info.author,
            "description": self.info.description,
            "sdk": crate::SDK_VERSION,
            "show_in_menu": self.show_in_menu,
            "enabled": self.store.get("enabled").and_then(|v| v.as_bool()).unwrap_or(true),
            "settings": self.store.schema(),
        })
    }

    fn set(&self, key: &str, value: &Value) -> Result<Value, String> {
        let stored = self.store.set(key, value)?;
        (self.changed)(key, &stored);
        Ok(json!({"ok": true, "values": self.store.values()}))
    }

    /// Answers one request: the `plugin.*` protocol, then the plugin's own commands.
    pub fn handle(&self, req: &Value) -> Result<Value, String> {
        let cmd = req.get("cmd").and_then(Value::as_str).unwrap_or("");
        match cmd {
            "plugin.info" => Ok(self.info_json()),
            "plugin.get" => Ok(json!({"ok": true, "values": self.store.values()})),
            "plugin.set" => {
                let key = req.get("key").and_then(Value::as_str).ok_or("plugin.set needs a key")?;
                self.set(key, req.get("value").ok_or("plugin.set needs a value")?)
            }
            "plugin.enable" => {
                let on = req.get("on").and_then(Value::as_bool).ok_or("plugin.enable needs \"on\": true or false")?;
                self.set("enabled", &Value::Bool(on))?;
                Ok(json!({"ok": true, "enabled": on}))
            }
            other if other.starts_with("plugin.") => Err(format!("unknown plugin command {other:?}")),
            other => match (self.command)(other, req) {
                Some(Ok(mut v)) => {
                    if let Some(o) = v.as_object_mut() {
                        o.entry("ok").or_insert(Value::Bool(true));
                    }
                    Ok(v)
                }
                Some(Err(e)) => Err(e),
                None => Err(format!("{:?} is not a command of plugin {:?}", other, self.info.id)),
            },
        }
    }
}

static RUNTIME: OnceLock<Runtime> = OnceLock::new();

/// The plugin's settings store (None before init).
pub fn store() -> Option<&'static Store> {
    RUNTIME.get().map(Runtime::store)
}

/// Settings file of plugin `id`: `<game>\JibbersMods\<id>.settings.json`.
pub fn settings_path(id: &str) -> PathBuf {
    crate::mods_dir().join(format!("{id}.settings.json"))
}

/// Generated `jh_core_init`. Returns the ABI version, or 0 to tell the loader to skip the plugin.
pub unsafe fn init<P: Plugin>(host_table: *const abi::Host) -> u32 {
    let r = std::panic::catch_unwind(|| {
        if !host::set_host(host_table) {
            log::info(format!("{}: loader ABI doesn't match this SDK's ABI {}; reinstall the loader", P::INFO.id, abi::ABI_VERSION));
            return 0;
        }
        if let Err(e) = il2cpp::init() {
            log::info(format!("{}: {e}", P::INFO.id));
            return 0;
        }
        let runtime = match Runtime::new::<P>(Some(settings_path(P::INFO.id))) {
            Ok(r) => r,
            Err(e) => {
                log::info(format!("{}: invalid plugin declaration: {e}", P::INFO.id));
                return 0;
            }
        };
        let _ = RUNTIME.set(runtime);
        P::init();
        log::info(format!("{} {} loaded (SDK {})", P::INFO.name, P::INFO.version, crate::SDK_VERSION));
        abi::ABI_VERSION
    });
    r.unwrap_or_else(|_| {
        log::info(format!("{}: panic in init", P::INFO.id));
        0
    })
}

/// Generated `jh_core_handle`: one JSON request in, one JSON reply out.
pub unsafe fn handle_raw(req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize) {
    let bytes = if req.is_null() { &[][..] } else { std::slice::from_raw_parts(req, len) };
    let reply = match serde_json::from_slice::<Value>(bytes) {
        Ok(req) => match RUNTIME.get() {
            Some(rt) => std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| rt.handle(&req)))
                .unwrap_or_else(|_| Err("internal error (panic)".into()))
                .unwrap_or_else(|e| json!({"ok": false, "error": e})),
            None => json!({"ok": false, "error": "plugin not initialised"}),
        },
        Err(e) => json!({"ok": false, "error": format!("bad request: {e}")}),
    };
    let mut s = reply.to_string().into_bytes().into_boxed_slice();
    *out_len = s.len();
    *out = s.as_mut_ptr();
    std::mem::forget(s);
}

/// Generated `jh_core_free`: frees a reply from [`handle_raw`].
pub unsafe fn free_raw(p: *mut u8, len: usize) {
    if !p.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(p, len)));
    }
}

/// Generated `jh_core_unload`.
pub unsafe fn unload() {
    if let Some(rt) = RUNTIME.get() {
        let f = rt.unload;
        let _ = std::panic::catch_unwind(f);
    }
    // The game must never call into this DLL once it is gone: drop what the SDK handed out.
    if crate::events::any() || crate::vars::any() {
        let r = crate::host::pump::run(|| unsafe {
            let a = crate::events::unsubscribe_all();
            let b = crate::vars::unwatch_all();
            a.and(b)
        });
        if let Err(e) = r.and_then(|r| r) {
            crate::log::info(format!("unload: could not remove game subscriptions: {e}"));
        }
    }
}

/// Generates the four exports the loader looks for (`jh_core_init`, `jh_core_handle`,
/// `jh_core_free`, `jh_core_unload`) for a type implementing [`Plugin`]. Use once per crate.
#[macro_export]
macro_rules! declare_plugin {
    ($plugin:ty) => {
        #[unsafe(no_mangle)]
        pub unsafe extern "C" fn jh_core_init(host: *const $crate::abi::Host) -> u32 {
            $crate::plugin::init::<$plugin>(host)
        }

        #[unsafe(no_mangle)]
        pub unsafe extern "C" fn jh_core_handle(req: *const u8, len: usize, out: *mut *mut u8, out_len: *mut usize) {
            $crate::plugin::handle_raw(req, len, out, out_len)
        }

        #[unsafe(no_mangle)]
        pub unsafe extern "C" fn jh_core_free(p: *mut u8, len: usize) {
            $crate::plugin::free_raw(p, len)
        }

        #[unsafe(no_mangle)]
        pub unsafe extern "C" fn jh_core_unload() {
            $crate::plugin::unload()
        }
    };
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    static CHANGES: AtomicU32 = AtomicU32::new(0);

    struct Demo;
    impl Plugin for Demo {
        const INFO: Info = Info { id: "demo_mod", name: "Demo", version: "1.2.3", author: "tester", description: "d" };
        fn settings() -> Vec<Setting> {
            vec![Setting::slider("power", "Power", 1.0, 0.0, 2.0, 0.1), Setting::choice("mode", "Mode", &["a", "b"], "a")]
        }
        fn changed(_key: &str, _value: &Value) {
            CHANGES.fetch_add(1, Ordering::SeqCst);
        }
        fn command(cmd: &str, req: &Value) -> Option<Result<Value, String>> {
            match cmd {
                "demo_mod.echo" => Some(Ok(json!({"echo": req["text"]}))),
                "demo_mod.fail" => Some(Err("nope".into())),
                _ => None,
            }
        }
    }

    fn rt() -> Runtime {
        Runtime::new::<Demo>(None).unwrap()
    }

    #[test]
    fn info_matches_what_the_mods_tab_reads() {
        let i = rt().handle(&json!({"cmd": "plugin.info"})).unwrap();
        assert_eq!(i["show_in_menu"], true);
        assert_eq!((&i["ok"], &i["id"], &i["name"], &i["version"], &i["enabled"]), (&json!(true), &json!("demo_mod"), &json!("Demo"), &json!("1.2.3"), &json!(true)));
        let keys: Vec<&str> = i["settings"].as_array().unwrap().iter().map(|s| s["key"].as_str().unwrap()).collect();
        assert_eq!(keys, ["enabled", "power", "mode"]);
    }

    #[test]
    fn internal_services_can_hide_their_section_without_disabling_commands() {
        struct Service;
        impl Plugin for Service {
            const INFO: Info = Info { id: "service", name: "Service", version: "1", author: "", description: "" };
            const ENABLED_TOGGLE: bool = false;
            const SHOW_IN_MENU: bool = false;
            fn settings() -> Vec<Setting> { vec![] }
            fn command(cmd: &str, _: &Value) -> Option<Result<Value, String>> {
                (cmd == "service.status").then(|| Ok(json!({"running": true})))
            }
        }
        let service = Runtime::new::<Service>(None).unwrap();
        let info = service.handle(&json!({"cmd": "plugin.info"})).unwrap();
        assert_eq!(info["show_in_menu"], false);
        assert_eq!(info["enabled"], true);
        assert_eq!(service.handle(&json!({"cmd": "service.status"})).unwrap()["running"], true);
    }

    #[test]
    fn set_get_and_enable_round_trip() {
        let rt = rt();
        let before = CHANGES.load(Ordering::SeqCst);
        let r = rt.handle(&json!({"cmd": "plugin.set", "key": "power", "value": 5.0})).unwrap();
        assert_eq!(r["values"]["power"], json!(2.0), "clamped");
        assert_eq!(rt.handle(&json!({"cmd": "plugin.get"})).unwrap()["values"]["power"], json!(2.0));
        let e = rt.handle(&json!({"cmd": "plugin.enable", "on": false})).unwrap();
        assert_eq!(e["enabled"], json!(false));
        assert_eq!(rt.handle(&json!({"cmd": "plugin.info"})).unwrap()["enabled"], json!(false));
        assert_eq!(CHANGES.load(Ordering::SeqCst) - before, 2);
        assert!(rt.handle(&json!({"cmd": "plugin.set", "key": "mode", "value": "z"})).is_err());
        assert!(rt.handle(&json!({"cmd": "plugin.set", "key": "mode"})).is_err());
        assert!(rt.handle(&json!({"cmd": "plugin.enable"})).is_err());
        assert!(rt.handle(&json!({"cmd": "plugin.bogus"})).is_err());
    }

    #[test]
    fn custom_commands_reach_the_plugin() {
        let rt = rt();
        let r = rt.handle(&json!({"cmd": "demo_mod.echo", "text": "hi"})).unwrap();
        assert_eq!((&r["ok"], &r["echo"]), (&json!(true), &json!("hi")));
        assert_eq!(rt.handle(&json!({"cmd": "demo_mod.fail"})).unwrap_err(), "nope");
        assert!(rt.handle(&json!({"cmd": "other.thing"})).unwrap_err().contains("not a command"));
        assert!(rt.handle(&json!({})).is_err());
    }

    #[test]
    fn plugin_ids_are_validated() {
        struct Bad;
        impl Plugin for Bad {
            const INFO: Info = Info { id: "Bad Id", name: "x", version: "0", author: "", description: "" };
            fn settings() -> Vec<Setting> {
                vec![]
            }
        }
        assert!(Runtime::new::<Bad>(None).is_err());
    }

    #[test]
    fn raw_handle_replies_with_json_and_survives_garbage() {
        // No runtime has been installed globally in tests: both paths must answer, never crash.
        for body in [&b"{\"cmd\":\"plugin.info\"}"[..], b"not json", b""] {
            let (mut out, mut n) = (std::ptr::null_mut(), 0usize);
            unsafe {
                handle_raw(body.as_ptr(), body.len(), &mut out, &mut n);
                let v: Value = serde_json::from_slice(std::slice::from_raw_parts(out, n)).unwrap();
                assert_eq!(v["ok"], json!(false));
                free_raw(out, n);
            }
        }
    }
}
