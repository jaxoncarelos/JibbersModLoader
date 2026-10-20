//! The game's named settings store (`game.vars`): every option the game's menus bind by name
//! (`globalName` on a toggle, slider or choice) lives in one of four tables (bools, floats, ints,
//! strings), each with change listeners. Main thread only.
//!
//! ```ignore
//! let names = unsafe { vars::names(vars::Kind::Bool) }?;
//! let v = unsafe { vars::get(vars::Kind::Float, "fov") }?;
//! unsafe { vars::watch(vars::Kind::Bool, "photoModeHud", on_change) }?;
//! ```
//!
//! [`set`] writes the value and notifies the game's listeners the way the game's own menus do. It
//! does not save it: the game writes its settings file when the player changes an option in its
//! menus. Watches are removed by the SDK after `Plugin::unload`.

use crate::delegates;
use crate::il2cpp::{self, api, Object};
use crate::symbols;
use crate::unity::{self, R};
use std::ffi::c_void;
use std::sync::Mutex;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Kind {
    Bool,
    Float,
    Int,
    String,
}

#[derive(Clone, Debug, PartialEq, serde::Serialize)]
#[serde(untagged)]
pub enum Var {
    Bool(bool),
    Float(f32),
    Int(i32),
    String(String),
}

impl Kind {
    fn table(self) -> &'static str {
        match self {
            Kind::Bool => "game.vars.bools",
            Kind::Float => "game.vars.floats",
            Kind::Int => "game.vars.ints",
            Kind::String => "game.vars.strings",
        }
    }

    fn listeners(self) -> &'static str {
        match self {
            Kind::Bool => "game.vars.bool_listeners",
            Kind::Float => "game.vars.float_listeners",
            Kind::Int => "game.vars.int_listeners",
            Kind::String => "game.vars.string_listeners",
        }
    }

    pub fn parse(s: &str) -> Option<Kind> {
        Some(match s {
            "bool" => Kind::Bool,
            "float" => Kind::Float,
            "int" => Kind::Int,
            "string" => Kind::String,
            _ => return None,
        })
    }
}

unsafe fn table(kind: Kind) -> R<*mut Object> {
    let d = symbols::get_static_obj(kind.table())?;
    if d.is_null() {
        Err("game settings not loaded yet".into())
    } else {
        Ok(d)
    }
}

unsafe fn contains(dict: *mut Object, key: *mut Object) -> R<bool> {
    let r = unity::callv(dict, "ContainsKey", &mut [key as *mut c_void])?;
    Ok(!r.is_null() && il2cpp::unbox::<bool>(r))
}

/// Every name in a table.
pub unsafe fn names(kind: Kind) -> R<Vec<String>> {
    let dict = table(kind)?;
    let keys = unity::callv(dict, "get_Keys", &mut [])?;
    let n = il2cpp::unbox::<i32>(unity::callv(dict, "get_Count", &mut [])?);
    let arr = (api().array_new)(unity::class("mscorlib.dll", "System", "String")?, n.max(0) as usize);
    let mut zero = 0i32;
    unity::callv(keys, "CopyTo", &mut [arr as *mut c_void, &mut zero as *mut i32 as *mut c_void])?;
    let mut out: Vec<String> = unity::array_items(arr).into_iter().map(|s| il2cpp::read_string(s)).collect();
    out.sort();
    Ok(out)
}

/// A value, or `None` if the table has no such name.
pub unsafe fn get(kind: Kind, name: &str) -> R<Option<Var>> {
    let dict = table(kind)?;
    let key = il2cpp::string(name);
    if !contains(dict, key)? {
        return Ok(None);
    }
    let v = unity::callv(dict, "get_Item", &mut [key as *mut c_void])?;
    Ok(Some(match kind {
        Kind::Bool => Var::Bool(il2cpp::unbox::<bool>(v)),
        Kind::Float => Var::Float(il2cpp::unbox::<f32>(v)),
        Kind::Int => Var::Int(il2cpp::unbox::<i32>(v)),
        Kind::String => Var::String(il2cpp::read_string(v)),
    }))
}

