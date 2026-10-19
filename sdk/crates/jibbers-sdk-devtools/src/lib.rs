//! Developer tools plugin (`JibbersMods\jibbers_devtools.dll`): live probes by symbol name, for
//! writing and checking symbol rules and for debugging mods. Not for players: it can call game
//! methods. Commands (from another plugin, or through the developer loader on 127.0.0.1:47820):
//!
//! | Command | Arguments | Reply |
//! | --- | --- | --- |
//! | `dev.symbols` | | `symbols::report()` |
//! | `dev.class` | `symbol` | the class's metadata: real names, field types and offsets, methods |
//! | `dev.objects` | `symbol`, optional `depth` (0) | live instances, described |
//! | `dev.inspect` | `symbol`, optional `index` (0), `depth` (1) | one live instance, described |
//! | `dev.get` | `path`, optional `index`, `depth` (1) | a member field's value: of the index-th live instance, or the static value |
//! | `dev.call` | `path`, optional `index`, `args`, `depth` (1) | calls a member method on the index-th live instance (or statically) |
//! | `dev.images` | | loaded assemblies |
//! | `dev.watch` | `symbol` (an `event.*` id) | subscribes; `dev.watched` then shows counts and the last event |
//! | `dev.watched` | | every watched event: `count`, `last` (described) |
//! | `dev.listeners` | `symbol` | how many listeners the game's bus has for that event |
//! | `dev.raise` | `symbol` | sends an empty (all-zero) event to its listeners: for debug events |
//! | `dev.vars` | `kind` (`bool`, `float`, `int`, `string`) | every name and value in that settings table |
//! | `dev.var.set` | `name`, `value` | writes a setting (type from the JSON value) and notifies the game |
//! | `dev.var.watch` | `kind`, `name` | logs every change of that setting |
//! | `dev.selftest` | | checks the native-delegate argument passing for event structs of several sizes |
//!
//! `args` are JSON values matched to the parameter types: numbers for `System.Int32`/`Int64`/
//! `Single`/`Double`, booleans, strings, and `null` for any reference type. Other types can't be
//! passed from JSON.

use jibbers_mod_sdk::plugin::{Info, Plugin};
use jibbers_mod_sdk::{declare_plugin, events, host, il2cpp, inspect, json, log, symbols, vars, Setting, Value};
use std::collections::BTreeMap;
use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::Mutex;

/// Watched events: id -> (count, last event described).
static WATCHED: Mutex<BTreeMap<&'static str, (u64, Value)>> = Mutex::new(BTreeMap::new());

type R<T> = Result<T, String>;

fn str_arg<'a>(req: &'a Value, k: &str) -> R<&'a str> {
    req.get(k).and_then(Value::as_str).ok_or_else(|| format!("missing \"{k}\""))
}

fn num_arg(req: &Value, k: &str, default: u64) -> u64 {
    req.get(k).and_then(Value::as_u64).unwrap_or(default)
}

/// The `index`-th live instance of the symbol owning `path`'s class, or null without `index` when
/// the member is static.
unsafe fn target(req: &Value, id: &str, member_static: bool) -> R<*mut il2cpp::Object> {
    if member_static && req.get("index").is_none() {
        return Ok(null_mut());
    }
    let objs = symbols::live(id)?;
    let i = num_arg(req, "index", 0) as usize;
    objs.get(i).copied().ok_or_else(|| format!("{id}: {} live instances, no #{i}", objs.len()))
}

/// One argument slot for `invoke`. Values are boxed in `store`, which must outlive the call.
enum Arg {
    I32(i32),
    I64(i64),
    F32(f32),
    F64(f64),
    Bool(bool),
    Ref(*mut il2cpp::Object),
}

