//! `/atmo` in the game's chat: the atmosphere configured like the game's own `/time` and `/clouds`.
//!
//! The game keeps its chat commands in a static `Dictionary<string, Command>` (Assembly-CSharp; class
//! and field names are obfuscated, so both are found by type). A `Command` holds its usage text, the
//! minimum argument count, a permission key, a hidden flag and a handler `Action<int, string[]>`
//! (sender id, arguments). We add an "atmo" entry whose handler is a delegate built around a native
//! function: IL2CPP invokes a delegate through its `invoke_impl` pointer, passing `method_code`,
//! the arguments and `method`, so pointing those at our function makes the game call straight into
//! the core. After a core reload the existing entry is re-pointed at the new code.

use crate::il2cpp::{self, api, cstr, type_name, Class, Field, Object};
use crate::unity::{self, R};
use crate::{atmosphere, log};
use serde_json::{json, Value};
use std::ffi::c_void;
use std::ptr::null_mut;

const NAME: &str = "atmo";
const USAGE: &str = "/atmo [on|off|preset <name>|next|fog <0-1>|snow <0-1>|reload]";

/// The static command table and the `Command` class (its value type).
unsafe fn table() -> R<(*mut Object, *mut Class)> {
    let a = api();
    let img = il2cpp::image("Assembly-CSharp.dll").ok_or("Assembly-CSharp not loaded")?;
    for i in 0..(a.image_get_class_count)(img) {
        let k = (a.image_get_class)(img, i) as *mut Class;
        let mut it: *mut c_void = null_mut();
        loop {
            let f = (a.class_get_fields)(k, &mut it);
            if f.is_null() {
                break;
            }
            let t = type_name((a.field_get_type)(f));
            if (a.field_get_flags)(f) & 0x10 == 0 || !t.starts_with("System.Collections.Generic.Dictionary<System.String,") || !t.ends_with(".Command>") {
                continue;
            }
            (a.runtime_class_init)(k);
            let mut dict: *mut Object = null_mut();
            (a.field_static_get_value)(f, &mut dict as *mut _ as _);
            if dict.is_null() {
                return Err("chat command table not created yet".into());
            }
            // value type of the dictionary = the Command class
            let entries = unity::field_obj(dict, "_entries")?;
            let ek = (a.class_get_element_class)((a.object_get_class)(entries));
            let vf = il2cpp::field(ek, "value").ok_or("Entry.value")?;
            return Ok((dict, (a.class_from_type)((a.field_get_type)(vf))));
        }
    }
    Err("chat command table not found (game update?)".into())
}

/// Fields of `Command` by role, found by type and order (their names are obfuscated).
struct CommandFields {
    usage: *mut Field,
    min_args: *mut Field,
    permission: *mut Field,
    hidden: *mut Field,
    handler: *mut Field,
}

unsafe fn command_fields(k: *mut Class) -> R<CommandFields> {
    let a = api();
    let (mut strings, mut ints, mut bools, mut handler) = (vec![], vec![], vec![], None);
    let mut it: *mut c_void = null_mut();
    loop {
        let f = (a.class_get_fields)(k, &mut it);
        if f.is_null() {
            break;
        }
        match type_name((a.field_get_type)(f)).as_str() {
            "System.String" => strings.push(f),
            "System.Int32" => ints.push(f),
            "System.Boolean" => bools.push(f),
            "System.Action<System.Int32,System.String[]>" => handler = Some(f),
            _ => {}
        }
    }
    match (strings.as_slice(), ints.first(), bools.first(), handler) {
        ([usage, permission, ..], Some(&min_args), Some(&hidden), Some(handler)) => {
            Ok(CommandFields { usage: *usage, min_args, permission: *permission, hidden, handler })
        }
        _ => Err("chat Command layout changed (game update?)".into()),
    }
}

unsafe fn set_ptr(o: *mut Object, k: *mut Class, name: &str, v: usize) -> R<()> {
    let f = il2cpp::field(k, name).ok_or_else(|| format!("delegate field {name} missing"))?;
    let mut v = v;
    (api().field_set_value)(o, f, &mut v as *mut usize as _);
    Ok(())
}

/// Points a handler delegate at `on_atmo` (a new delegate, or the one left by an earlier core).
unsafe fn aim(d: *mut Object, dk: *mut Class) -> R<()> {
    let invoke = il2cpp::method(dk, "Invoke", 2).ok_or("Action.Invoke missing")?;
    let f = on_atmo as *const () as usize;
    set_ptr(d, dk, "method_ptr", f)?;
    set_ptr(d, dk, "invoke_impl", f)?;
    set_ptr(d, dk, "method", invoke as usize)?;
    set_ptr(d, dk, "method_code", 0)?;
    Ok(())
}

