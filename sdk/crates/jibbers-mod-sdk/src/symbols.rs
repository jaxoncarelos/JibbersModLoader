//! Stable names for the game's obfuscated classes and members.
//!
//! The built-in catalogue (`symbols.json`, documented in `docs/SYMBOLS.md`) describes game classes
//! by shape ([`crate::shape`]) and names them: `map.importer` is the class, `map.importer.terrains`
//! one of its fields. Look things up by those names instead of the obfuscated ones, which change
//! with every game build:
//!
//! ```ignore
//! use jibbers_mod_sdk::symbols;
//! let terrains = unsafe { symbols::get_obj(importer, "map.importer.terrains") }?;
//! unsafe { symbols::hook("chat.command.run", on_command) }?;
//! ```
//!
//! The first lookup reads the game's metadata and resolves the whole catalogue once; later lookups
//! are map reads. Lookups touch metadata only, so like [`crate::host::pre_hook_method`] they may run
//! in `Plugin::init`; reading or writing objects (`get_obj`, `call`, ...) is main thread only.
//!
//! A symbol that does not resolve on the running build returns `Err` with the reason (not found,
//! ambiguous, which member failed). Log it and run without the feature: never fall back to a
//! hard-coded obfuscated name.
//!
//! Your own symbols: [`register`] a catalogue in the same JSON format before your first lookup, and
//! prefix their ids with your plugin id (`my_mod.thing`).

use crate::abi::PreHookFn;
use crate::il2cpp::{self, api, cstr, type_name, Class, Field, Image, Method, Object};
use crate::shape::{self, Catalogue, ClassInfo, FieldInfo, Member, MemberHit, MethodInfo, Resolution, Snapshot, Symbol};
use crate::unity::R;
use serde_json::{json, Value};
use std::collections::{BTreeSet, HashSet};
use std::ffi::c_void;
use std::path::Path;
use std::ptr::null_mut;
use std::sync::Mutex;

/// The built-in catalogue.
pub const CATALOGUE_JSON: &str = include_str!("../symbols.json");

/// Live metadata: the snapshot plus the IL2CPP handles behind each entry (same indices).
struct Live {
    snap: Snapshot,
    classes: Vec<*mut Class>,
    fields: Vec<Vec<*mut Field>>,
    methods: Vec<Vec<*const Method>>,
    images: BTreeSet<String>,
}

struct State {
    extra: Vec<Symbol>,
    live: Option<Live>,
    cat: Option<Catalogue>,
    res: Option<Resolution>,
}

// IL2CPP metadata handles live as long as the process.
unsafe impl Send for State {}

static STATE: Mutex<State> = Mutex::new(State { extra: vec![], live: None, cat: None, res: None });

fn catalogue(extra: &[Symbol]) -> R<Catalogue> {
    let mut c: Catalogue = serde_json::from_str(CATALOGUE_JSON).map_err(|e| format!("symbols.json: {e}"))?;
    c.symbols.extend(extra.iter().cloned());
    Ok(c)
}

/// Adds symbols (a catalogue in the `symbols.json` format) for this plugin's lookups. Ids must not
/// clash with built-in ones. Call before your first lookup; a later call re-resolves everything.
pub fn register(catalogue_json: &str) -> R<()> {
    let add: Catalogue = serde_json::from_str(catalogue_json).map_err(|e| format!("register: {e}"))?;
    let mut st = STATE.lock().map_err(|_| "symbols state poisoned")?;
    let mut all = catalogue(&st.extra)?;
    all.symbols.extend(add.symbols.iter().cloned());
    let errs = shape::lint(&all);
    if !errs.is_empty() {
        return Err(format!("register: {}", errs.join("; ")));
    }
    st.extra.extend(add.symbols);
    st.cat = None;
    st.res = None;
    Ok(())
}

/// Images every rule of `cat` looks in.
fn images_of(cat: &Catalogue) -> BTreeSet<String> {
    let mut s: BTreeSet<String> = cat.symbols.iter().map(|x| x.class.image.clone().unwrap_or_else(|| shape::GAME_IMAGE.into())).collect();
    s.insert(shape::GAME_IMAGE.into());
    s
}