fn parse_arg(ty: &str, v: &Value) -> R<Arg> {
    let bad = || format!("cannot pass {v} as {ty}");
    Ok(match ty {
        "System.Int32" => Arg::I32(v.as_i64().ok_or_else(bad)? as i32),
        "System.Int64" => Arg::I64(v.as_i64().ok_or_else(bad)?),
        "System.Single" => Arg::F32(v.as_f64().ok_or_else(bad)? as f32),
        "System.Double" => Arg::F64(v.as_f64().ok_or_else(bad)?),
        "System.Boolean" => Arg::Bool(v.as_bool().ok_or_else(bad)?),
        "System.String" => match v {
            Value::Null => Arg::Ref(null_mut()),
            Value::String(s) => Arg::Ref(unsafe { il2cpp::string(s) }),
            _ => return Err(bad()),
        },
        _ if v.is_null() => Arg::Ref(null_mut()),
        _ => return Err(bad()),
    })
}

unsafe fn class_cmd(req: &Value) -> R<Value> {
    let info = symbols::info(str_arg(req, "symbol")?)?;
    serde_json::to_value(info).map_err(|e| e.to_string())
}

unsafe fn objects(req: &Value) -> R<Value> {
    let id = str_arg(req, "symbol")?;
    let d = num_arg(req, "depth", 0) as u32;
    let list: Vec<Value> = symbols::live(id)?.into_iter().map(|o| inspect::describe(o, d)).collect();
    Ok(json!({"count": list.len(), "objects": list}))
}

unsafe fn inspect_cmd(req: &Value) -> R<Value> {
    let id = str_arg(req, "symbol")?;
    let o = target(req, id, false)?;
    Ok(inspect::describe(o, num_arg(req, "depth", 1) as u32))
}

unsafe fn get(req: &Value) -> R<Value> {
    let path = str_arg(req, "path")?;
    let (id, _) = path.rsplit_once('.').ok_or("path must be <symbol>.<member>")?;
    let f = symbols::field(path)?;
    let is_static = (il2cpp::api().field_get_flags)(f) & 0x10 != 0;
    let d = num_arg(req, "depth", 1) as u32;
    if is_static {
        (il2cpp::api().runtime_class_init)(symbols::class(id)?);
        return Ok(json!({"real": symbols::real_name(path)?, "value": inspect::describe_static(f, d)}));
    }
    let o = target(req, id, false)?;
    Ok(json!({"real": symbols::real_name(path)?, "value": inspect::describe_field(o, f, d)}))
}

unsafe fn call(req: &Value) -> R<Value> {
    let path = str_arg(req, "path")?;
    let (id, member) = path.rsplit_once('.').ok_or("path must be <symbol>.<member>")?;
    let info = symbols::info(id)?;
    let m = symbols::method(path)?;
    let real = symbols::real_name(path)?;
    let mut flags = 0u32;
    let is_static = (il2cpp::api().method_get_flags)(m, &mut flags) & 0x10 != 0;
    let params: Vec<String> = (0..(il2cpp::api().method_get_param_count)(m)).map(|i| il2cpp::type_name((il2cpp::api().method_get_param)(m, i))).collect();
    let given: Vec<Value> = req.get("args").and_then(Value::as_array).cloned().unwrap_or_default();
    if given.len() != params.len() {
        return Err(format!("{path} ({real}) takes {} arguments ({}), got {}", params.len(), params.join(", "), given.len()));
    }
    let mut store: Vec<Arg> = params.iter().zip(&given).map(|(t, v)| parse_arg(t, v)).collect::<R<_>>()?;
    let mut ptrs: Vec<*mut c_void> = store
        .iter_mut()
        .map(|a| match a {
            Arg::I32(v) => v as *mut i32 as *mut c_void,
            Arg::I64(v) => v as *mut i64 as *mut c_void,
            Arg::F32(v) => v as *mut f32 as *mut c_void,
            Arg::F64(v) => v as *mut f64 as *mut c_void,
            Arg::Bool(v) => v as *mut bool as *mut c_void,
            Arg::Ref(o) => *o as *mut c_void,
        })
        .collect();
    let this = target(req, id, is_static)?;
    if this.is_null() && !is_static {
        return Err(format!("{path} is an instance method: give an index"));
    }
    let r = il2cpp::invoke(m, this, &mut ptrs).map_err(|e| format!("{path} ({real}): {e}"))?;
    let _ = (info, member);
    Ok(json!({"real": real, "value": inspect::describe(r, num_arg(req, "depth", 1) as u32)}))
}