/// Adds `/atmo` to the game's chat commands, or re-aims an existing entry at this core.
pub unsafe fn register() -> R<()> {
    let a = api();
    let (dict, ck) = table()?;
    let cf = command_fields(ck)?;
    let dk = (a.class_from_type)((a.field_get_type)(cf.handler));
    let key = il2cpp::string(NAME) as *mut c_void;
    let has = unity::callv(dict, "ContainsKey", &mut [key]).map(|o| il2cpp::unbox::<bool>(o)).unwrap_or(false);
    if has {
        let cmd = unity::callv(dict, "get_Item", &mut [key])?;
        let mut d: *mut Object = null_mut();
        (a.field_get_value)(cmd, cf.handler, &mut d as *mut _ as _);
        if !d.is_null() {
            aim(d, dk)?;
            log::info("chat /atmo: re-attached");
            return Ok(());
        }
    }
    let cmd = (a.object_new)(ck);
    unity::callv(cmd, ".ctor", &mut []).ok();
    let d = (a.object_new)(dk);
    aim(d, dk)?;
    (a.field_set_value)(cmd, cf.usage, il2cpp::string(USAGE) as _);
    let mut zero = 0i32;
    (a.field_set_value)(cmd, cf.min_args, &mut zero as *mut i32 as _);
    (a.field_set_value)(cmd, cf.permission, null_mut());
    let mut no = false;
    (a.field_set_value)(cmd, cf.hidden, &mut no as *mut bool as _);
    (a.field_set_value)(cmd, cf.handler, d as _);
    unity::callv(dict, "set_Item", &mut [key, cmd as *mut c_void])?;
    log::info("chat /atmo: registered");
    Ok(())
}

/// The game calls this (main thread) when someone runs `/atmo ...`.
unsafe extern "C" fn on_atmo(_target: *mut c_void, sender: i32, args: *mut Object, _method: *const c_void) {
    let _ = std::panic::catch_unwind(|| {
        let words: Vec<String> = unity::array_items(args).into_iter().map(|s| il2cpp::read_string(s)).collect();
        let reply = run(&words);
        log::info(format!("chat /atmo {words:?} (sender {sender}): {reply}"));
        say(&reply);
    });
}

fn num(w: &[String], i: usize) -> Option<f64> {
    w.get(i).and_then(|s| s.parse::<f64>().ok()).map(|v| v.clamp(0.0, 1.0))
}

/// Runs the words after `/atmo` and returns the line to show in chat.
fn run(words: &[String]) -> String {
    // some builds pass the command name as the first argument
    let w: Vec<String> = words.iter().skip_while(|s| s.trim_start_matches('/').eq_ignore_ascii_case(NAME)).map(|s| s.to_lowercase()).collect();
    let req = match w.first().map(String::as_str) {
        None | Some("status") => json!({}),
        Some("on") => json!({"enabled": true, "save": true}),
        Some("off") => json!({"enabled": false, "save": true}),
        Some("reload") => json!({"reload": true}),
        Some("next") => json!({"nextPreset": true}),
        Some("preset") if w.len() > 1 => {
            let want = w[1..].join(" ");
            json!({"preset": preset_name(&want).unwrap_or(want)})
        }
        Some("fog") => match num(&w, 1) {
            // 0 = clear air, 1 = a whiteout; the sky is covered once there is any real fog
            Some(v) => json!({"hazeDensity": 0.012 * v, "fogDensity": 0.008 * v, "skyHaze": if v > 0.15 { 1.0 } else { v * 5.0 }, "save": true}),
            None => return "usage: /atmo fog <0-1>".into(),
        },
        Some("snow") => match num(&w, 1) {
            Some(v) => json!({"snow": v, "save": true}),
            None => return "usage: /atmo snow <0-1>".into(),
        },
        _ => return format!("usage: {USAGE}"),
    };
    match atmosphere::command(&req) {
        Ok(r) => {
            let s = &r["settings"];
            format!(
                "atmosphere {} | preset {} | fog {:.2} | snow {:.2}",
                if s["enabled"].as_bool().unwrap_or(false) { "on" } else { "off" },
                r["preset"].as_str().unwrap_or("?"),
                s["hazeDensity"].as_f64().unwrap_or(0.0) / 0.012,
                s["snow"].as_f64().unwrap_or(0.0),
            )
        }
        Err(e) => format!("atmo: {e}"),
    }
}

