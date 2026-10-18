//! The **Mods** tab in the game's own Options window: one collapsible section per plugin, holding
//! that plugin's settings.
//!
//! The Options window (`UI/MainMenu/OptionsMenu`) is a `TabMenu`: a row of tab buttons
//! (`Tabs/GameplayButton`, ...) and one page per tab (`Windows/Gameplay`, ...), each page a
//! `VerticalList` of the game's widgets: `ToggleButton` (on/off), `ArrowSelection` (◀ value ▶) and
//! `LabeledSlider`. We clone the Gameplay tab button and page ("Mod_Tab", "Mod_Page"), empty the page,
//! and fill it with clones of those widgets: per plugin a header button (cloned from the window's
//! Back button) that folds its rows open and shut, then one row per entry of the plugin's
//! `plugin.info` schema. The TabMenu keeps its tabs in two arrays (buttons, (page, list) pairs); ours
//! are appended so the game itself hides and shows the page when switching tabs.
//!
//! The originals are inactive until the window first opens, so Awake/Start never run on a clone
//! before we've cut it loose from the game's settings (`globalName`/`save`) and pointed its change
//! callback at us. Callbacks are native delegates (as in `chatcmd`): the delegate's `method_code` is
//! passed to our function as its first argument and carries an index into `CONTROLS`/`SECTIONS`.
//! Rows are named `<plugin id>.<key>` and headers `<plugin id>`, so folding works by name and never
//! keeps pointers to scene objects between frames.

use crate::il2cpp::{self, api, type_name, Class, Object};
use crate::unity::{self, R};
use crate::{host, log};
use serde_json::{json, Value};
use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::Mutex;

const OPTIONS: &str = "MainMenu/OptionsMenu/Content";
const OURS: &str = "Mod_";

/// What a widget changes: plugin id, schema key, and how to turn the widget's value into JSON.
struct Control {
    plugin: u32,
    key: String,
    kind: Kind,
    /// Row object name (`<plugin id>.<key>`) and the value to show when the row starts.
    row: String,
    value: Value,
    /// Slider range.
    range: (f32, f32),
}

enum Kind {
    Toggle,
    /// Choice: the option strings, sent as the chosen string.
    Choice(Vec<String>),
    Slider,
}

/// A plugin's collapsible section: its id (row-name prefix), display name and whether it's open.
struct Section {
    id: String,
    name: String,
    open: bool,
    plugin: u32,
}

static CONTROLS: Mutex<Vec<Control>> = Mutex::new(Vec::new());

/// True while `refresh` pushes values into widgets: their change callbacks fire then and must not
/// be taken as user edits. (Main thread only, like every callback here.)
static APPLYING: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static SECTIONS: Mutex<Vec<Section>> = Mutex::new(Vec::new());

// ---- talking to plugins through the loader ---------------------------------------------------

unsafe fn take(p: *mut u8, n: usize) -> Value {
    let v = if p.is_null() { Value::Null } else { serde_json::from_slice(std::slice::from_raw_parts(p, n)).unwrap_or(Value::Null) };
    if !p.is_null() {
        (host().free_buf)(p, n);
    }
    v
}

fn plugins() -> Vec<(u32, Value)> {
    unsafe {
        let (mut p, mut n) = (null_mut(), 0usize);
        (host().list_plugins)(&mut p, &mut n);
        let list = take(p, n);
        let mut out = vec![];
        for e in list.as_array().cloned().unwrap_or_default() {
            let Some(id) = e.get("id").and_then(Value::as_u64) else { continue };
            let info = call(id as u32, &json!({"cmd": "plugin.info"}));
            if info.get("ok").and_then(Value::as_bool) == Some(true) && info.get("settings").is_some() {
                out.push((id as u32, info));
            }
        }
        out
    }
}

fn call(id: u32, req: &Value) -> Value {
    unsafe {
        let s = req.to_string();
        let (mut p, mut n) = (null_mut(), 0usize);
        if (host().call_plugin)(id, s.as_ptr(), s.len(), &mut p, &mut n) != 0 {
            return Value::Null;
        }
        take(p, n)
    }
}

// ---- Unity helpers -----------------------------------------------------------------------------

pub(crate) unsafe fn ui_root() -> R<*mut Object> {
    let sm = unity::class(unity::CORE, "UnityEngine.SceneManagement", "SceneManager")?;
    let sk = unity::class(unity::CORE, "UnityEngine.SceneManagement", "Scene")?;
    let scene = unity::call(sm, null_mut(), "GetActiveScene", &mut [])?;
    let roots = unity::array_items(unity::call(sk, (api().object_unbox)(scene), "GetRootGameObjects", &mut [])?);
    let ui = roots.into_iter().find(|g| unity::name(*g) == "UI").ok_or("no UI root (not in the main scene?)")?;
    unity::callv(ui, "get_transform", &mut [])
}

/// Child transform by path, or null.
pub(crate) unsafe fn find(tr: *mut Object, path: &str) -> *mut Object {
    if tr.is_null() {
        return null_mut();
    }
    unity::callv(tr, "Find", &mut [il2cpp::string(path) as *mut c_void]).unwrap_or(null_mut())
}

unsafe fn go_of(tr: *mut Object) -> R<*mut Object> {
    unity::callv(tr, "get_gameObject", &mut [])
}

unsafe fn children(tr: *mut Object) -> Vec<*mut Object> {
    let n = unity::call_unbox::<i32>(tr, "get_childCount").unwrap_or(0);
    (0..n)
        .filter_map(|i| {
            let mut i = i;
            unity::callv(tr, "GetChild", &mut [&mut i as *mut i32 as _]).ok()
        })
        .collect()
}

pub(crate) unsafe fn set_active(tr: *mut Object, on: bool) {
    if let Ok(go) = go_of(tr) {
        let mut on = on;
        let _ = unity::callv(go, "SetActive", &mut [&mut on as *mut bool as _]);
    }
}

/// Clones `template` (a Transform) under `parent` (a Transform) and names it; returns the clone's Transform.
pub(crate) unsafe fn clone(template: *mut Object, parent: *mut Object, name: &str) -> R<*mut Object> {
    let obj_k = unity::class(unity::CORE, "UnityEngine", "Object")?;
    let inst = il2cpp::method_sig(obj_k, "Instantiate", &["UnityEngine.Object", "UnityEngine.Transform"]).ok_or("Instantiate(Object, Transform) missing")?;
    let go = il2cpp::invoke(inst, null_mut(), &mut [go_of(template)? as *mut c_void, parent as *mut c_void])?;
    unity::callv(go, "set_name", &mut [il2cpp::string(name) as *mut c_void])?;
    unity::callv(go, "get_transform", &mut [])
}