fn on_watched(ev: &events::Event) -> Result<(), String> {
    let desc = unsafe { ev.describe(2) };
    let mut w = WATCHED.lock().map_err(|_| "poisoned")?;
    let e = w.entry(ev.id).or_insert((0, Value::Null));
    e.0 += 1;
    e.1 = desc;
    Ok(())
}

unsafe fn watch(req: &Value) -> R<Value> {
    let id = str_arg(req, "symbol")?;
    if WATCHED.lock().map_err(|_| "poisoned")?.contains_key(id) {
        return Ok(json!({"already": true}));
    }
    // Subscriptions keep their id for the plugin's lifetime.
    let id: &'static str = Box::leak(id.to_string().into_boxed_str());
    events::subscribe(id, on_watched)?;
    WATCHED.lock().map_err(|_| "poisoned")?.insert(id, (0, Value::Null));
    Ok(json!({"watching": id, "listeners": events::listener_count(id)?}))
}

fn watched() -> R<Value> {
    let w = WATCHED.lock().map_err(|_| "poisoned")?;
    Ok(Value::Object(w.iter().map(|(k, (n, last))| (k.to_string(), json!({"count": n, "last": last}))).collect()))
}

unsafe fn listeners(req: &Value) -> R<Value> {
    Ok(json!({"listeners": events::listener_count(str_arg(req, "symbol")?)?}))
}

unsafe fn raise(req: &Value) -> R<Value> {
    Ok(json!({"sent": events::raise(str_arg(req, "symbol")?, &[])?}))
}

fn kind_arg(req: &Value) -> R<vars::Kind> {
    vars::Kind::parse(str_arg(req, "kind")?).ok_or_else(|| "kind is bool, float, int or string".to_string())
}

unsafe fn vars_cmd(req: &Value) -> R<Value> {
    let all = vars::all(kind_arg(req)?)?;
    Ok(json!({"count": all.len(), "vars": all.into_iter().map(|(n, v)| (n, json!(v))).collect::<serde_json::Map<_, _>>()}))
}

unsafe fn var_set(req: &Value) -> R<Value> {
    let name = str_arg(req, "name")?;
    let v = match req.get("value") {
        Some(Value::Bool(b)) => vars::Var::Bool(*b),
        Some(Value::String(s)) => vars::Var::String(s.clone()),
        Some(Value::Number(n)) if req.get("kind").and_then(Value::as_str) == Some("int") => vars::Var::Int(n.as_i64().ok_or("int value")? as i32),
        Some(Value::Number(n)) => vars::Var::Float(n.as_f64().ok_or("number")? as f32),
        _ => return Err("value: bool, number (kind \"int\" for ints) or string".into()),
    };
    Ok(json!({"notified": vars::set(name, v)?}))
}

fn on_var(name: &str, v: &vars::Var) {
    log::info(format!("devtools: var {name} = {}", json!(v)));
}

unsafe fn var_watch(req: &Value) -> R<Value> {
    vars::watch(kind_arg(req)?, str_arg(req, "name")?, on_var)?;
    Ok(json!({"watching": str_arg(req, "name")?}))
}

static RECEIVED: Mutex<Vec<u8>> = Mutex::new(Vec::new());
static EXPECT_SIZE: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);

unsafe extern "C" fn record(_ctx: usize, arg: usize, _m: *const c_void) {
    let size = EXPECT_SIZE.load(std::sync::atomic::Ordering::SeqCst);
    let bytes = if jibbers_mod_sdk::delegates::struct_in_register(size) {
        arg.to_ne_bytes()[..size].to_vec()
    } else {
        std::slice::from_raw_parts(arg as *const u8, size).to_vec()
    };
    *RECEIVED.lock().unwrap() = bytes;
}

