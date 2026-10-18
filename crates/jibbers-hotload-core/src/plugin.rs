//! Plugin protocol: what the loader's Mods menu asks of this core (ABI 4). Every plugin answers
//! `plugin.info` (identity and settings schema), `plugin.get`, `plugin.set` and `plugin.enable`; the
//! menu builds one control per schema entry. Atmosphere's settings change through `atmosphere::command`,
//! so they take effect and save exactly as `atmo` requests do.
//!
//! Schema keys are the camelCase names of `Settings` fields, except `fog`, which sets three fields at
//! once (see `fog_fields`).

use crate::atmosphere::{self, Settings};
use crate::unity::R;
use serde_json::{json, Map, Value};

const ID: &str = "atmosphere";
const NAME: &str = "Atmosphere";
const AUTHOR: &str = "Jaxon";
const DESCRIPTION: &str = "Fog, snowfall, valley clouds and colour grading.";

/// hazeDensity and fogDensity at fog thickness 1 (the Snowstorm preset uses the same scale).
const FOG_HAZE_FULL: f32 = 0.012;
const FOG_DENSITY_FULL: f32 = 0.008;

/// How a schema entry is edited.
enum Kind {
    Toggle,
    /// Range and step of the slider.
    Slider { min: f64, max: f64, step: f64 },
    /// One of the preset names.
    Choice,
    /// A key code (Windows virtual-key code, 0..=255).
    Key,
}

/// One schema entry. The order is the menu order; sections appear in the order first used.
struct Field {
    key: &'static str,
    label: &'static str,
    section: &'static str,
    tooltip: &'static str,
    kind: Kind,
}

const FIELDS: [Field; 13] = [
    Field { key: "enabled", label: "Atmosphere", section: "General", tooltip: "Turns the whole effect on or off.", kind: Kind::Toggle },
    Field { key: "preset", label: "Preset", section: "General", tooltip: "A named set of fog, snow and sky values.", kind: Kind::Choice },
    Field { key: "hotkeys", label: "Hotkeys", section: "General", tooltip: "Keyboard shortcuts, while the game has focus.", kind: Kind::Toggle },
    Field { key: "toggleKey", label: "Toggle key", section: "General", tooltip: "Key that turns the atmosphere on or off.", kind: Kind::Key },
    Field { key: "presetKey", label: "Preset key", section: "General", tooltip: "Key that goes to the next preset.", kind: Kind::Key },
    Field { key: "fog", label: "Fog thickness", section: "Fog", tooltip: "How thick the haze is, near and far.", kind: Kind::Slider { min: 0.0, max: 1.0, step: 0.05 } },
    Field { key: "fogStart", label: "Fog start", section: "Fog", tooltip: "Distance (m) before any fog, so nearby ground stays crisp.", kind: Kind::Slider { min: 0.0, max: 60.0, step: 1.0 } },
    Field { key: "snow", label: "Snowfall", section: "Snow", tooltip: "Fraction of the snowflakes shown.", kind: Kind::Slider { min: 0.0, max: 1.0, step: 0.05 } },
    Field { key: "snowSize", label: "Flake size", section: "Snow", tooltip: "Radius of a snowflake (m).", kind: Kind::Slider { min: 0.01, max: 0.08, step: 0.005 } },
    Field { key: "speedStreaks", label: "Speed streaks", section: "Snow", tooltip: "How much falling snow streaks with your speed.", kind: Kind::Slider { min: 0.0, max: 2.0, step: 0.1 } },
    Field { key: "cloudBank", label: "Valley clouds", section: "Sky", tooltip: "Low cloud sea in the valleys below the map.", kind: Kind::Slider { min: 0.0, max: 2.0, step: 0.1 } },
    Field { key: "sunShafts", label: "Sun shafts", section: "Sky", tooltip: "Light shafts around the sun.", kind: Kind::Slider { min: 0.0, max: 2.0, step: 0.1 } },
    Field { key: "grading", label: "Colour grading", section: "Colour", tooltip: "Filmic tonemapping, white balance and bloom.", kind: Kind::Toggle },
];