/// Every name and value of a table.
pub unsafe fn all(kind: Kind) -> R<Vec<(String, Var)>> {
    let mut out = vec![];
    for n in names(kind)? {
        if let Some(v) = get(kind, &n)? {
            out.push((n, v));
        }
    }
    Ok(out)
}

/// Pointer to `v` as a runtime_invoke argument (strings become managed strings).
unsafe fn arg(v: &mut Var, s: &mut *mut Object) -> *mut c_void {
    match v {
        Var::Bool(b) => b as *mut bool as *mut c_void,
        Var::Float(f) => f as *mut f32 as *mut c_void,
        Var::Int(i) => i as *mut i32 as *mut c_void,
        Var::String(t) => {
            *s = il2cpp::string(t);
            *s as *mut c_void
        }
    }
}

fn kind_of(v: &Var) -> Kind {
    match v {
        Var::Bool(_) => Kind::Bool,
        Var::Float(_) => Kind::Float,
        Var::Int(_) => Kind::Int,
        Var::String(_) => Kind::String,
    }
}

/// The listener list for `name`, created if `create` and missing.
unsafe fn listener_list(kind: Kind, name: &str, create: bool) -> R<Option<*mut Object>> {
    let dict = symbols::get_static_obj(kind.listeners())?;
    if dict.is_null() {
        return Err("game settings not loaded yet".into());
    }
    let key = il2cpp::string(name);
    if contains(dict, key)? {
        return unity::callv(dict, "get_Item", &mut [key as *mut c_void]).map(Some);
    }
    if !create {
        return Ok(None);
    }
    let value_k = *delegates::generic_args((api().object_get_class)(dict))?.get(1).ok_or("listener table has no value type")?;
    let list = (api().object_new)(value_k);
    unity::callv(list, ".ctor", &mut [])?;
    unity::callv(dict, "set_Item", &mut [key as *mut c_void, list as *mut c_void])?;
    Ok(Some(list))
}

/// Writes a value (the table is chosen by the value's type) and notifies the game's listeners
/// for that name. Returns how many were notified. Not saved to the game's settings file.
pub unsafe fn set(name: &str, mut v: Var) -> R<usize> {
    let kind = kind_of(&v);
    let dict = table(kind)?;
    let key = il2cpp::string(name);
    let mut s = std::ptr::null_mut();
    let p = arg(&mut v, &mut s);
    unity::callv(dict, "set_Item", &mut [key as *mut c_void, p])?;
    let Some(list) = listener_list(kind, name, false)? else { return Ok(0) };
    let n = il2cpp::unbox::<i32>(unity::callv(list, "get_Count", &mut [])?);
    let mut sent = 0;
    for i in 0..n {
        let mut idx = i;
        let item = unity::callv(list, "get_Item", &mut [&mut idx as *mut i32 as *mut c_void])?;
        let pair = il2cpp::unbox::<[*mut Object; 2]>(item);
        delegates::invoke(pair[1], &mut [p])?;
        sent += 1;
    }
    Ok(sent)
}

/// A watch callback: the name and its new value.
pub type Callback = fn(&str, &Var);

struct Watch {
    name: String,
    cb: Callback,
    list: isize,
    delegate: isize,
    live: bool,
}

static WATCHES: Mutex<Vec<Watch>> = Mutex::new(Vec::new());
static OWNER: Mutex<isize> = Mutex::new(0);

fn deliver(ctx: usize, v: Var) {
    let Some((name, cb)) = WATCHES.lock().ok().and_then(|w| w.get(ctx).filter(|w| w.live).map(|w| (w.name.clone(), w.cb))) else { return };
    if std::panic::catch_unwind(|| cb(&name, &v)).is_err() {
        crate::log::info(format!("vars: watch on {name} panicked"));
    }
}