pub(crate) unsafe fn destroy(tr: *mut Object) -> R<()> {
    let obj_k = unity::class(unity::CORE, "UnityEngine", "Object")?;
    unity::call(obj_k, null_mut(), "DestroyImmediate", &mut [go_of(tr)? as *mut c_void])?;
    Ok(())
}

/// The first component of the GameObject whose class is (or derives from) `class_name`.
pub(crate) unsafe fn component(tr: *mut Object, class_name: &str) -> R<*mut Object> {
    let a = api();
    let comp_k = unity::class(unity::CORE, "UnityEngine", "Component")?;
    let all = unity::callv(go_of(tr)?, "GetComponents", &mut [unity::type_object(comp_k) as *mut c_void])?;
    for c in unity::array_items(all) {
        let mut k = (a.object_get_class)(c);
        while !k.is_null() {
            if il2cpp::cstr((a.class_get_name)(k)) == class_name {
                return Ok(c);
            }
            k = (a.class_get_parent)(k);
        }
    }
    Err(format!("no {class_name} component"))
}

pub(crate) unsafe fn set_text(tr: *mut Object, path: &str, text: &str) {
    let t = if path.is_empty() { tr } else { find(tr, path) };
    if t.is_null() {
        return;
    }
    if let Ok(tmp) = component(t, "TMP_Text") {
        let _ = unity::callv(tmp, "set_text", &mut [il2cpp::string(text) as *mut c_void]);
    }
}

/// The first instance field of `o`'s class (or its parents) whose type name is `ty`.
unsafe fn field_by_type(o: *mut Object, ty: &str) -> R<*mut il2cpp::Field> {
    if o.is_null() {
        return Err(format!("{ty} field of a null object"));
    }
    let a = api();
    let mut k = (a.object_get_class)(o);
    while !k.is_null() {
        let mut it: *mut c_void = null_mut();
        loop {
            let f = (a.class_get_fields)(k, &mut it);
            if f.is_null() {
                break;
            }
            if (a.field_get_flags)(f) & 0x10 == 0 && type_name((a.field_get_type)(f)) == ty {
                return Ok(f);
            }
        }
        k = (a.class_get_parent)(k);
    }
    Err(format!("no field of type {ty}"))
}

/// Cuts a cloned widget loose from the game's settings store.
unsafe fn unbind(w: *mut Object) {
    let _ = unity::set_field_obj(w, "globalName", null_mut());
    let a = api();
    if let Some(f) = il2cpp::field((a.object_get_class)(w), "save") {
        let mut no = false;
        (a.field_set_value)(w, f, &mut no as *mut bool as _);
    }
}

/// A delegate of class `dk` that calls `f` with `id` as its first argument.
unsafe fn delegate(dk: *mut Class, f: usize, id: usize) -> R<*mut Object> {
    let a = api();
    let d = (a.object_new)(dk);
    let invoke = il2cpp::method(dk, "Invoke", -1).ok_or("delegate Invoke missing")?;
    for (name, v) in [("method_ptr", f), ("invoke_impl", f), ("method", invoke as usize), ("method_code", id)] {
        let fld = il2cpp::field(dk, name).ok_or_else(|| format!("delegate field {name} missing"))?;
        let mut v = v;
        (a.field_set_value)(d, fld, &mut v as *mut usize as _);
    }
    Ok(d)
}

/// Points the widget's first callback field of type `ty` (e.g. "System.Action<System.Boolean>") at
/// `f`, which receives `id` first. Replaces whatever the clone carried over.
unsafe fn hook(w: *mut Object, ty: &str, f: usize, id: usize) -> R<()> {
    let a = api();
    let fld = field_by_type(w, ty)?;
    let d = delegate((a.class_from_type)((a.field_get_type)(fld)), f, id)?;
    (a.field_set_value)(w, fld, d as *mut c_void);
    Ok(())
}

/// Copies a managed array into a new one of `n` elements (same element class).
unsafe fn resized(arr: *mut Object, n: usize) -> R<*mut Object> {
    let a = api();
    let ek = (a.class_get_element_class)((a.object_get_class)(arr));
    let old = (a.array_length)(arr) as usize;
    let size = if (a.class_is_valuetype)(ek) { (a.class_value_size)(ek, null_mut()) as usize } else { std::mem::size_of::<usize>() };
    let new = (a.array_new)(ek, n);
    if new.is_null() {
        return Err("array_new failed".into());
    }
    std::ptr::copy_nonoverlapping(il2cpp::array_data::<u8>(arr), il2cpp::array_data::<u8>(new), old.min(n) * size);
    Ok(new)
}

// ---- callbacks (main thread) ---------------------------------------------------------------------

fn changed(id: usize, value: Value) {
    if APPLYING.load(std::sync::atomic::Ordering::Relaxed) {
        return;
    }
    let (plugin, key, value) = {
        let mut c = CONTROLS.lock().unwrap();
        let Some(ctl) = c.get_mut(id) else { return };
        let v = match (&ctl.kind, &value) {
            (Kind::Choice(opts), Value::Number(n)) => opts.get(n.as_u64().unwrap_or(0) as usize).map(|s| json!(s)).unwrap_or(Value::Null),
            _ => value,
        };
        ctl.value = v.clone();
        (ctl.plugin, ctl.key.clone(), v)
    };
    let r = call(plugin, &json!({"cmd": "plugin.set", "key": key, "value": value}));
    log::info(format!("mods menu: {key} = {value} -> ok {}", r.get("ok").map(|v| v.to_string()).unwrap_or_default()));
    // One setting can move others (a preset sets fog, snow, ...): take all of the plugin's current
    // values from the reply so every row's getter shows them.
    let mut others_moved = false;
    if let Some(values) = r.get("values").and_then(Value::as_object) {
        for ctl in CONTROLS.lock().unwrap().iter_mut().filter(|c| c.plugin == plugin) {
            if let Some(v) = values.get(&ctl.key) {
                others_moved |= ctl.key != key && ctl.value != *v;
                ctl.value = v.clone();
            }
        }
    }
    // The form reads the getters when it refreshes: redraw it so moved rows show their new values
    // (not on a plain slider drag, which would fight the drag).
    if others_moved {
        std::thread::spawn(|| {
            std::thread::sleep(std::time::Duration::from_millis(30));
            let _ = crate::pump::run(|| unsafe {
                if let Ok(form) = our_form() {
                    let _ = unity::callv(form, "Refresh", &mut []);
                }
            });
        });
    }
}

