//! Settings: describe them once, and the in-game Mods tab, the JSON file and your code all agree.
//!
//! ```ignore
//! fn settings() -> Vec<Setting> {
//!     vec![
//!         Setting::slider("speed", "Speed", 1.0, 0.5, 3.0, 0.1).section("Movement").tooltip("How fast."),
//!         Setting::toggle("trail", "Trail", true),
//!         Setting::choice("mode", "Mode", &["Soft", "Hard"], "Soft"),
//!         Setting::key("hotkey", "Hotkey", 0x58),
//!     ]
//! }
//! // anywhere in the plugin:
//! let speed = jibbers_mod_sdk::settings::get_f64("speed");
//! ```
//!
//! Every plugin also gets an `enabled` toggle (added first, in the "General" section) unless it
//! declares its own `enabled` setting. Read it with [`enabled`].
//!
//! The Mods tab currently shows toggles, sliders and choices; key settings are stored and
//! readable but have no control in the tab yet.

use serde_json::{json, Map, Value};
use std::path::PathBuf;
use std::sync::Mutex;

/// How a setting is edited.
#[derive(Clone, Debug, PartialEq)]
pub enum Kind {
    Toggle,
    Slider { min: f64, max: f64, step: f64 },
    Choice(Vec<String>),
    /// A Windows virtual-key code, 0..=255 (0x58 = X, 0x78 = F9).
    Key,
}

/// One setting. Build with [`Setting::toggle`], [`Setting::slider`], [`Setting::choice`] or
/// [`Setting::key`], then optionally [`Setting::section`] and [`Setting::tooltip`].
#[derive(Clone, Debug, PartialEq)]
pub struct Setting {
    pub key: String,
    pub label: String,
    pub section: String,
    pub tooltip: String,
    pub kind: Kind,
    pub default: Value,
}

impl Setting {
    fn new(key: &str, label: &str, kind: Kind, default: Value) -> Setting {
        Setting { key: key.into(), label: label.into(), section: "General".into(), tooltip: String::new(), kind, default }
    }

    pub fn toggle(key: &str, label: &str, default: bool) -> Setting {
        Setting::new(key, label, Kind::Toggle, json!(default))
    }

    pub fn slider(key: &str, label: &str, default: f64, min: f64, max: f64, step: f64) -> Setting {
        Setting::new(key, label, Kind::Slider { min, max, step }, json!(default))
    }

    pub fn choice(key: &str, label: &str, options: &[&str], default: &str) -> Setting {
        Setting::new(key, label, Kind::Choice(options.iter().map(|s| s.to_string()).collect()), json!(default))
    }

    pub fn key(key: &str, label: &str, default: u32) -> Setting {
        Setting::new(key, label, Kind::Key, json!(default))
    }

    /// Group this setting under a collapsible heading in the Mods tab (default "General").
    pub fn section(mut self, section: &str) -> Setting {
        self.section = section.into();
        self
    }

    pub fn tooltip(mut self, tooltip: &str) -> Setting {
        self.tooltip = tooltip.into();
        self
    }

    /// The `plugin.info` schema entry for this setting.
    pub fn to_json(&self) -> Value {
        let mut e = json!({"key": self.key, "label": self.label, "default": self.default, "section": self.section, "tooltip": self.tooltip});
        let kind = match &self.kind {
            Kind::Toggle => "toggle",
            Kind::Slider { min, max, step } => {
                e["min"] = json!(min);
                e["max"] = json!(max);
                e["step"] = json!(step);
                "slider"
            }
            Kind::Choice(options) => {
                e["options"] = json!(options);
                "choice"
            }
            Kind::Key => "key",
        };
        e["type"] = json!(kind);
        e
    }

    /// Checks `v` against this setting and returns the value to store: sliders are clamped to
    /// their range, choices must be one of the options, keys must be 0..=255.
    pub fn normalize(&self, v: &Value) -> Result<Value, String> {
        let key = &self.key;
        match &self.kind {
            Kind::Toggle => v.as_bool().map(Value::Bool).ok_or_else(|| format!("{key} takes true or false")),
            Kind::Slider { min, max, .. } => {
                let x = v.as_f64().filter(|x| x.is_finite()).ok_or_else(|| format!("{key} takes a number"))?;
                Ok(json!((x.clamp(*min, *max) * 1e6).round() / 1e6))
            }
            Kind::Choice(options) => {
                let s = v.as_str().ok_or_else(|| format!("{key} takes one of {options:?}"))?;
                if options.iter().any(|o| o == s) {
                    Ok(json!(s))
                } else {
                    Err(format!("{key}: {s:?} is not one of {options:?}"))
                }
            }
            Kind::Key => v.as_u64().filter(|k| *k <= 255).map(|k| json!(k)).ok_or_else(|| format!("{key} takes a key code (0-255)")),
        }
    }