unsafe extern "C" fn on_bool(ctx: usize, v: bool, _m: *const c_void) {
    deliver(ctx, Var::Bool(v));
}
unsafe extern "C" fn on_float(ctx: usize, v: f32, _m: *const c_void) {
    deliver(ctx, Var::Float(v));
}
unsafe extern "C" fn on_int(ctx: usize, v: i32, _m: *const c_void) {
    deliver(ctx, Var::Int(v));
}
unsafe extern "C" fn on_string(ctx: usize, v: *mut Object, _m: *const c_void) {
    deliver(ctx, Var::String(il2cpp::read_string(v)));
}

unsafe fn owner() -> R<*mut Object> {
    let mut h = OWNER.lock().map_err(|_| "vars state poisoned")?;
    if *h == 0 {
        *h = unity::gc_handle(delegates::new_owner()?)?;
    }
    unity::gc_target(*h)
}

/// Calls `cb` whenever the game (or [`set`]) changes `name`. Main thread only.
pub unsafe fn watch(kind: Kind, name: &str, cb: Callback) -> R<()> {
    let list = listener_list(kind, name, true)?.ok_or("no listener list")?;
    // The list's element is (object, Action<T>): its delegate class comes from the tuple's arguments.
    let tuple_k = *delegates::generic_args((api().object_get_class)(list))?.first().ok_or("listener list has no element type")?;
    let action_k = *delegates::generic_args(tuple_k)?.get(1).ok_or("listener tuple has no delegate type")?;
    let f: *const c_void = match kind {
        Kind::Bool => on_bool as *const c_void,
        Kind::Float => on_float as *const c_void,
        Kind::Int => on_int as *const c_void,
        Kind::String => on_string as *const c_void,
    };
    let ctx = WATCHES.lock().map_err(|_| "vars state poisoned")?.len();
    let d = delegates::native(action_k, f, ctx)?;
    let mut tuple: [*mut Object; 2] = [owner()?, d];
    unity::callv(list, "Add", &mut [tuple.as_mut_ptr() as *mut c_void])?;
    WATCHES.lock().map_err(|_| "vars state poisoned")?.push(Watch { name: name.to_string(), cb, list: unity::gc_handle(list)?, delegate: unity::gc_handle(d)?, live: true });
    Ok(())
}

/// Whether any watch is live.
pub fn any() -> bool {
    WATCHES.lock().map(|w| w.iter().any(|w| w.live)).unwrap_or(false)
}

/// Removes every watch of this plugin. Main thread only. The SDK calls it after `Plugin::unload`.
pub unsafe fn unwatch_all() -> R<()> {
    let taken: Vec<(isize, isize)> = {
        let mut w = WATCHES.lock().map_err(|_| "vars state poisoned")?;
        w.iter_mut().filter_map(|w| std::mem::replace(&mut w.live, false).then(|| (std::mem::take(&mut w.list), std::mem::take(&mut w.delegate)))).collect()
    };
    let mut first = Ok(());
    for (lh, dh) in taken {
        let r = (|| -> R<()> {
            let list = unity::gc_target(lh)?;
            let d = unity::gc_target(dh)?;
            let n = il2cpp::unbox::<i32>(unity::callv(list, "get_Count", &mut [])?);
            for i in (0..n).rev() {
                let mut idx = i;
                let item = unity::callv(list, "get_Item", &mut [&mut idx as *mut i32 as *mut c_void])?;
                if il2cpp::unbox::<[*mut Object; 2]>(item)[1] == d {
                    unity::callv(list, "RemoveAt", &mut [&mut idx as *mut i32 as *mut c_void])?;
                }
            }
            Ok(())
        })();
        let _ = unity::gc_free(lh);
        let _ = unity::gc_free(dh);
        if first.is_ok() {
            first = r;
        }
    }
    first
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kinds_parse_and_map_to_tables() {
        assert_eq!(Kind::parse("float"), Some(Kind::Float));
        assert_eq!(Kind::parse("x"), None);
        assert_eq!(kind_of(&Var::String("a".into())), Kind::String);
        assert_eq!(Kind::Int.table(), "game.vars.ints");
        assert_eq!(serde_json::to_value(Var::Float(0.5)).unwrap(), serde_json::json!(0.5));
    }
}