/// Fog slider value to the fields it sets: both densities scale with it, and the sky is fully
/// hazed from 0.15 up.
fn fog_fields(v: f32) -> Map<String, Value> {
    let sky = if v > 0.15 { 1.0 } else { v * 5.0 };
    let mut m = Map::new();
    m.insert("hazeDensity".into(), json!(v * FOG_HAZE_FULL));
    m.insert("fogDensity".into(), json!(v * FOG_DENSITY_FULL));
    m.insert("skyHaze".into(), json!(sky));
    m
}

/// The fog slider value read back from the settings (inverse of `fog_fields`, clamped to 0..1).
fn fog_value(s: &Settings) -> f32 {
    (s.haze_density / FOG_HAZE_FULL).clamp(0.0, 1.0)
}

/// Rounds a float read back from a Settings field (stored as f64 with f32 noise) to 4 decimals.
fn tidy(v: Value) -> Value {
    match v.as_f64() {
        Some(x) if v.is_f64() => json!((x * 1e4).round() / 1e4),
        _ => v,
    }
}

/// Current value of every schema key.
fn values(s: &Settings) -> Value {
    let all = serde_json::to_value(s).unwrap_or(Value::Null);
    let mut out = Map::new();
    for f in &FIELDS {
        let v = if f.key == "fog" { json!(fog_value(s)) } else { all[f.key].clone() };
        out.insert(f.key.into(), tidy(v));
    }
    Value::Object(out)
}

/// The schema entries. Choice options are the presets of `s`; defaults come from `Settings::default()`.
fn schema(s: &Settings) -> Value {
    let defaults = values(&Settings::default());
    let entries = FIELDS.iter().map(|f| {
        let mut e = json!({
            "key": f.key,
            "label": f.label,
            "default": defaults[f.key],
            "section": f.section,
            "tooltip": f.tooltip,
        });
        let kind = match f.kind {
            Kind::Toggle => "toggle",
            Kind::Slider { min, max, step } => {
                e["min"] = json!(min);
                e["max"] = json!(max);
                e["step"] = json!(step);
                "slider"
            }
            Kind::Choice => {
                e["options"] = json!(atmosphere::preset_order(&s.presets));
                "choice"
            }
            Kind::Key => "key",
        };
        e["type"] = json!(kind);
        e
    });
    Value::Array(entries.collect())
}

/// `plugin.info` for the settings `s`.
pub fn info_for(s: &Settings) -> Value {
    json!({
        "ok": true,
        "id": ID,
        "name": NAME,
        "version": env!("CARGO_PKG_VERSION"),
        "author": AUTHOR,
        "description": DESCRIPTION,
        "enabled": s.enabled,
        "settings": schema(s),
    })
}

/// The `Settings` fields (as a `command` request) that setting `key` to `value` changes. Values are
/// checked against the schema: sliders are clamped to their range, choices must be a preset name.
fn request_for(s: &Settings, key: &str, value: &Value) -> R<Map<String, Value>> {
    let f = FIELDS.iter().find(|f| f.key == key).ok_or_else(|| format!("unknown setting {key:?}"))?;
    let mut fields = Map::new();
    match f.kind {
        Kind::Toggle => {
            let on = value.as_bool().ok_or_else(|| format!("{key} takes true or false"))?;
            fields.insert(key.into(), json!(on));
        }
        Kind::Slider { min, max, .. } => {
            let x = value.as_f64().ok_or_else(|| format!("{key} takes a number"))?.clamp(min, max);
            if key == "fog" {
                fields = fog_fields(x as f32);
            } else {
                fields.insert(key.into(), json!(x));
            }
        }
        Kind::Choice => {
            let name = value.as_str().ok_or_else(|| format!("{key} takes a preset name"))?;
            if !atmosphere::preset_order(&s.presets).iter().any(|n| n == name) {
                return Err(format!("no preset named {name:?}"));
            }
            fields.insert(key.into(), json!(name));
        }
        Kind::Key => {
            let k = value.as_u64().filter(|k| *k <= 255).ok_or_else(|| format!("{key} takes a key code (0-255)"))?;
            fields.insert(key.into(), json!(k));
        }
    }
    Ok(fields)
}