/// Takes the Mods tab out of the Options window again (before this code is unloaded: the tab's
/// widgets call into it every frame) and puts the TabMenu back on its first tab.
pub fn teardown() {
    unsafe {
        let Ok(ui) = ui_root() else { return };
        let content = find(ui, OPTIONS);
        let tabs = find(content, "TabMenu");
        let Ok(tab_menu) = component(tabs, "TabMenu") else { return };
        let a = api();
        if let (Ok(bf), Ok(pf)) = (field_by_type(tab_menu, "Button[]"), field_by_type(tab_menu, "System.ValueTuple<UnityEngine.RectTransform,NavigatableMenu>[]")) {
            let get = |f| {
                let mut v: *mut Object = null_mut();
                (a.field_get_value)(tab_menu, f, &mut v as *mut _ as _);
                v
            };
            let base = unity::array_items(get(bf)).iter().take_while(|b| !unity::name(go_of(**b).unwrap_or(null_mut())).starts_with(OURS)).count();
            if let (Ok(b), Ok(p)) = (resized(get(bf), base), resized(get(pf), base)) {
                (a.field_set_value)(tab_menu, bf, b as *mut c_void);
                (a.field_set_value)(tab_menu, pf, p as *mut c_void);
            }
        }
        if let Ok(f) = field_by_type(tab_menu, "System.Int32") {
            let mut first = 0i32;
            (a.field_set_value)(tab_menu, f, &mut first as *mut i32 as _);
        }
        for parent in [find(tabs, "Tabs"), find(tabs, "Windows")] {
            for c in children(parent) {
                if unity::name(go_of(c).unwrap_or(null_mut())).starts_with(OURS) {
                    let _ = destroy(c);
                }
            }
        }
        CONTROLS.lock().unwrap().clear();
        SECTIONS.lock().unwrap().clear();
        log::info("mods menu: removed");
    }
}

unsafe extern "C" fn on_bool(id: usize, v: bool, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| changed(id, json!(v)));
}

unsafe extern "C" fn on_int(id: usize, v: i32, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| changed(id, json!(v)));
}

unsafe extern "C" fn on_float(id: usize, v: f32, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| changed(id, json!(v)));
}

/// The Mods tab button was clicked: `id` is its index in the TabMenu.
unsafe extern "C" fn on_tab(id: usize, _button: *mut Object, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| unsafe {
        let Ok(content) = ui_root().map(|ui| find(ui, OPTIONS)) else { return };
        let Ok(tm) = component(find(content, "TabMenu"), "TabMenu") else { return };
        match tab_switch(tm) {
            Some(m) => {
                let (mut i, mut a, mut b) = (id as i32, true, false);
                if let Err(e) = il2cpp::invoke(m, tm as *mut c_void, &mut [&mut i as *mut i32 as _, &mut a as *mut bool as _, &mut b as *mut bool as _]) {
                    log::info(format!("mods menu: tab switch failed: {e}"));
                }
            }
            None => log::info("mods menu: TabMenu tab switch not found"),
        }
        // Rows that just became visible start next frame and reset their text; fix them up after.
        refresh_soon();
    });
}

/// A section header was clicked: fold that plugin's rows open or shut.
unsafe extern "C" fn on_header(id: usize, _button: *mut Object, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| unsafe {
        let (pid, name, open) = {
            let mut s = SECTIONS.lock().unwrap();
            let Some(sec) = s.get_mut(id) else { return };
            sec.open = !sec.open;
            (sec.id.clone(), sec.name.clone(), sec.open)
        };
        let Ok(list) = our_list() else { return };
        let prefix = format!("{pid}.");
        for c in children(list) {
            let n = unity::name(go_of(c).unwrap_or(null_mut()));
            if n.starts_with(&prefix) {
                set_active(c, open);
            } else if n == pid {
                label(c, &header_text(&name, open));
            }
        }
        // Rows shown for the first time start next frame and reset themselves; set them after that.
        refresh_soon();
    });
}

/// The game's font has no arrow glyphs; plain characters read the same.
fn header_text(name: &str, open: bool) -> String {
    format!("{name}   {}", if open { "v" } else { ">" })
}

unsafe fn our_list() -> R<*mut Object> {
    let content = find(ui_root()?, OPTIONS);
    let list = find(content, &format!("TabMenu/Windows/{OURS}Page/VList"));
    if list.is_null() {
        return Err("Mods page not built".into());
    }
    Ok(list)
}

/// Sets the text of the row's own label (its first TMP text).
unsafe fn label(row: *mut Object, text: &str) {
    let Ok(tk) = unity::class("Unity.TextMeshPro.dll", "TMPro", "TMP_Text") else { return };
    let mut inactive = true;
    if let Ok(t) = unity::callv(go_of(row).unwrap_or(null_mut()), "GetComponentInChildren", &mut [unity::type_object(tk) as *mut c_void, &mut inactive as *mut bool as _]) {
        if !t.is_null() {
            let _ = unity::callv(t, "set_text", &mut [il2cpp::string(text) as *mut c_void]);
        }
    }
}

/// Runs `refresh` on the main thread a few frames from now.
fn refresh_soon() {
    std::thread::spawn(|| {
        std::thread::sleep(std::time::Duration::from_millis(150));
        let _ = crate::pump::run(|| unsafe {
            if let Err(e) = refresh() {
                log::info(format!("mods menu: refresh: {e}"));
            }
        });
    });
}

/// The method of `o`'s class named `name` whose parameter types are exactly `sig`.
unsafe fn method_on(o: *mut Object, name: &str, sig: &[&str]) -> Option<*const il2cpp::Method> {
    let mut k = (api().object_get_class)(o);
    while !k.is_null() {
        if let Some(m) = il2cpp::method_sig(k, name, sig) {
            return Some(m);
        }
        k = (api().class_get_parent)(k);
    }
    None
}