/// Builds an Action<T> per event struct, invokes it through IL2CPP with a byte pattern and checks
/// that the native side received exactly those bytes.
unsafe fn selftest(_req: &Value) -> R<Value> {
    use jibbers_mod_sdk::delegates;
    let mut out = serde_json::Map::new();
    for id in ["event.show_mouse_cursor", "event.time_changed", "event.slow_mo_debug", "event.session_marker_set", "event.ambient_color_changed", "event.trick_performed"] {
        let r = (|| -> R<Value> {
            let k = symbols::class(id)?;
            let size = delegates::value_size(k);
            let d = delegates::native(delegates::action_of(k)?, record as *const c_void, 0)?;
            // Pointer-sized fields get null so the runtime never sees a bogus reference.
            let info = symbols::info(id)?;
            let mut buf: Vec<u8> = (0..size.max(8)).map(|i| (i as u8).wrapping_mul(37).wrapping_add(11)).collect();
            for f in info.fields.iter().filter(|f| !f.is_static) {
                let is_ref = !f.ty.starts_with("System.") || f.ty == "System.String";
                if is_ref && f.ty != "UnityEngine.Color" {
                    let off = f.offset - 16;
                    buf[off..off + 8].fill(0);
                }
            }
            EXPECT_SIZE.store(size, std::sync::atomic::Ordering::SeqCst);
            RECEIVED.lock().unwrap().clear();
            delegates::invoke(d, &mut [buf.as_mut_ptr() as *mut c_void])?;
            let got = RECEIVED.lock().unwrap().clone();
            Ok(json!({"size": size, "ok": got == buf[..size], "sent": hex(&buf[..size]), "got": hex(&got)}))
        })();
        out.insert(id.to_string(), r.unwrap_or_else(|e| json!({"error": e})));
    }
    Ok(Value::Object(out))
}

fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

/// Runs a probe on the main thread.
fn main_thread(req: &Value, f: unsafe fn(&Value) -> R<Value>) -> R<Value> {
    let req = req.clone();
    host::pump::run(move || unsafe { f(&req) })?
}

pub struct DevTools;

impl Plugin for DevTools {
    const INFO: Info = Info {
        id: "devtools",
        name: "Developer Tools",
        version: env!("CARGO_PKG_VERSION"),
        author: "Jaxon",
        description: "Live probes by symbol name, for mod developers. Not for players.",
    };
    const ENABLED_TOGGLE: bool = false;

    fn settings() -> Vec<Setting> {
        vec![]
    }

    fn command(cmd: &str, req: &Value) -> Option<Result<Value, String>> {
        Some(match cmd {
            "dev.symbols" => host::pump::run(|| unsafe { symbols::report() }).and_then(|r| r),
            "dev.images" => host::pump::run(|| unsafe { symbols::images() }).map(|v| json!({"images": v})),
            "dev.class" => main_thread(req, class_cmd),
            "dev.objects" => main_thread(req, objects),
            "dev.inspect" => main_thread(req, inspect_cmd),
            "dev.get" => main_thread(req, get),
            "dev.call" => main_thread(req, call),
            "dev.watch" => main_thread(req, watch),
            "dev.watched" => watched(),
            "dev.listeners" => main_thread(req, listeners),
            "dev.raise" => main_thread(req, raise),
            "dev.vars" => main_thread(req, vars_cmd),
            "dev.var.set" => main_thread(req, var_set),
            "dev.var.watch" => main_thread(req, var_watch),
            "dev.selftest" => main_thread(req, selftest),
            _ => return None,
        })
    }
}

declare_plugin!(DevTools);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arguments_follow_parameter_types() {
        assert!(matches!(parse_arg("System.Int32", &json!(3)), Ok(Arg::I32(3))));
        assert!(matches!(parse_arg("System.Boolean", &json!(true)), Ok(Arg::Bool(true))));
        assert!(matches!(parse_arg("System.Single", &json!(0.5)), Ok(Arg::F32(v)) if v == 0.5));
        assert!(matches!(parse_arg("UnityEngine.Transform", &json!(null)), Ok(Arg::Ref(p)) if p.is_null()));
        assert!(parse_arg("System.Int32", &json!("x")).is_err());
        assert!(parse_arg("UnityEngine.Vector3", &json!([1, 2, 3])).is_err());
    }

    #[test]
    fn declares_a_valid_plugin() {
        let rt = jibbers_mod_sdk::plugin::Runtime::new::<DevTools>(None).unwrap();
        assert_eq!(rt.handle(&json!({"cmd": "plugin.info"})).unwrap()["id"], json!("devtools"));
    }
}