/// A preset by case-insensitive prefix ("foggy" -> "Foggy day").
fn preset_name(want: &str) -> Option<String> {
    let r = atmosphere::command(&json!({})).ok()?;
    let names: Vec<String> = r["settings"]["presets"].as_object()?.keys().cloned().collect();
    names.into_iter().find(|n| n.to_lowercase().starts_with(want))
}

/// Shows a line in the local chat. The game's add-message method is found by signature
/// (ChatKind, int, string, string, string); `PRINT` picks which candidate, see `test`.
fn say(text: &str) {
    unsafe {
        if let Err(e) = print_local(text) {
            log::info(format!("chat /atmo: can't print ({e}): {text}"));
        }
    }
}

static PRINT: std::sync::Mutex<Option<String>> = std::sync::Mutex::new(None);

unsafe fn print_local(text: &str) -> R<()> {
    let name = PRINT.lock().unwrap().clone().ok_or("no chat print method chosen yet")?;
    call_print(&name, 7, text)
}

/// Calls the static method `name` on the command table's class with (ChatKind kind, 0, "Atmosphere", text, null).
unsafe fn call_print(name: &str, kind: i32, text: &str) -> R<()> {
    let owner = owner_class()?;
    let m = il2cpp::method_sig(owner, name, &["ChatKind", "System.Int32", "System.String", "System.String", "System.String"])
        .ok_or_else(|| format!("{name}(ChatKind, int, string, string, string) not found"))?;
    let mut kind = kind;
    let mut zero = 0i32;
    il2cpp::invoke(m, null_mut(), &mut [&mut kind as *mut i32 as _, &mut zero as *mut i32 as _, il2cpp::string("Atmosphere") as _, il2cpp::string(text) as _, null_mut()])?;
    Ok(())
}

/// The class that owns the command table.
unsafe fn owner_class() -> R<*mut Class> {
    let a = api();
    let img = il2cpp::image("Assembly-CSharp.dll").ok_or("Assembly-CSharp not loaded")?;
    for i in 0..(a.image_get_class_count)(img) {
        let k = (a.image_get_class)(img, i) as *mut Class;
        let mut it: *mut c_void = null_mut();
        loop {
            let f = (a.class_get_fields)(k, &mut it);
            if f.is_null() {
                break;
            }
            let t = type_name((a.field_get_type)(f));
            if (a.field_get_flags)(f) & 0x10 != 0 && t.starts_with("System.Collections.Generic.Dictionary<System.String,") && t.ends_with(".Command>") {
                return Ok(k);
            }
        }
    }
    Err("command table owner not found".into())
}

/// `{"cmd":"chatcmd","print":"<method>","kind":7,"text":"..."}` tries one print candidate (and keeps
/// it for /atmo replies); `{"cmd":"chatcmd","register":true}` registers /atmo;
/// `{"cmd":"chatcmd","candidates":true}` lists methods with the print signature.
pub fn test(req: &Value) -> R<Value> {
    unsafe {
        if req.get("candidates").is_some() {
            let a = api();
            let owner = owner_class()?;
            let mut out = vec![];
            let mut it: *mut c_void = null_mut();
            loop {
                let m = (a.class_get_methods)(owner, &mut it);
                if m.is_null() {
                    break;
                }
                let n = (a.method_get_param_count)(m);
                let sig: Vec<String> = (0..n).map(|i| type_name((a.method_get_param)(m, i))).collect();
                if sig.first().map(|s| s == "ChatKind").unwrap_or(false) {
                    out.push(format!("{}({})", cstr((a.method_get_name)(m)), sig.join(", ")));
                }
            }
            return Ok(json!({"ok": true, "owner": cstr((a.class_get_name)(owner)), "methods": out}));
        }
        if req.get("register").is_some() {
            register()?;
            return Ok(json!({"ok": true}));
        }
        if let Some(name) = req.get("print").and_then(Value::as_str) {
            let kind = req.get("kind").and_then(Value::as_i64).unwrap_or(7) as i32;
            call_print(name, kind, req.get("text").and_then(Value::as_str).unwrap_or("atmosphere test"))?;
            *PRINT.lock().unwrap() = Some(name.to_string());
            return Ok(json!({"ok": true}));
        }
        if let Some(words) = req.get("run").and_then(Value::as_array) {
            let w: Vec<String> = words.iter().filter_map(|v| v.as_str().map(String::from)).collect();
            return Ok(json!({"ok": true, "reply": run(&w)}));
        }
        Err("chatcmd: nothing to do".into())
    }
}