/// Puts each visible row in the state we want, after the widget's own Start has run: header labels
/// and click handlers, choice lists, slider ranges and every current value.
unsafe fn refresh() -> R<()> {
    let list = our_list()?;
    let rows: Vec<(String, *mut Object)> = children(list).into_iter().map(|c| (unity::name(go_of(c).unwrap_or(null_mut())), c)).collect();
    let row = |n: &str| rows.iter().find(|(name, _)| name == n).map(|r| r.1);

    // Never hold SECTIONS/CONTROLS while calling the game: setting a widget fires its change
    // callback, which would take the same lock on this thread (that froze the game once).
    let sections: Vec<(usize, String, String, bool)> = SECTIONS.lock().unwrap().iter().enumerate().map(|(i, s)| (i, s.id.clone(), s.name.clone(), s.open)).collect();
    let controls: Vec<(usize, Kind, String, Value, (f32, f32))> = CONTROLS
        .lock()
        .unwrap()
        .iter()
        .enumerate()
        .map(|(i, c)| {
            let kind = match &c.kind {
                Kind::Toggle => Kind::Toggle,
                Kind::Choice(o) => Kind::Choice(o.clone()),
                Kind::Slider => Kind::Slider,
            };
            (i, kind, c.row.clone(), c.value.clone(), c.range)
        })
        .collect();
    APPLYING.store(true, std::sync::atomic::Ordering::Relaxed);
    let r = apply(&row, &sections, &controls);
    APPLYING.store(false, std::sync::atomic::Ordering::Relaxed);
    r
}

unsafe fn apply(
    row: &dyn Fn(&str) -> Option<*mut Object>,
    sections: &[(usize, String, String, bool)],
    controls: &[(usize, Kind, String, Value, (f32, f32))],
) -> R<()> {
    let a = api();
    for (i, id, name, open) in sections {
        let (i, open) = (*i, *open);
        let sec_name = name;
        let Some(r) = row(id) else { continue };
        let Ok(w) = component(r, "Button") else { continue };
        // replaces the toggle's own click handler too: the header only folds
        hook(w, "System.Action<Button>", on_header as *const () as usize, i)?;
        label(r, &header_text(sec_name, open));
    }
    for (cid, kind, row_name, value, range) in controls {
        let cid = *cid;
        let Some(r) = row(row_name) else { continue };
        if !unity::call_unbox::<bool>(go_of(r)?, "get_activeInHierarchy").unwrap_or(false) {
            continue;
        }
        match kind {
            Kind::Toggle => {
                let w = component(r, "ToggleButton")?;
                if let Some(f) = il2cpp::field((a.object_get_class)(w), "isOn") {
                    let mut on = value.as_bool().unwrap_or(false);
                    (a.field_set_value)(w, f, &mut on as *mut bool as _);
                }
                // re-render "Label: On/Off" (the no-argument refresh method)
                if let Some(m) = method_on(w, "hcj", &[]) {
                    let _ = il2cpp::invoke(m, w as *mut c_void, &mut []);
                }
                hook(w, "System.Action<System.Boolean>", on_bool as *const () as usize, cid)?;
            }
            Kind::Choice(opts) => {
                let w = component(r, "ArrowSelection")?;
                // the template (respawn hold time) parses its options as numbers; ours are names
                if let Some(f) = il2cpp::field((api().object_get_class)(w), "globalIsFloat") {
                    let mut no = false;
                    (api().field_set_value)(w, f, &mut no as *mut bool as _);
                }
                let sk = unity::class("mscorlib.dll", "System", "String")?;
                let arr = (a.array_new)(sk, opts.len());
                for (i, o) in opts.iter().enumerate() {
                    *il2cpp::array_data::<*mut Object>(arr).add(i) = il2cpp::string(o);
                }
                let mut idx = opts.iter().position(|o| Some(o.as_str()) == value.as_str()).unwrap_or(0) as i32;
                let mut notify = false;
                match method_on(w, "guh", &["System.String[]", "System.Int32", "System.Boolean"]) {
                    Some(m) => {
                        il2cpp::invoke(m, w as *mut c_void, &mut [arr as *mut c_void, &mut idx as *mut i32 as _, &mut notify as *mut bool as _])?;
                    }
                    None => log::info("mods menu: ArrowSelection set-options not found"),
                }
                hook(w, "System.Action<System.Int32>", on_int as *const () as usize, cid)?;
            }
            Kind::Slider => {
                let w = component(r, "LabeledSlider")?;
                if let Ok(sl) = component(r, "Slider") {
                    let (mut lo, mut hi) = *range;
                    let _ = unity::callv(sl, "set_minValue", &mut [&mut lo as *mut f32 as _]);
                    let _ = unity::callv(sl, "set_maxValue", &mut [&mut hi as *mut f32 as _]);
                }
                let mut v = value.as_f64().unwrap_or(0.0) as f32;
                let mut notify = false;
                // set value + label without firing the change callback
                match method_on(w, "gxd", &["System.Single", "System.Boolean"]) {
                    Some(m) => {
                        il2cpp::invoke(m, w as *mut c_void, &mut [&mut v as *mut f32 as _, &mut notify as *mut bool as _])?;
                    }
                    None => log::info("mods menu: LabeledSlider set-value not found"),
                }
                hook(w, "System.Action<System.Single>", on_float as *const () as usize, cid)?;
            }
        }
    }
    Ok(())
}

/// TabMenu's tab switch: the method shaped (int, bool, bool) -> void, found by signature.
unsafe fn tab_switch(tm: *mut Object) -> Option<*const il2cpp::Method> {
    let a = api();
    let k = (a.object_get_class)(tm);
    let mut it: *mut c_void = null_mut();
    loop {
        let m = (a.class_get_methods)(k, &mut it);
        if m.is_null() {
            return None;
        }
        if (a.method_get_param_count)(m) == 3
            && type_name((a.method_get_param)(m, 0)) == "System.Int32"
            && type_name((a.method_get_param)(m, 1)) == "System.Boolean"
            && type_name((a.method_get_param)(m, 2)) == "System.Boolean"
            && type_name((a.method_get_return_type)(m)) == "System.Void"
        {
            return Some(m);
        }
    }
}

// ---- building the tab ------------------------------------------------------------------------------