    /// Mistakes in the declaration itself (caught when the plugin loads).
    fn check(&self) -> Result<(), String> {
        if self.key.is_empty() {
            return Err("a setting has an empty key".into());
        }
        match &self.kind {
            Kind::Slider { min, max, step } if !(min.is_finite() && max.is_finite() && step.is_finite() && min < max && *step > 0.0) => {
                return Err(format!("{}: slider needs finite min < max and step > 0", self.key));
            }
            Kind::Choice(o) if o.is_empty() => return Err(format!("{}: choice has no options", self.key)),
            _ => {}
        }
        self.normalize(&self.default).map(|_| ()).map_err(|e| format!("bad default: {e}"))
    }
}

/// The current values of one plugin's settings, persisted as JSON.
pub struct Store {
    settings: Vec<Setting>,
    values: Mutex<Map<String, Value>>,
    path: Option<PathBuf>,
}

impl Store {
    /// Validates the declaration, prepends the `enabled` toggle (named `enabled_label`) if the plugin
    /// has none and `enabled_label` is Some, and loads saved values from `path` (a missing or damaged
    /// file means defaults; unknown or invalid entries are ignored). `path` None keeps everything in memory.
    pub fn new(mut settings: Vec<Setting>, enabled_label: Option<&str>, path: Option<PathBuf>) -> Result<Store, String> {
        if let Some(label) = enabled_label {
            if !settings.iter().any(|s| s.key == "enabled") {
                settings.insert(0, Setting::toggle("enabled", label, true).tooltip("Turns this mod on or off."));
            }
        }
        for (i, s) in settings.iter().enumerate() {
            s.check()?;
            if settings[..i].iter().any(|o| o.key == s.key) {
                return Err(format!("duplicate setting key {:?}", s.key));
            }
        }
        let saved: Map<String, Value> = path
            .as_ref()
            .and_then(|p| std::fs::read(p).ok())
            .and_then(|b| serde_json::from_slice::<Value>(&b).ok())
            .and_then(|v| v.as_object().cloned())
            .unwrap_or_default();
        let values = settings
            .iter()
            .map(|s| (s.key.clone(), saved.get(&s.key).and_then(|v| s.normalize(v).ok()).unwrap_or_else(|| s.default.clone())))
            .collect();
        Ok(Store { settings, values: Mutex::new(values), path })
    }

    pub fn settings(&self) -> &[Setting] {
        &self.settings
    }

    /// The `plugin.info` `settings` array.
    pub fn schema(&self) -> Value {
        Value::Array(self.settings.iter().map(Setting::to_json).collect())
    }

    /// Current value of `key`.
    pub fn get(&self, key: &str) -> Option<Value> {
        self.values.lock().unwrap().get(key).cloned()
    }

    /// All current values as an object keyed by setting key.
    pub fn values(&self) -> Value {
        Value::Object(self.values.lock().unwrap().clone())
    }

    /// Validates, stores and saves a new value; returns the value as stored.
    pub fn set(&self, key: &str, value: &Value) -> Result<Value, String> {
        let setting = self.settings.iter().find(|s| s.key == key).ok_or_else(|| format!("unknown setting {key:?}"))?;
        let v = setting.normalize(value)?;
        self.values.lock().unwrap().insert(key.into(), v.clone());
        self.save();
        Ok(v)
    }

    fn save(&self) {
        let Some(path) = &self.path else { return };
        let text = serde_json::to_string_pretty(&self.values()).unwrap_or_default();
        if let Some(dir) = path.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        // Write beside the file, then rename, so a crash never leaves half a settings file.
        let tmp = path.with_extension("json.tmp");
        if std::fs::write(&tmp, text).is_ok() && std::fs::rename(&tmp, path).is_err() {
            let _ = std::fs::remove_file(&tmp);
        }
    }
}

// ---- reading settings from plugin code -------------------------------------------------------

fn current(key: &str) -> Option<Value> {
    crate::plugin::store()?.get(key)
}

/// Current value of any setting (None before the plugin has loaded or for an unknown key).
pub fn value(key: &str) -> Option<Value> {
    current(key)
}

/// A toggle's value (false if unknown).
pub fn get_bool(key: &str) -> bool {
    current(key).and_then(|v| v.as_bool()).unwrap_or(false)
}

/// A slider's value (0.0 if unknown).
pub fn get_f64(key: &str) -> f64 {
    current(key).and_then(|v| v.as_f64()).unwrap_or(0.0)
}

/// A choice's selected option ("" if unknown).
pub fn get_string(key: &str) -> String {
    current(key).and_then(|v| v.as_str().map(String::from)).unwrap_or_default()
}

/// A key setting's virtual-key code (0 if unknown).
pub fn get_key(key: &str) -> u32 {
    current(key).and_then(|v| v.as_u64()).unwrap_or(0) as u32
}