unsafe fn class_full_name(k: *const Class) -> String {
    if k.is_null() {
        String::new()
    } else {
        type_name((api().class_get_type)(k))
    }
}

/// Reads one class's metadata (declared members only).
unsafe fn read_class(image: &str, k: *mut Class) -> (ClassInfo, Vec<*mut Field>, Vec<*const Method>) {
    let a = api();
    let mut info = ClassInfo {
        image: image.to_string(),
        namespace: cstr((a.class_get_namespace)(k)),
        name: cstr((a.class_get_name)(k)),
        full_name: class_full_name(k),
        parent: class_full_name((a.class_get_parent)(k)),
        declaring: class_full_name((a.class_get_declaring_type)(k)),
        valuetype: (a.class_is_valuetype)(k),
        ..Default::default()
    };
    let mut it: *mut c_void = null_mut();
    loop {
        let i = (a.class_get_interfaces)(k, &mut it);
        if i.is_null() {
            break;
        }
        info.interfaces.push(class_full_name(i));
    }
    let mut fields = vec![];
    let mut it: *mut c_void = null_mut();
    loop {
        let f = (a.class_get_fields)(k, &mut it);
        if f.is_null() {
            break;
        }
        let is_static = (a.field_get_flags)(f) & 0x10 != 0;
        info.fields.push(FieldInfo {
            name: cstr((a.field_get_name)(f)),
            ty: type_name((a.field_get_type)(f)),
            is_static,
            offset: if is_static { 0 } else { (a.field_get_offset)(f) },
        });
        fields.push(f);
    }
    let mut methods = vec![];
    let mut it: *mut c_void = null_mut();
    loop {
        let m = (a.class_get_methods)(k, &mut it);
        if m.is_null() {
            break;
        }
        let mut iflags = 0u32;
        let flags = (a.method_get_flags)(m, &mut iflags);
        info.methods.push(MethodInfo {
            name: cstr((a.method_get_name)(m)),
            params: (0..(a.method_get_param_count)(m)).map(|i| type_name((a.method_get_param)(m, i))).collect(),
            ret: type_name((a.method_get_return_type)(m)),
            is_static: flags & 0x10 != 0,
        });
        methods.push(m);
    }
    (info, fields, methods)
}

unsafe fn read_live(images: &BTreeSet<String>) -> R<Live> {
    let a = api();
    let mut live = Live { snap: Snapshot::default(), classes: vec![], fields: vec![], methods: vec![], images: images.clone() };
    live.snap.game_build = crate::gamebuild::fingerprint_loaded().map(|b| b.describe()).unwrap_or_default();
    for (img, name) in il2cpp::assemblies() {
        if !images.contains(&name) {
            continue;
        }
        for i in 0..(a.image_get_class_count)(img) {
            let k = (a.image_get_class)(img, i) as *mut Class;
            if k.is_null() {
                continue;
            }
            let (info, f, m) = read_class(&name, k);
            live.snap.classes.push(info);
            live.classes.push(k);
            live.fields.push(f);
            live.methods.push(m);
        }
    }
    if live.classes.is_empty() {
        return Err(format!("no classes found in {}", images.iter().cloned().collect::<Vec<_>>().join(", ")));
    }
    Ok(live)
}

/// Runs `f` with the resolved catalogue, resolving it first if needed.
unsafe fn with<T>(f: impl FnOnce(&Live, &Catalogue, &Resolution) -> R<T>) -> R<T> {
    let mut st = STATE.lock().map_err(|_| "symbols state poisoned")?;
    if st.cat.is_none() {
        st.cat = Some(catalogue(&st.extra)?);
    }
    let want = images_of(st.cat.as_ref().unwrap());
    if st.live.as_ref().is_none_or(|l| !want.is_subset(&l.images)) {
        st.live = Some(read_live(&want)?);
        st.res = None;
    }
    if st.res.is_none() {
        let r = shape::resolve(st.cat.as_ref().unwrap(), &st.live.as_ref().unwrap().snap);
        for (id, e) in &r.failed {
            crate::log::info(format!("symbols: {id} unresolved: {}", serde_json::to_string(e).unwrap_or_default()));
        }
        st.res = Some(r);
    }
    f(st.live.as_ref().unwrap(), st.cat.as_ref().unwrap(), st.res.as_ref().unwrap())
}