/// Adds (or rebuilds) the Mods tab. Safe to call repeatedly.
pub fn build(_req: &Value) -> R<Value> {
    unsafe {
        let a = api();
        let content = find(ui_root()?, OPTIONS);
        let tabs = find(content, "TabMenu");
        let tab_row = find(tabs, "Tabs");
        let pages = find(tabs, "Windows");
        let gameplay_btn = find(tab_row, "GameplayButton");
        let gameplay = find(pages, "Gameplay");
        let tpl_toggle = find(gameplay, "VList/SnowboardToggleButton");
        let tpl_choice = find(gameplay, "VList/RespawnHoldTimeSelection");
        let tpl_slider = find(pages, "Audio/VList/VolumeSlider");
        if [content, tabs, tab_row, pages, gameplay_btn, gameplay, tpl_toggle, tpl_choice, tpl_slider].iter().any(|p| p.is_null()) {
            return Err("Options window layout changed (game update?)".into());
        }
        let tab_menu = component(tabs, "TabMenu")?;
        let buttons_f = field_by_type(tab_menu, "Button[]")?;
        let pages_f = field_by_type(tab_menu, "System.ValueTuple<UnityEngine.RectTransform,NavigatableMenu>[]")?;
        let get = |f| {
            let mut v: *mut Object = null_mut();
            (a.field_get_value)(tab_menu, f, &mut v as *mut _ as _);
            v
        };

        // Remove an earlier build: our objects (named Mod_*) and our entries in the TabMenu arrays.
        let base = unity::array_items(get(buttons_f)).iter().take_while(|b| !unity::name(go_of(**b).unwrap_or(null_mut())).starts_with(OURS)).count();
        for parent in [tab_row, pages] {
            for c in children(parent) {
                let n = unity::name(go_of(c)?);
                if n.starts_with(OURS) || n == "ModsButton" || n == "Mods" {
                    destroy(c)?;
                }
            }
        }

        // Tab button + page.
        let tab_index = base;
        let b = clone(gameplay_btn, tab_row, &format!("{OURS}Tab"))?;
        set_text(b, "LabelText", "Mods");
        // The clone carries the Gameplay tab's state, which is "selected" (grey, not clickable)
        // whenever Gameplay is the open tab. Take the look of an unselected game tab instead.
        let selectable = "<uhg>k__BackingField"; // UIBehaviour: interactable (false = the open tab)
        let idle = children(tab_row).into_iter().find(|c| {
            !unity::name(go_of(*c).unwrap_or(null_mut())).starts_with(OURS)
                && component(*c, "Button").ok().and_then(|w| unity::field_val::<bool>(w, selectable).ok()) == Some(true)
        });
        if let (Some(idle), Ok(ours)) = (idle, component(b, "Button")) {
            if let Some(f) = il2cpp::field((a.object_get_class)(ours), selectable) {
                let mut yes = true;
                (a.field_set_value)(ours, f, &mut yes as *mut bool as _);
            }
            // the button eases its image towards a target colour it keeps itself; copy that too
            if let (Ok(idle_btn), Ok(f)) = (component(idle, "Button"), field_by_type(ours, "UnityEngine.Color")) {
                let mut c = [0f32; 4];
                (a.field_get_value)(idle_btn, f, c.as_mut_ptr() as _);
                (a.field_set_value)(ours, f, c.as_mut_ptr() as _);
            }
            if let (Ok(from), Ok(to)) = (component(idle, "Image"), component(b, "Image")) {
                if let Ok(c) = unity::callv(from, "get_color", &mut []) {
                    let _ = unity::callv(to, "set_color", &mut [(a.object_unbox)(c)]);
                }
            }
        }
        let bw = component(b, "Button")?;
        hook(bw, "System.Action<Button>", on_tab as *const () as usize, tab_index)?;
        let page = clone(gameplay, pages, &format!("{OURS}Page"))?;
        let list = find(page, "VList");
        for c in children(list) {
            destroy(c)?;
        }
        set_active(page, false);

        // Register them with the TabMenu: append to its (button) and (page, list) arrays.
        let buttons = resized(get(buttons_f), base + 1)?;
        *il2cpp::array_data::<*mut Object>(buttons).add(tab_index) = bw;
        let page_arr = resized(get(pages_f), base + 1)?;
        let tuple_k = (a.class_get_element_class)((a.object_get_class)(page_arr));
        let tuple_size = (a.class_value_size)(tuple_k, null_mut()) as usize;
        *(il2cpp::array_data::<u8>(page_arr).add(tab_index * tuple_size) as *mut [*mut Object; 2]) = [page, component(list, "NavigatableMenu")?];
        (a.field_set_value)(tab_menu, buttons_f, buttons as *mut c_void);
        (a.field_set_value)(tab_menu, pages_f, page_arr as *mut c_void);

        // The page's content is a FormMenu (the game's own builder UI, as in its Environment
        // window): it sizes itself to the page and folds its Accordion sections without our help.
        if let Ok(vl) = component(list, "NavigatableMenu") {
            let mut off = false;
            let _ = unity::callv(vl, "set_enabled", &mut [&mut off as *mut bool as _]);
        }
        let (form, n) = fill_form(list)?;
        // controller/keyboard navigation of the tab goes into the form
        let pairs = get(pages_f);
        *(il2cpp::array_data::<u8>(pairs).add(tab_index * tuple_size) as *mut [*mut Object; 2]) = [page, form];
        // the tab row (a HorizontalList) places its buttons once; let it place ours too
        if let Ok(row) = component(tab_row, "NavigatableMenu") {
            let _ = unity::callv(row, "gwp", &mut []);
        }
        // Options opens on whichever tab the TabMenu remembers; after a rebuild that index can point
        // at a page that no longer exists (an empty window). Always start on the first tab.
        if let Ok(f) = field_by_type(tab_menu, "System.Int32") {
            let mut first = 0i32;
            (a.field_set_value)(tab_menu, f, &mut first as *mut i32 as _);
        }
        if unity::call_unbox::<bool>(go_of(tabs)?, "get_activeInHierarchy").unwrap_or(false) {
            // open right now: switching to the tab it already "has" is ignored, so mark none first
            if let Ok(f) = field_by_type(tab_menu, "System.Int32") {
                let mut none = -1i32;
                (a.field_set_value)(tab_menu, f, &mut none as *mut i32 as _);
            }
            if let Some(m) = tab_switch(tab_menu) {
                let (mut i, mut x, mut y) = (0i32, true, false);
                let _ = il2cpp::invoke(m, tab_menu as *mut c_void, &mut [&mut i as *mut i32 as _, &mut x as *mut bool as _, &mut y as *mut bool as _]);
            }
        }
        log::info(format!("mods menu: {n} settings"));
        Ok(json!({"ok": true, "settings": n}))
    }
}