/// True while the plugin's `enabled` toggle is on. Check it at the top of every hook.
pub fn enabled() -> bool {
    get_bool("enabled")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> Vec<Setting> {
        vec![
            Setting::slider("speed", "Speed", 1.0, 0.5, 3.0, 0.1).section("Movement"),
            Setting::toggle("trail", "Trail", true),
            Setting::choice("mode", "Mode", &["Soft", "Hard"], "Soft"),
            Setting::key("hotkey", "Hotkey", 0x58),
        ]
    }

    fn temp(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("jibbers-sdk-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir.join(name)
    }

    #[test]
    fn enabled_toggle_is_added_first() {
        let s = Store::new(sample(), Some("My mod"), None).unwrap();
        assert_eq!(s.settings()[0].key, "enabled");
        assert_eq!(s.settings()[0].label, "My mod");
        assert_eq!(s.get("enabled"), Some(json!(true)));
        // a plugin's own `enabled` wins
        let own = Store::new(vec![Setting::toggle("enabled", "Mine", false)], Some("x"), None).unwrap();
        assert_eq!(own.settings().len(), 1);
        assert_eq!(own.get("enabled"), Some(json!(false)));
        // and the toggle can be left out entirely
        assert!(Store::new(sample(), None, None).unwrap().get("enabled").is_none());
    }

    #[test]
    fn values_are_checked_against_the_schema() {
        let s = Store::new(sample(), Some("m"), None).unwrap();
        assert_eq!(s.set("speed", &json!(99.0)).unwrap(), json!(3.0), "clamped to max");
        assert_eq!(s.set("speed", &json!(0.0)).unwrap(), json!(0.5), "clamped to min");
        assert!(s.set("speed", &json!("fast")).is_err());
        assert!(s.set("trail", &json!(1)).is_err());
        assert!(s.set("mode", &json!("Medium")).is_err());
        assert_eq!(s.set("mode", &json!("Hard")).unwrap(), json!("Hard"));
        assert!(s.set("hotkey", &json!(300)).is_err());
        assert!(s.set("nope", &json!(1)).is_err());
        assert_eq!(s.get("mode"), Some(json!("Hard")));
    }

    #[test]
    fn schema_entries_have_what_the_menu_needs() {
        let s = Store::new(sample(), Some("m"), None).unwrap();
        for e in s.schema().as_array().unwrap() {
            for k in ["key", "label", "type", "default", "section", "tooltip"] {
                assert!(e.get(k).is_some(), "{} lacks {k}", e["key"]);
            }
        }
        let speed = &s.schema()[1];
        assert_eq!((&speed["type"], &speed["min"], &speed["max"], &speed["step"]), (&json!("slider"), &json!(0.5), &json!(3.0), &json!(0.1)));
        assert_eq!(speed["section"], json!("Movement"));
        assert_eq!(s.schema()[3]["options"], json!(["Soft", "Hard"]));
    }

    #[test]
    fn values_persist_and_bad_files_fall_back_to_defaults() {
        let path = temp("persist.json");
        let _ = std::fs::remove_file(&path);
        let s = Store::new(sample(), Some("m"), Some(path.clone())).unwrap();
        s.set("speed", &json!(2.0)).unwrap();
        s.set("trail", &json!(false)).unwrap();
        let again = Store::new(sample(), Some("m"), Some(path.clone())).unwrap();
        assert_eq!(again.get("speed"), Some(json!(2.0)));
        assert_eq!(again.get("trail"), Some(json!(false)));

        std::fs::write(&path, br#"{"speed": "oops", "mode": "Nope", "extra": 1, "hotkey": 120}"#).unwrap();
        let damaged = Store::new(sample(), Some("m"), Some(path.clone())).unwrap();
        assert_eq!(damaged.get("speed"), Some(json!(1.0)));
        assert_eq!(damaged.get("mode"), Some(json!("Soft")));
        assert_eq!(damaged.get("hotkey"), Some(json!(120)));
        assert_eq!(damaged.get("extra"), None);

        std::fs::write(&path, b"not json").unwrap();
        assert_eq!(Store::new(sample(), Some("m"), Some(path.clone())).unwrap().get("speed"), Some(json!(1.0)));
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn bad_declarations_are_rejected() {
        assert!(Store::new(vec![Setting::toggle("a", "A", true), Setting::toggle("a", "B", true)], None, None).is_err());
        assert!(Store::new(vec![Setting::slider("s", "S", 5.0, 3.0, 1.0, 0.1)], None, None).is_err());
        assert!(Store::new(vec![Setting::slider("s", "S", 1.0, 0.0, 2.0, 0.0)], None, None).is_err());
        assert!(Store::new(vec![Setting::choice("c", "C", &[], "x")], None, None).is_err());
        assert!(Store::new(vec![Setting::choice("c", "C", &["a"], "b")], None, None).is_err());
        assert!(Store::new(vec![Setting::toggle("", "C", true)], None, None).is_err());
        assert!(Store::new(vec![Setting::key("k", "K", 999)], None, None).is_err());
    }
}