fn split(path: &str) -> R<(&str, &str)> {
    path.rsplit_once('.').ok_or_else(|| format!("{path:?} is not <symbol>.<member>"))
}

fn why(res: &Resolution, cat: &Catalogue, id: &str) -> String {
    match res.failed.get(id) {
        Some(f) => format!("symbol {id} unresolved on this game build: {}", serde_json::to_string(f).unwrap_or_default()),
        None if cat.symbols.iter().any(|s| s.id == id) => format!("symbol {id} unresolved"),
        None => format!("unknown symbol {id}"),
    }
}

/// The class a symbol resolves to.
pub unsafe fn class(id: &str) -> R<*mut Class> {
    with(|live, cat, res| res.ok.get(id).map(|r| live.classes[r.class]).ok_or_else(|| why(res, cat, id)))
}

/// Metadata of the class a symbol resolves to (real names, types, offsets).
pub unsafe fn info(id: &str) -> R<ClassInfo> {
    with(|live, cat, res| res.ok.get(id).map(|r| live.snap.classes[r.class].clone()).ok_or_else(|| why(res, cat, id)))
}

unsafe fn member(path: &str) -> R<Member> {
    let (id, m) = split(path)?;
    with(|_, cat, res| {
        let r = res.ok.get(id).ok_or_else(|| why(res, cat, id))?;
        r.members.get(m).cloned().ok_or_else(|| format!("symbol {id} has no member {m:?}"))
    })
}

/// A field by `<symbol>.<member>`.
pub unsafe fn field(path: &str) -> R<*mut Field> {
    match member(path)? {
        Member { class, hit: MemberHit::Field(i) } => with(|live, _, _| Ok(live.fields[class][i])),
        _ => Err(format!("{path} is a method, not a field")),
    }
}

/// A method by `<symbol>.<member>`.
pub unsafe fn method(path: &str) -> R<*const Method> {
    match member(path)? {
        Member { class, hit: MemberHit::Method(i) } => with(|live, _, _| Ok(live.methods[class][i])),
        _ => Err(format!("{path} is a field, not a method")),
    }
}

unsafe fn method_info(path: &str) -> R<MethodInfo> {
    match member(path)? {
        Member { class, hit: MemberHit::Method(i) } => with(|live, _, _| Ok(live.snap.classes[class].methods[i].clone())),
        _ => Err(format!("{path} is a field, not a method")),
    }
}

/// The real (obfuscated) name of a symbol's class or member, for logs and probes.
pub unsafe fn real_name(path: &str) -> R<String> {
    if let Ok(i) = info(path) {
        return Ok(i.full_name);
    }
    let m = member(path)?;
    with(|live, _, _| Ok(shape::member_name(&live.snap, &m)))
}

/// Whether the loader's pre-hook can take this method; the reason if not.
pub fn hookable(m: &MethodInfo) -> Result<(), String> {
    if m.is_static {
        return Err("static method (hooks take instance methods)".into());
    }
    if m.ret != "System.Void" && m.ret != "System.Boolean" {
        return Err(format!("returns {} (hooks take void or bool methods)", m.ret));
    }
    if m.params.len() > 2 {
        return Err(format!("{} parameters (hooks take at most two)", m.params.len()));
    }
    if m.params.iter().any(|p| p == "System.Single" || p == "System.Double") {
        return Err("float parameter (the detour forwards integer registers only)".into());
    }
    Ok(())
}

/// Hooks `<symbol>.<member>` like [`crate::host::pre_hook_method`]: `cb(this, arg0)` on the main
/// thread before every call. Checks the hook limits first. Call it from `Plugin::init`: the loader
/// ties the hook to the plugin being initialised.
pub unsafe fn hook(path: &str, cb: PreHookFn) -> R<()> {
    hookable(&method_info(path)?).map_err(|e| format!("cannot hook {path}: {e}"))?;
    crate::host::pre_hook(method(path)?, path, cb)
}

fn require(o: *mut Object, path: &str) -> R<()> {
    if o.is_null() {
        Err(format!("{path} of a null object"))
    } else {
        Ok(())
    }
}