// ---- keeping the tab alive -----------------------------------------------------------------------

/// Whether the tab must be (re)built: the Options window has its own Mods tab and page, and the
/// TabMenu's button array still lists the tab. Leaving a server reloads the main menu, which
/// brings back a fresh Options window without them.
fn needs_build(tab: bool, page: bool, registered: bool) -> bool {
    !(tab && page && registered)
}

/// True when `last` already holds `e`; otherwise remembers it and returns false (log it once).
fn repeated_error(last: &mut Option<String>, e: &str) -> bool {
    if last.as_deref() == Some(e) {
        return true;
    }
    *last = Some(e.to_string());
    false
}

/// Rebuilds the Mods tab if the Options window doesn't have it (Ok(true) = rebuilt). Does nothing
/// while there is no main menu (a map is loaded) or the Options window isn't there yet. Cheap when
/// the tab is present. Main thread only.
pub fn ensure() -> R<bool> {
    unsafe {
        let Ok(ui) = ui_root() else { return Ok(false) };
        let tabs = find(find(ui, OPTIONS), "TabMenu");
        if tabs.is_null() {
            return Ok(false);
        }
        let a = api();
        let registered = (|| -> R<bool> {
            let tab_menu = component(tabs, "TabMenu")?;
            let f = field_by_type(tab_menu, "Button[]")?;
            let mut arr: *mut Object = null_mut();
            (a.field_get_value)(tab_menu, f, &mut arr as *mut _ as _);
            if arr.is_null() {
                return Ok(false);
            }
            Ok(unity::array_items(arr).iter().any(|b| !b.is_null() && unity::name(go_of(*b).unwrap_or(null_mut())).starts_with(OURS)))
        })()
        .unwrap_or(false);
        let tab = !find(tabs, &format!("Tabs/{OURS}Tab")).is_null();
        let page = !find(tabs, &format!("Windows/{OURS}Page")).is_null();
        if !needs_build(tab, page, registered) {
            return Ok(false);
        }
        build(&Value::Null).map(|_| true)
    }
}

static STOP: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static WATCHER: Mutex<Option<std::thread::JoinHandle<()>>> = Mutex::new(None);

/// Starts the background watcher (once): every two seconds it has the main thread run `ensure`,
/// so the tab is built when the menu first appears and again after every return to it.
pub fn watch() {
    use std::sync::atomic::Ordering::Acquire;
    let mut w = WATCHER.lock().unwrap();
    if w.is_some() {
        return;
    }
    STOP.store(false, std::sync::atomic::Ordering::Release);
    *w = Some(std::thread::spawn(|| {
        let mut last_err: Option<String> = None;
        loop {
            for _ in 0..40 {
                if STOP.load(Acquire) {
                    return;
                }
                std::thread::sleep(std::time::Duration::from_millis(50));
            }
            match crate::pump::run(ensure).and_then(|r| r) {
                Ok(true) => {
                    last_err = None;
                    log::info("mods menu: rebuilt after the main menu was recreated");
                }
                Ok(false) => last_err = None,
                Err(e) => {
                    if !repeated_error(&mut last_err, &e) {
                        log::info(format!("mods menu: waiting for the Options window: {e}"));
                    }
                }
            }
        }
    }));
}