/// `plugin.info`.
fn info() -> R<Value> {
    Ok(info_for(&atmosphere::settings()?))
}

/// `plugin.get`.
fn get() -> R<Value> {
    Ok(json!({"ok": true, "values": values(&atmosphere::settings()?)}))
}

/// `plugin.set`: applies and saves the value, then replies with the new values.
fn set(key: &str, value: &Value) -> R<Value> {
    let s = atmosphere::settings()?;
    let mut req = request_for(&s, key, value)?;
    req.insert("save".into(), json!(true));
    let r = atmosphere::command(&Value::Object(req))?;
    let now: Settings = serde_json::from_value(r["settings"].clone()).map_err(|e| e.to_string())?;
    Ok(json!({"ok": true, "values": values(&now)}))
}

/// `plugin.enable`: turns the atmosphere on or off and saves it.
fn enable(on: &Value) -> R<Value> {
    let on = on.as_bool().ok_or("plugin.enable needs \"on\": true or false")?;
    let r = atmosphere::command(&json!({"enabled": on, "save": true}))?;
    Ok(json!({"ok": true, "enabled": r["settings"]["enabled"]}))
}

/// Answers a `plugin.*` request (routed from `dispatch` in both builds).
pub fn handle(req: &Value) -> R<Value> {
    match req.get("cmd").and_then(Value::as_str).unwrap_or("") {
        "plugin.info" => info(),
        "plugin.get" => get(),
        "plugin.set" => {
            let key = req.get("key").and_then(Value::as_str).ok_or("plugin.set needs a key")?;
            let value = req.get("value").ok_or("plugin.set needs a value")?;
            set(key, value)
        }
        "plugin.enable" => enable(req.get("on").unwrap_or(&Value::Null)),
        other => Err(format!("unknown plugin command {other:?}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn near(a: f64, b: f64) -> bool {
        (a - b).abs() < 1e-6
    }

    #[test]
    fn info_shape() {
        let info = info_for(&Settings::default());
        assert_eq!(info["ok"], json!(true));
        assert_eq!(info["id"], json!("atmosphere"));
        assert_eq!(info["name"], json!("Atmosphere"));
        assert_eq!(info["version"], json!(env!("CARGO_PKG_VERSION")));
        assert_eq!(info["enabled"], json!(true));
        assert!(info["author"].as_str().is_some_and(|a| !a.is_empty()));
        assert!(info["description"].as_str().is_some());

        let settings = info["settings"].as_array().expect("settings array");
        let keys: Vec<&str> = settings.iter().map(|e| e["key"].as_str().unwrap()).collect();
        assert_eq!(
            keys,
            ["enabled", "preset", "hotkeys", "toggleKey", "presetKey", "fog", "fogStart", "snow", "snowSize", "speedStreaks", "cloudBank", "sunShafts", "grading"]
        );
        let mut sections: Vec<&str> = settings.iter().map(|e| e["section"].as_str().unwrap()).collect();
        sections.dedup();
        assert_eq!(sections, ["General", "Fog", "Snow", "Sky", "Colour"]);
    }

    #[test]
    fn schema_entries_have_their_fields() {
        let info = info_for(&Settings::default());
        for e in info["settings"].as_array().unwrap() {
            for k in ["key", "label", "type", "default", "section", "tooltip"] {
                assert!(e.get(k).is_some(), "{} lacks {k}", e["key"]);
            }
            match e["type"].as_str().unwrap() {
                "slider" => {
                    assert!(e["min"].is_number() && e["max"].is_number() && e["step"].is_number(), "{}", e["key"]);
                    let d = e["default"].as_f64().unwrap();
                    assert!(d >= e["min"].as_f64().unwrap() && d <= e["max"].as_f64().unwrap(), "{} default out of range", e["key"]);
                }
                "choice" => assert!(e["options"].is_array(), "{}", e["key"]),
                "toggle" | "key" => {
                    assert!(e.get("min").is_none() && e.get("max").is_none() && e.get("options").is_none(), "{}", e["key"]);
                }
                other => panic!("unexpected type {other}"),
            }
        }
        let by_key = |k: &str| info["settings"].as_array().unwrap().iter().find(|e| e["key"] == k).unwrap().clone();
        assert_eq!(by_key("preset")["options"], json!(["Foggy day", "Clear", "Snowstorm"]));
        assert_eq!(by_key("preset")["default"], json!("Foggy day"));
        assert_eq!(by_key("toggleKey")["default"], json!(0x78));
        assert_eq!(by_key("snowSize")["step"], json!(0.005));
    }

    #[test]
    fn values_cover_every_key() {
        let v = values(&Settings::default());
        for f in &FIELDS {
            assert!(v.get(f.key).is_some(), "{} missing", f.key);
        }
        assert!(near(v["fog"].as_f64().unwrap(), 0.3333));
        assert_eq!(v["fogStart"], json!(5.0));
    }

    #[test]
    fn fog_maps_both_ways() {
        // Forward: thickness scales both densities; the sky is fully hazed from 0.15 up.
        let m = fog_fields(0.5);
        assert!(near(m["hazeDensity"].as_f64().unwrap(), 0.006));
        assert!(near(m["fogDensity"].as_f64().unwrap(), 0.004));
        assert!(near(m["skyHaze"].as_f64().unwrap(), 1.0));
        assert!(near(fog_fields(0.1)["skyHaze"].as_f64().unwrap(), 0.5));
        assert!(near(fog_fields(0.15)["skyHaze"].as_f64().unwrap(), 0.75));

        // Back: a slider value survives the round trip through the settings field.
        for v in [0.0f32, 0.05, 0.1, 0.15, 0.5, 1.0] {
            let mut s = Settings::default();
            s.haze_density = fog_fields(v)["hazeDensity"].as_f64().unwrap() as f32;
            assert!((fog_value(&s) - v).abs() < 1e-5, "fog {v} read back as {}", fog_value(&s));
        }
        // Read back is clamped to the slider's range.
        let mut s = Settings::default();
        s.haze_density = 0.024;
        assert_eq!(fog_value(&s), 1.0);
        s.haze_density = -0.01;
        assert_eq!(fog_value(&s), 0.0);
    }

    #[test]
    fn requests_are_checked_against_the_schema() {
        let s = Settings::default();
        let fog = request_for(&s, "fog", &json!(0.5)).unwrap();
        assert!(fog.contains_key("hazeDensity") && fog.contains_key("fogDensity") && fog.contains_key("skyHaze"));
        assert!(!fog.contains_key("fog"));

        let size = request_for(&s, "snowSize", &json!(1.0)).unwrap();
        assert!(near(size["snowSize"].as_f64().unwrap(), 0.08), "clamped to the slider's max");

        assert_eq!(request_for(&s, "preset", &json!("Clear")).unwrap()["preset"], json!("Clear"));
        assert!(request_for(&s, "preset", &json!("Nope")).is_err());
        assert!(request_for(&s, "grading", &json!("yes")).is_err());
        assert_eq!(request_for(&s, "toggleKey", &json!(0x79)).unwrap()["toggleKey"], json!(0x79));
        assert!(request_for(&s, "toggleKey", &json!(300)).is_err());
        assert!(request_for(&s, "nope", &json!(1)).is_err());
    }
}