/// Reads an object-reference field. Main thread only.
pub unsafe fn get_obj(o: *mut Object, path: &str) -> R<*mut Object> {
    require(o, path)?;
    let f = field(path)?;
    let mut v: *mut Object = null_mut();
    (api().field_get_value)(o, f, &mut v as *mut _ as _);
    Ok(v)
}

/// Reads a value field (`i32`, `bool`, `f32`, `Vec3`, ...). `T` must match the field's type.
/// Main thread only.
pub unsafe fn get<T: Copy + Default>(o: *mut Object, path: &str) -> R<T> {
    require(o, path)?;
    let f = field(path)?;
    let mut v = T::default();
    (api().field_get_value)(o, f, &mut v as *mut T as _);
    Ok(v)
}

/// Writes a value field. Main thread only.
pub unsafe fn set<T: Copy>(o: *mut Object, path: &str, mut v: T) -> R<()> {
    require(o, path)?;
    (api().field_set_value)(o, field(path)?, &mut v as *mut T as _);
    Ok(())
}

/// Writes an object-reference field. Main thread only.
pub unsafe fn set_obj(o: *mut Object, path: &str, v: *mut Object) -> R<()> {
    require(o, path)?;
    (api().field_set_value)(o, field(path)?, v as *mut c_void);
    Ok(())
}

/// Reads a static value field. `T` must match the field's type. Main thread only.
pub unsafe fn get_static<T: Copy + Default>(path: &str) -> R<T> {
    let (id, _) = split(path)?;
    (api().runtime_class_init)(class(id)?);
    let mut v = T::default();
    (api().field_static_get_value)(field(path)?, &mut v as *mut T as _);
    Ok(v)
}

/// Writes a static value field. Main thread only.
pub unsafe fn set_static<T: Copy>(path: &str, mut v: T) -> R<()> {
    let (id, _) = split(path)?;
    (api().runtime_class_init)(class(id)?);
    (api().field_static_set_value)(field(path)?, &mut v as *mut T as _);
    Ok(())
}

/// Reads a string field (null reads as ""). Main thread only.
pub unsafe fn get_string(o: *mut Object, path: &str) -> R<String> {
    Ok(il2cpp::read_string(get_obj(o, path)?))
}

/// Writes a string field. Main thread only.
pub unsafe fn set_string(o: *mut Object, path: &str, v: &str) -> R<()> {
    set_obj(o, path, il2cpp::string(v))
}

/// Reads a static object-reference field. Main thread only.
pub unsafe fn get_static_obj(path: &str) -> R<*mut Object> {
    let (id, _) = split(path)?;
    (api().runtime_class_init)(class(id)?);
    let mut v: *mut Object = null_mut();
    (api().field_static_get_value)(field(path)?, &mut v as *mut _ as _);
    Ok(v)
}

/// The method `this` really runs for `m`: an override with the same name and parameter types in
/// its runtime class or a parent below `m`'s class, else `m`. (`runtime_invoke` does not dispatch
/// virtually.)
unsafe fn dispatch(this: *mut Object, m: *const Method, mi: &MethodInfo) -> *const Method {
    let a = api();
    let mut k = (a.object_get_class)(this) as *const Class;
    let name = std::ffi::CString::new(mi.name.as_str()).unwrap_or_default();
    while !k.is_null() {
        let mut it: *mut c_void = null_mut();
        loop {
            let c = (a.class_get_methods)(k, &mut it);
            if c.is_null() {
                break;
            }
            if c == m {
                return m;
            }
            if std::ffi::CStr::from_ptr((a.method_get_name)(c)) == name.as_c_str()
                && (a.method_get_param_count)(c) as usize == mi.params.len()
                && (0..mi.params.len()).all(|i| type_name((a.method_get_param)(c, i as u32)) == mi.params[i])
            {
                return c;
            }
        }
        k = (a.class_get_parent)(k);
    }
    m
}

/// Calls a method (`this` null for static ones), dispatching to the object's override like a
/// virtual call. Arguments as for [`il2cpp::invoke`]. Main thread only.
pub unsafe fn call(this: *mut Object, path: &str, args: &mut [*mut c_void]) -> R<*mut Object> {
    let mi = method_info(path)?;
    if mi.params.len() != args.len() {
        return Err(format!("{path} takes {} arguments, got {}", mi.params.len(), args.len()));
    }
    let mut m = method(path)?;
    if !mi.is_static {
        require(this, path)?;
        m = dispatch(this, m, &mi);
    }
    il2cpp::invoke(m, this, args).map_err(|e| format!("{path}: {e}"))
}