/// Stops the watcher and waits for it (before this code is unloaded).
pub fn stop() {
    STOP.store(true, std::sync::atomic::Ordering::Release);
    let h = WATCHER.lock().unwrap().take();
    if let Some(h) = h {
        let _ = h.join();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_when_any_part_is_missing() {
        assert!(!needs_build(true, true, true));
        assert!(needs_build(false, false, false));
        assert!(needs_build(true, true, false));
        assert!(needs_build(true, false, true));
        assert!(needs_build(false, true, true));
    }

    #[test]
    fn logs_an_error_once() {
        let mut last = None;
        assert!(!repeated_error(&mut last, "not ready"));
        assert!(repeated_error(&mut last, "not ready"));
        assert!(!repeated_error(&mut last, "layout changed"));
    }
}

// ---- FormMenu content --------------------------------------------------------------------------

/// The FormMenu method with parameter types `sig` (and name `name`, when given): FormMenu is a game
/// class, so the helpers whose names are obfuscated are found by signature.
unsafe fn form_method(name: Option<&str>, sig: &[&str]) -> R<*const il2cpp::Method> {
    let a = api();
    let k = unity::class("Assembly-CSharp.dll", "", "FormMenu")?;
    let mut it: *mut c_void = null_mut();
    loop {
        let m = (a.class_get_methods)(k, &mut it);
        if m.is_null() {
            return Err(format!("FormMenu.{}({}) not found", name.unwrap_or("?"), sig.join(", ")));
        }
        if name.map(|n| il2cpp::cstr((a.method_get_name)(m)) == n).unwrap_or(true)
            && (a.method_get_param_count)(m) as usize == sig.len()
            && sig.iter().enumerate().all(|(i, t)| type_name((a.method_get_param)(m, i as u32)) == *t)
        {
            return Ok(m);
        }
    }
}

/// Delegate for parameter `i` of `m`, calling `f` with `id` first.
unsafe fn arg_delegate(m: *const il2cpp::Method, i: u32, f: usize, id: usize) -> R<*mut Object> {
    let a = api();
    delegate((a.class_from_type)((a.method_get_param)(m, i)), f, id)
}

/// The FormMenu inside our page.
unsafe fn our_form() -> R<*mut Object> {
    let page = find(find(ui_root()?, OPTIONS), &format!("TabMenu/Windows/{OURS}Page"));
    if page.is_null() {
        return Err("Mods page not built".into());
    }
    let k = unity::class("Assembly-CSharp.dll", "", "FormMenu")?;
    let mut inactive = true;
    let f = unity::callv(go_of(page)?, "GetComponentInChildren", &mut [unity::type_object(k) as *mut c_void, &mut inactive as *mut bool as _])?;
    if f.is_null() {
        return Err("no FormMenu on the Mods page".into());
    }
    Ok(f)
}

/// Creates the FormMenu in `page` and adds one Accordion (folding section) per plugin. Returns the
/// number of settings.
unsafe fn fill_form(parent: *mut Object) -> R<(*mut Object, usize)> {
    // static FormMenu Create(RectTransform parent, string name)
    let create = form_method(None, &["UnityEngine.RectTransform", "System.String"])?;
    let form = il2cpp::invoke(create, null_mut(), &mut [parent as *mut c_void, il2cpp::string("Mods") as *mut c_void])?;
    if form.is_null() {
        return Err("FormMenu create returned null".into());
    }
    let accordion = form_method(Some("Accordion"), &["System.String", "System.Action", "System.Boolean"])?;
    // Match the Options pages: the form's default style is built for big windows (70 px rows);
    // the Options rows are ~40 px apart with small text. One scale factor shrinks it all.
    if let Ok(style) = unity::field_obj(form, "localStyle") {
        if !style.is_null() {
            for (name, v) in [("scale", 0.56f32), ("paddingLeft", 0.0), ("paddingRight", 0.0), ("paddingTop", 0.0)] {
                if let Some(f) = il2cpp::field((api().object_get_class)(style), name) {
                    let mut v = v;
                    (api().field_set_value)(style, f, &mut v as *mut f32 as _);
                }
            }
        }
    }

    let mut made = 0;
    let mut todo = vec![];
    {
        let mut controls = CONTROLS.lock().unwrap();
        let mut sections = SECTIONS.lock().unwrap();
        controls.clear();
        sections.clear();
        for (pid, info) in plugins() {
            let id = info["id"].as_str().unwrap_or("mod").to_string();
            let name = info["name"].as_str().unwrap_or("Mod").to_string();
            let values = call(pid, &json!({"cmd": "plugin.get"}))["values"].clone();
            for e in info["settings"].as_array().cloned().unwrap_or_default() {
                let key = e["key"].as_str().unwrap_or_default().to_string();
                let kind = match e["type"].as_str() {
                    Some("toggle") => Kind::Toggle,
                    Some("slider") => Kind::Slider,
                    Some("choice") => Kind::Choice(e["options"].as_array().map(|o| o.iter().filter_map(|s| s.as_str().map(String::from)).collect()).unwrap_or_default()),
                    _ => continue, // key bindings: not in the menu yet
                };
                let value = values.get(&key).cloned().unwrap_or_else(|| e["default"].clone());
                let range = (e["min"].as_f64().unwrap_or(0.0) as f32, e["max"].as_f64().unwrap_or(1.0) as f32);
                let label = e["label"].as_str().unwrap_or(&key).to_string();
                controls.push(Control { plugin: pid, key, kind, row: label, value, range });
                made += 1;
            }
            todo.push((sections.len(), name.clone()));
            sections.push(Section { id, name, open: false, plugin: pid });
        }
    }
    // no lock held from here: the form may call back into us while we build it
    for (section, name) in todo {
        let build = arg_delegate(accordion, 1, on_section as *const () as usize, section)?;
        let mut open = false;
        il2cpp::invoke(accordion, form as *mut c_void, &mut [il2cpp::string(&name) as *mut c_void, build as *mut c_void, &mut open as *mut bool as _])?;
    }
    Ok((form, made))
}

/// The game fills an Accordion through this callback (when the section is built or opened): add
/// that plugin's rows. `id` = section index.
unsafe extern "C" fn on_section(id: usize, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| unsafe {
        if let Err(e) = fill_section(id) {
            log::info(format!("mods menu: section {id}: {e}"));
        }
    });
}

unsafe fn fill_section(section: usize) -> R<()> {
    log::info(format!("mods menu: filling section {section}"));
    let form = our_form()?;
    let pid = SECTIONS.lock().unwrap().get(section).map(|s| s.plugin).ok_or("no such section")?;
    // copy what we need; no lock held while calling the game
    let rows: Vec<(usize, String, Kind, (f32, f32), Value)> = CONTROLS
        .lock()
        .unwrap()
        .iter()
        .enumerate()
        .filter(|(_, c)| c.plugin == pid)
        .map(|(i, c)| {
            let kind = match &c.kind {
                Kind::Toggle => Kind::Toggle,
                Kind::Choice(o) => Kind::Choice(o.clone()),
                Kind::Slider => Kind::Slider,
            };
            (i, c.row.clone(), kind, c.range, c.value.clone())
        })
        .collect();
    let slider = form_method(Some("Slider"), &["System.String", "System.Single", "System.Single", "System.Func<System.Single>", "System.Action<System.Single>", "System.Single", "System.Int32"])?;
    let toggle = form_method(None, &["System.String", "System.Func<System.Boolean>", "System.Action<System.Boolean>", "System.Boolean"])?;
    let choice = form_method(None, &["System.String", "System.String[]", "System.Func<System.Int32>", "System.Action<System.Int32>", "System.Int32"])?;
    for (cid, label, kind, (lo, hi), value) in rows {
        let text = il2cpp::string(&label) as *mut c_void;
        match kind {
            Kind::Toggle => {
                let (get, set) = (arg_delegate(toggle, 1, get_bool as *const () as usize, cid)?, arg_delegate(toggle, 2, on_bool as *const () as usize, cid)?);
                let mut init = value.as_bool().unwrap_or(false);
                il2cpp::invoke(toggle, form as *mut c_void, &mut [text, get as _, set as _, &mut init as *mut bool as _])?;
            }
            Kind::Slider => {
                let (get, set) = (arg_delegate(slider, 3, get_float as *const () as usize, cid)?, arg_delegate(slider, 4, on_float as *const () as usize, cid)?);
                let (mut lo, mut hi, mut init, mut digits) = (lo, hi, value.as_f64().unwrap_or(0.0) as f32, 2i32);
                il2cpp::invoke(slider, form as *mut c_void, &mut [text, &mut lo as *mut f32 as _, &mut hi as *mut f32 as _, get as _, set as _, &mut init as *mut f32 as _, &mut digits as *mut i32 as _])?;
            }
            Kind::Choice(opts) => {
                let sk = unity::class("mscorlib.dll", "System", "String")?;
                let arr = (api().array_new)(sk, opts.len());
                for (i, o) in opts.iter().enumerate() {
                    *il2cpp::array_data::<*mut Object>(arr).add(i) = il2cpp::string(o);
                }
                let (get, set) = (arg_delegate(choice, 2, get_int as *const () as usize, cid)?, arg_delegate(choice, 3, on_int as *const () as usize, cid)?);
                let mut init = opts.iter().position(|o| Some(o.as_str()) == value.as_str()).unwrap_or(0) as i32;
                il2cpp::invoke(choice, form as *mut c_void, &mut [text, arr as _, get as _, set as _, &mut init as *mut i32 as _])?;
            }
        }
    }
    Ok(())
}