/// Live instances of a symbol's class (a component or other `UnityEngine.Object` type), active
/// ones only. Main thread only.
pub unsafe fn live(id: &str) -> R<Vec<*mut Object>> {
    let k = class(id)?;
    if !crate::inspect::is_unity_object(k) {
        return Err(format!("{id} is not a UnityEngine.Object type; find its instances through a field that holds one"));
    }
    crate::unity::find_objects(k)
}

/// Whether `o` is an instance of the symbol's class (or a subclass). Main thread only.
pub unsafe fn is(o: *mut Object, id: &str) -> bool {
    !o.is_null() && class(id).is_ok_and(|k| (api().class_is_assignable_from)(k, (api().object_get_class)(o)))
}

/// Every symbol's status on the running build: `{id: {class, members}}` or `{id: {error}}`.
pub unsafe fn report() -> R<Value> {
    with(|live, cat, res| {
        let mut out = serde_json::Map::new();
        for s in &cat.symbols {
            let v = match res.ok.get(&s.id) {
                Some(r) => {
                    let k = &live.snap.classes[r.class];
                    let members: serde_json::Map<String, Value> = r.members.iter().map(|(n, m)| (n.clone(), json!(shape::member_name(&live.snap, m)))).collect();
                    json!({"class": k.full_name, "members": members})
                }
                None => json!({"error": res.failed.get(&s.id)}),
            };
            out.insert(s.id.clone(), v);
        }
        Ok(json!({"game_build": live.snap.game_build, "resolved": res.ok.len(), "failed": res.failed.len(), "symbols": out}))
    })
}

/// Writes every class of `images` (default: the game image) to `path` as a [`Snapshot`] JSON file,
/// the input of the offline `sdk-symbols` tool. Returns the number of classes.
pub unsafe fn dump(path: &Path, images: &[&str]) -> R<usize> {
    let set: BTreeSet<String> = if images.is_empty() { [shape::GAME_IMAGE.to_string()].into() } else { images.iter().map(|s| s.to_string()).collect() };
    let known: HashSet<String> = il2cpp::assemblies().into_iter().map(|(_, n)| n).collect();
    if let Some(bad) = set.iter().find(|i| !known.contains(*i)) {
        return Err(format!("no loaded image {bad}"));
    }
    let live = read_live(&set)?;
    let n = live.snap.classes.len();
    let text = serde_json::to_string(&live.snap).map_err(|e| e.to_string())?;
    std::fs::write(path, text).map_err(|e| format!("{}: {e}", path.display()))?;
    Ok(n)
}

/// Images loaded in the game, for choosing what to dump.
pub unsafe fn images() -> Vec<String> {
    il2cpp::assemblies().into_iter().map(|(_, n): (*const Image, String)| n).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hook_limits() {
        let m = |ret: &str, params: &[&str], st: bool| MethodInfo { name: "m".into(), params: params.iter().map(|s| s.to_string()).collect(), ret: ret.into(), is_static: st };
        assert!(hookable(&m("System.Void", &["System.Int32", "Foo"], false)).is_ok());
        assert!(hookable(&m("System.Boolean", &[], false)).is_ok());
        assert!(hookable(&m("System.Int32", &[], false)).is_err());
        assert!(hookable(&m("System.Void", &["System.Single"], false)).is_err());
        assert!(hookable(&m("System.Void", &["a", "b", "c"], false)).is_err());
        assert!(hookable(&m("System.Void", &[], true)).is_err());
    }

    #[test]
    fn member_paths_split_on_the_last_dot() {
        assert_eq!(split("map.importer.terrains").unwrap(), ("map.importer", "terrains"));
        assert!(split("nodot").is_err());
    }

    #[test]
    fn register_rejects_bad_catalogues() {
        assert!(register("{\"symbols\": [{\"id\": \"BAD\", \"summary\": \"\", \"match\": {}}]}").is_err());
        assert!(register("not json").is_err());
    }
}