/// Current value of a control, for the form's live getters (called often: cached, no plugin call).
/// `try_lock`: a getter running while our own code holds the lock just reads a default once.
fn current(id: usize) -> Option<(Value, Option<Vec<String>>)> {
    let c = CONTROLS.try_lock().ok()?;
    let ctl = c.get(id)?;
    let opts = if let Kind::Choice(o) = &ctl.kind { Some(o.clone()) } else { None };
    Some((ctl.value.clone(), opts))
}

unsafe extern "C" fn get_bool(id: usize, _m: *const c_void) -> bool {
    std::panic::catch_unwind(|| current(id).and_then(|(v, _)| v.as_bool()).unwrap_or(false)).unwrap_or(false)
}

unsafe extern "C" fn get_float(id: usize, _m: *const c_void) -> f32 {
    std::panic::catch_unwind(|| current(id).and_then(|(v, _)| v.as_f64()).unwrap_or(0.0) as f32).unwrap_or(0.0)
}

unsafe extern "C" fn get_int(id: usize, _m: *const c_void) -> i32 {
    std::panic::catch_unwind(|| match current(id) {
        Some((v, Some(opts))) => opts.iter().position(|s| Some(s.as_str()) == v.as_str()).unwrap_or(0) as i32,
        Some((v, None)) => v.as_i64().unwrap_or(0) as i32,
        None => 0,
    })
    .unwrap_or(0)
}


/// Developer test driver (developer build only): `{"cmd":"modmenu","open":true}` opens Options
/// from the main menu and selects the Mods tab; `{"cmd":"modmenu","fold":0}` clicks section 0's
/// header; `{"cmd":"modmenu","build":true}` (or no arguments) rebuilds the tab.
pub fn test(req: &Value) -> R<Value> {
    unsafe {
        if req.get("open").is_some() {
            let mk = unity::class("Assembly-CSharp.dll", "", "MainMenu")?;
            let mm = *unity::find_objects(mk)?.first().ok_or("main menu not open")?;
            let options = unity::field_obj(mm, "toz").ok().filter(|o| !o.is_null()).ok_or("options window not found")?;
            let ui_window = unity::class("Assembly-CSharp.dll", "", "UIWindow")?;
            let close = il2cpp::method_sig(ui_window, "Close", &["UIWindow", "System.Boolean"]).ok_or("UIWindow.Close missing")?;
            let mut instant = true;
            il2cpp::invoke(close, mm as *mut c_void, &mut [options as *mut c_void, &mut instant as *mut bool as _])?;
            // the Mods tab is the last one
            let content = find(ui_root()?, OPTIONS);
            let tm = component(find(content, "TabMenu"), "TabMenu")?;
            let n = unity::array_items({
                let f = field_by_type(tm, "Button[]")?;
                let mut v: *mut Object = null_mut();
                (api().field_get_value)(tm, f, &mut v as *mut _ as _);
                v
            })
            .len();
            if req.get("select").and_then(Value::as_bool) != Some(false) {
                // switch away first: after a rebuild the TabMenu may think our (new) index is already open
                on_tab(0, null_mut(), std::ptr::null());
                on_tab(n.saturating_sub(1), null_mut(), std::ptr::null());
            }
            return Ok(json!({"ok": true, "tab": n.saturating_sub(1)}));
        }
        if req.get("tabs").is_some() {
            // test: the tab buttons' state, to compare ours with the game's
            let row = find(find(ui_root()?, OPTIONS), "TabMenu/Tabs");
            let mut out = serde_json::Map::new();
            for c in children(row) {
                let n = unity::name(go_of(c)?);
                let b = component(c, "Button")?;
                let img = component(c, "Image").ok().and_then(|i| unity::callv(i, "get_color", &mut []).ok()).map(|o| il2cpp::unbox::<[f32; 4]>(o));
                out.insert(n, json!({"button": crate::probe::describe(b, 1), "image": img}));
            }
            return Ok(Value::Object(out));
        }
        if let Some(how) = req.get("layout").and_then(Value::as_str) {
            let list = our_list()?;
            match how {
                "gwp" => {
                    unity::callv(component(list, "NavigatableMenu")?, "gwp", &mut [])?;
                }
                "refresh" => {
                    let form = our_form()?;
                    for m in ["Refresh", "Apply"] {
                        let r = unity::callv(form, m, &mut []);
                        log::info(format!("mods menu test: {m}: {:?}", r.err()));
                    }
                }
                "reenable" => {
                    set_active(list, false);
                    set_active(list, true);
                }
                _ => return Err("layout: gwp | reenable".into()),
            }
            return Ok(json!({"ok": true}));
        }
        if let Some(i) = req.get("expand").and_then(Value::as_u64) {
            // test: open accordion `i` the way a click does (FormMenu's (rg, bool) open/close)
            let form = our_form()?;
            let rows_f = field_by_type(form, "System.Collections.Generic.List<qj>")?;
            let mut rows: *mut Object = null_mut();
            (api().field_get_value)(form, rows_f, &mut rows as *mut _ as _);
            let items = unity::field_obj(rows, "_items")?;
            let row = *unity::array_items(items).get(i as usize).ok_or("no such row")?;
            let open = form_method(None, &["rg", "System.Boolean"])?;
            let mut yes = true;
            il2cpp::invoke(open, form as *mut c_void, &mut [row as *mut c_void, &mut yes as *mut bool as _])?;
            return Ok(json!({"ok": true}));
        }
        if let Some(i) = req.get("fold").and_then(Value::as_u64) {
            on_header(i as usize, null_mut(), std::ptr::null());
            return Ok(json!({"ok": true}));
        }
        build(req)
    }
}
