//! The **Mods** tab in the game's main-menu and phone Options windows: one collapsible section per plugin, holding
//! that plugin's settings.
//!
//! Each Options window is a `TabMenu`: a row of tab buttons
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
use crate::{host, log, symbols};
use serde_json::{json, Value};
use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::Mutex;

const OPTIONS: &[&str] = &[
    "PhoneCanvas/PhoneMenu/OptionsMenu/Content",
    "MainMenu/OptionsMenu/Content",
];
const OURS: &str = "Mod_";
const VERSION_LABEL: &str = "Mod_LoaderVersion";
static VERSION_ERROR: Mutex<Option<String>> = Mutex::new(None);

/// Compiled into the running implementation, never read from the update catalog.
fn loader_version_text() -> String {
    format!("Jibbers Mod Loader v{}", option_env!("JIBBERS_LOADER_VERSION").unwrap_or("development"))
}

/// Main thread only. Parent to MainMenu so the game controls visibility during transitions.
/// Rediscover by name each time; no scene pointers survive a scene unload.
unsafe fn ensure_version_label(ui: *mut Object) -> R<()> {
    let menu = find(ui, "MainMenu");
    if menu.is_null() || !find(menu, VERSION_LABEL).is_null() {
        return Ok(());
    }
    // Clone only the text, retaining the game's font/material without any button callbacks.
    let template = find(menu, "Content/VList/PlayButton/LabelText");
    if template.is_null() { return Ok(()); }
    let label = clone(template, menu, VERSION_LABEL)?;
    let result = (|| -> R<()> {
        for (method, mut value) in [
            ("set_anchorMin", [1.0f32, 0.0]),
            ("set_anchorMax", [1.0, 0.0]),
            ("set_pivot", [1.0, 0.0]),
            ("set_sizeDelta", [420.0, 32.0]),
            ("set_anchoredPosition", [-20.0, 16.0]),
        ] {
            unity::callv(label, method, &mut [value.as_mut_ptr() as _])?;
        }
        let mut scale = [1.0f32; 3];
        unity::callv(label, "set_localScale", &mut [scale.as_mut_ptr() as _])?;
        let tmp = component(label, "TMP_Text")?;
        for method in ["set_enableAutoSizing", "set_raycastTarget", "set_enableWordWrapping"] {
            let mut no = false;
            unity::callv(tmp, method, &mut [&mut no as *mut bool as _])?;
        }
        let mut size = 18.0f32;
        unity::callv(tmp, "set_fontSize", &mut [&mut size as *mut f32 as _])?;
        let mut alignment = 1028i32; // TMPro.TextAlignmentOptions.BottomRight
        unity::callv(tmp, "set_alignment", &mut [&mut alignment as *mut i32 as _])?;
        let mut color = [0.85f32, 0.85, 0.85, 0.9];
        unity::callv(tmp, "set_color", &mut [color.as_mut_ptr() as _])?;
        unity::callv(tmp, "set_text", &mut [il2cpp::string(&loader_version_text()) as _])?;
        unity::callv(label, "SetAsLastSibling", &mut [])?;
        set_active(label, true);
        Ok(())
    })();
    if result.is_err() { let _ = destroy(label); }
    result
}

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
static STORE_IDS: Mutex<Vec<String>> = Mutex::new(Vec::new());
static STORE_SEQUENCE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
static STORE_MESSAGE: Mutex<String> = Mutex::new(String::new());

// ---- talking to plugins through the loader ---------------------------------------------------

unsafe fn take(p: *mut u8, n: usize) -> Value {
    let v = if p.is_null() { Value::Null } else { serde_json::from_slice(std::slice::from_raw_parts(p, n)).unwrap_or(Value::Null) };
    if !p.is_null() {
        (host().free_buf)(p, n);
    }
    v
}

fn has_menu_section(info: &Value) -> bool {
    info.get("ok").and_then(Value::as_bool) == Some(true)
        && info.get("settings").and_then(Value::as_array).is_some()
        && info.get("show_in_menu").and_then(Value::as_bool) != Some(false)
}

fn plugins() -> Vec<(u32, Value)> {
    unsafe {
        let (mut p, mut n) = (null_mut(), 0usize);
        (host().list_plugins)(&mut p, &mut n);
        let list = take(p, n);
        let mut out = vec![];
        for e in list.as_array().cloned().unwrap_or_default() {
            let Some(id) = e.get("id").and_then(Value::as_u64) else { continue };
            let mut info = call(id as u32, &json!({"cmd": "plugin.info"}));
            if has_menu_section(&info) {
                info["unverified"] = Value::Bool(e.get("file").and_then(Value::as_str).is_some_and(crate::core::is_unverified));
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

/// Hotkeys can change settings while the phone is closed. Read them again on entry, without
/// holding our locks across plugin calls (a plugin may run game callbacks).
fn sync_values() -> bool {
    let mut changed = false;
    let ids: Vec<u32> = SECTIONS.lock().unwrap().iter().map(|s| s.plugin).collect();
    for id in ids {
        let reply = call(id, &json!({"cmd": "plugin.get"}));
        if let Some(values) = reply.get("values").and_then(Value::as_object) {
            for ctl in CONTROLS.lock().unwrap().iter_mut().filter(|c| c.plugin == id) {
                if let Some(value) = values.get(&ctl.key) {
                    changed |= ctl.value != *value;
                    ctl.value = value.clone();
                }
            }
        }
    }
    changed
}

// ---- Unity helpers -----------------------------------------------------------------------------

/// The current scene's Options content (phone in a map, main menu otherwise).
pub(crate) unsafe fn options(ui: *mut Object) -> *mut Object {
    OPTIONS.iter().map(|path| find(ui, path)).find(|tr| !tr.is_null()).unwrap_or(null_mut())
}

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

/// The cursor searches these cached rows, independently of TabMenu's button/page arrays.
unsafe fn navigation_items(menu: *mut Object) -> R<Vec<*mut Object>> {
    unsafe fn list_items(list: *mut Object) -> R<Vec<*mut Object>> {
        let n = unity::field_val::<i32>(list, "_size")?;
        let items = unity::array_items(unity::field_obj(list, "_items")?);
        if n < 0 || n as usize > items.len() {
            return Err("invalid navigation list size".into());
        }
        Ok(items.into_iter().take(n as usize).collect())
    }
    let rows = symbols::get_obj(menu, "ui.navigatable_menu.rows")?;
    let mut items = Vec::new();
    for row in list_items(rows)? {
        items.extend(list_items(row)?);
    }
    Ok(items)
}

/// Recollect the row after adding/removing a tab. Layout alone leaves its cursor cache stale.
unsafe fn refresh_tab_row(tab_row: *mut Object, added: Option<*mut Object>) -> R<()> {
    let row = component(tab_row, "NavigatableMenu")?;
    symbols::call(row, "ui.navigatable_menu.layout", &mut [])?;
    symbols::call(row, "ui.navigatable_menu.collect", &mut [])?;
    if let Some(button) = added {
        // A clone of an initialized widget can miss the collection event. Register it through the
        // game's own API, which also assigns its parent menu and its position in the cached rows.
        if !navigation_items(row)?.contains(&button) {
            symbols::call(row, "ui.navigatable_menu.add_button", &mut [button as *mut c_void])?;
        }
        let items = navigation_items(row)?;
        if !items.contains(&button) {
            return Err("Mods tab missing from cursor navigation".into());
        }
        log::info(format!("mods menu: tab cursor navigation has {} buttons (Mods registered)", items.len()));
    }
    Ok(())
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
        let content = options(ui);
        let tabs = find(content, "TabMenu");
        let Ok(tab_menu) = component(tabs, "TabMenu") else { return };
        // A layout/readiness error before cloning anything must not change the player's tab.
        if [find(tabs, "Tabs"), find(tabs, "Windows")].into_iter().all(|parent| {
            children(parent).into_iter().all(|c| !unity::name(go_of(c).unwrap_or(null_mut())).starts_with(OURS))
        }) {
            return;
        }
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
            // Force the game's switch routine to restore both the visible page and navigation.
            // Merely resetting the index leaves an empty window if Mods was open at teardown.
            let mut none = -1i32;
            (a.field_set_value)(tab_menu, f, &mut none as *mut i32 as _);
        }
        if let Some(m) = tab_switch(tab_menu) {
            let (mut first, mut instant, mut focus) = (0i32, true, false);
            let _ = il2cpp::invoke(m, tab_menu as *mut c_void, &mut [&mut first as *mut i32 as _, &mut instant as *mut bool as _, &mut focus as *mut bool as _]);
        }
        for parent in [find(tabs, "Tabs"), find(tabs, "Windows")] {
            for c in children(parent) {
                if unity::name(go_of(c).unwrap_or(null_mut())).starts_with(OURS) {
                    let _ = destroy(c);
                }
            }
        }
        let _ = refresh_tab_row(find(tabs, "Tabs"), None);
        let _ = refresh_phone_targets();
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
    log::info(format!("mods menu: Mods tab clicked ({id})"));
    let _ = std::panic::catch_unwind(|| unsafe {
        let Ok(content) = ui_root().map(|ui| options(ui)) else { return };
        let Ok(tm) = component(find(content, "TabMenu"), "TabMenu") else { return };
        sync_values();
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
    let content = options(ui_root()?);
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
    let r = apply(&row, &sections, &controls).and_then(|_| refresh_form());
    APPLYING.store(false, std::sync::atomic::Ordering::Relaxed);
    r
}

/// FormMenu's generated controls live below Content, rather than directly below VList.
/// Refresh their displayed getter values and collect them after an accordion changes layout.
unsafe fn refresh_form() -> R<()> {
    let form = our_form()?;
    unity::callv(form, "Refresh", &mut [])?;
    symbols::call(form, "ui.navigatable_menu.collect", &mut [])?;
    refresh_phone_targets()?;
    Ok(())
}

/// The phone raycasts against its own cached widget array, separate from TabMenu navigation.
/// New tabs and generated form controls must enter that cache to receive mouse clicks.
unsafe fn refresh_phone_targets() -> R<()> {
    let phone = find(ui_root()?, "PhoneCanvas");
    if phone.is_null() {
        return Ok(());
    }
    let go = go_of(phone)?;
    let component_k = unity::class(unity::CORE, "UnityEngine", "Component")?;
    let components = unity::callv(go, "GetComponents", &mut [unity::type_object(component_k) as _])?;
    let mut cache = None;
    for c in unity::array_items(components) {
        if !c.is_null() {
            if let Ok(field) = field_by_type(c, "UIBehaviour[]") {
                cache = Some((c, field));
                break;
            }
        }
    }
    let (owner, field) = cache.ok_or("phone widget cache not found")?;
    let widget_k = unity::class("Assembly-CSharp.dll", "", "UIBehaviour")?;
    let mut inactive = true;
    // Resolve the non-generic overload explicitly; matching only by argument count can select
    // a generic List overload and leave the phone's raycast cache unchanged.
    let get = il2cpp::method_sig((api().object_get_class)(go), "GetComponentsInChildren", &["System.Type", "System.Boolean"])
        .ok_or("GameObject.GetComponentsInChildren(Type, bool) missing")?;
    let widgets = il2cpp::invoke(get, go as _, &mut [unity::type_object(widget_k) as _, &mut inactive as *mut bool as _])?;
    let mut old = null_mut();
    (api().field_get_value)(owner, field, &mut old as *mut _ as _);
    if unity::array_items(old) != unity::array_items(widgets) {
        let items = unity::array_items(widgets);
        let typed = (api().array_new)(widget_k, items.len());
        for (i, item) in items.iter().enumerate() { *il2cpp::array_data::<*mut Object>(typed).add(i) = *item; }
        (api().field_set_value)(owner, field, typed as *mut c_void);
        log::info(format!("mods menu: phone mouse targets refreshed ({} widgets)", items.len()));
    }
    Ok(())
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
                let _ = symbols::set(w, "ui.toggle_button.is_on", value.as_bool().unwrap_or(false));
                // re-render "Label: On/Off"
                if let Err(e) = symbols::call(w, "ui.toggle_button.refresh", &mut []) {
                    log::info(format!("mods menu: {e}"));
                }
                hook(w, "System.Action<System.Boolean>", on_bool as *const () as usize, cid)?;
            }
            Kind::Choice(opts) => {
                let w = component(r, "ArrowSelection")?;
                // the template (respawn hold time) parses its options as numbers; ours are names
                let _ = symbols::set(w, "ui.arrow_selection.is_float", false);
                let sk = unity::class("mscorlib.dll", "System", "String")?;
                let arr = (a.array_new)(sk, opts.len());
                for (i, o) in opts.iter().enumerate() {
                    *il2cpp::array_data::<*mut Object>(arr).add(i) = il2cpp::string(o);
                }
                let mut idx = opts.iter().position(|o| Some(o.as_str()) == value.as_str()).unwrap_or(0) as i32;
                let mut notify = false;
                symbols::call(w, "ui.arrow_selection.set_options", &mut [arr as *mut c_void, &mut idx as *mut i32 as _, &mut notify as *mut bool as _])?;
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
                symbols::call(w, "ui.labeled_slider.set_value", &mut [&mut v as *mut f32 as _, &mut notify as *mut bool as _])?;
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
    let result = unsafe { build_tab() };
    if result.is_err() {
        // Never leave a registered but half-built page: ensure must be able to retry next tick.
        teardown();
    }
    result
}

unsafe fn build_tab() -> R<Value> {
    unsafe {
        let a = api();
        let content = options(ui_root()?);
        if content.is_null() {
            return Err("Options menu is not available yet".into());
        }
        let tabs = find(content, "TabMenu");
        let tab_row = find(tabs, "Tabs");
        let pages = find(tabs, "Windows");
        let gameplay = find(pages, "Gameplay");
        // FormMenu creates its own controls. The phone has no SnowboardToggleButton, so do not
        // require unused main-menu widget templates here.
        if [content, tabs, tab_row, pages, gameplay, find(gameplay, "VList")].iter().any(|p| p.is_null()) {
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
        // UIBehaviour's interactable flag is false on the open tab.
        let idle = children(tab_row).into_iter().find(|c| {
            !unity::name(go_of(*c).unwrap_or(null_mut())).starts_with(OURS)
                && component(*c, "Button").ok().and_then(|w| symbols::get::<bool>(w, "ui.behaviour.interactable").ok()) == Some(true)
        }).ok_or("no unselected Options tab to clone")?;
        let b = clone(idle, tab_row, &format!("{OURS}Tab"))?;
        set_text(b, "LabelText", "Mods");
        let bw = component(b, "Button")?;
        let idle_btn = component(idle, "Button")?;
        // Copy the whole palette, including UIBehaviour's normal/focused/disabled colors. Copying
        // only Button's own Color field (the pressed color) leaves the selected grey behind.
        let mut k = (a.object_get_class)(bw);
        while !k.is_null() {
            let mut it = null_mut();
            loop {
                let f = (a.class_get_fields)(k, &mut it);
                if f.is_null() {
                    break;
                }
                if (a.field_get_flags)(f) & 0x10 == 0 && type_name((a.field_get_type)(f)) == "UnityEngine.Color" {
                    let mut color = [0f32; 4];
                    (a.field_get_value)(idle_btn, f, color.as_mut_ptr() as _);
                    (a.field_set_value)(bw, f, color.as_mut_ptr() as _);
                }
            }
            k = (a.class_get_parent)(k);
        }
        // Unlike writing the backing field, this updates the Image from the current palette too.
        let mut yes = true;
        symbols::call(bw, "ui.behaviour.set_interactable", &mut [&mut yes as *mut bool as _])?;
        hook(bw, "System.Action<Button>", on_tab as *const () as usize, tab_index)?;
        let page = clone(gameplay, pages, &format!("{OURS}Page"))?;
        set_active(page, false);
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
        refresh_tab_row(tab_row, Some(bw))?;
        refresh_phone_targets()?;
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
/// while the scene's Options window isn't there yet. Cheap when
/// the tab is present. Main thread only.
pub fn ensure() -> R<bool> {
    unsafe {
        let Ok(ui) = ui_root() else { return Ok(false) };
        // Cosmetic text must never prevent the Mods tab from being built.
        match ensure_version_label(ui) {
            Ok(()) => *VERSION_ERROR.lock().unwrap() = None,
            Err(e) => {
                if !repeated_error(&mut VERSION_ERROR.lock().unwrap(), &e) {
                    log::info(format!("loader version label: {e}"));
                }
            }
        }
        let tabs = find(options(ui), "TabMenu");
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
            // PhoneCanvas initializes its own cache again when opening/recreating the phone.
            // Repair that independently of setting changes, including after map transitions.
            refresh_phone_targets()?;
            let catalog_sequence = jibbers_distribution::manager::snapshot(&crate::game_dir()).catalog.map(|c| c.sequence).unwrap_or(0);
            if catalog_sequence != STORE_SEQUENCE.load(std::sync::atomic::Ordering::Relaxed)
                && !unity::call_unbox::<bool>(go_of(tabs)?, "get_activeInHierarchy").unwrap_or(false) {
                return build(&Value::Null).map(|_| true);
            }
            // Reopening Options can restore the last tab without clicking its button again.
            let message = jibbers_distribution::manager::message(&crate::game_dir());
            let message_changed = {
                let mut previous = STORE_MESSAGE.lock().unwrap();
                let changed = *previous != message; *previous = message; changed
            };
            if sync_values() || message_changed {
                APPLYING.store(true, std::sync::atomic::Ordering::Relaxed);
                let refreshed = refresh_form();
                APPLYING.store(false, std::sync::atomic::Ordering::Relaxed);
                refreshed?;
            }
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
            let flag = crate::game_dir().join("JibbersMods").join("dump_ui.flag");
            if flag.exists() {
                let _ = std::fs::remove_file(&flag);
                match crate::pump::run(|| unsafe { dump_ui() }).and_then(|r| r) {
                    Ok(n) => log::info(format!("ui dump: {n} objects written")),
                    Err(e) => log::info(format!("ui dump failed: {e}")),
                }
            }
            match crate::pump::run(ensure).and_then(|r| r) {
                Ok(true) => {
                    last_err = None;
                    log::info("mods menu: built for the current scene's Options window");
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

/// Writes the scene's UI hierarchy (every object under the `UI` root, active or not, with its
/// components) to `jibbers_ui_dump.txt` beside the game, so the in-game phone's layout can be
/// matched. Triggered by creating `JibbersMods\dump_ui.flag`. Main thread only.
unsafe fn dump_ui() -> R<usize> {
    const MAX_NODES: usize = 4000;
    const MAX_DEPTH: usize = 12;
    let a = api();
    let root = ui_root()?;
    let mut out = String::new();
    let mut count = 0usize;
    let mut stack: Vec<(*mut Object, usize, String)> = vec![(root, 0, "UI".to_string())];
    while let Some((tr, depth, path)) = stack.pop() {
        if count >= MAX_NODES {
            out.push_str("... truncated\n");
            break;
        }
        count += 1;
        let go = go_of(tr)?;
        let active = unity::call_unbox::<bool>(go, "get_activeSelf").unwrap_or(false);
        let mut comps = Vec::new();
        let mut details = Vec::new();
        let want_fields = path == "UI/PhoneCanvas" || path.contains("/Mod_Page/") || (path.contains("OptionsMenu/Content/TabMenu")
            && (path.ends_with("/TabMenu") || path.ends_with("/Tabs") || path.contains("/Tabs/")));
        if let Ok(comp_k) = unity::class(unity::CORE, "UnityEngine", "Component") {
            if let Ok(all) = unity::callv(go, "GetComponents", &mut [unity::type_object(comp_k) as *mut c_void]) {
                for c in unity::array_items(all) {
                    if !c.is_null() {
                        let n = il2cpp::cstr((a.class_get_name)((a.object_get_class)(c)));
                        if want_fields && matches!(n.as_str(), "Button" | "FormMenu" | "TabMenu" | "HorizontalList" | "Image" | "pe") {
                            details.push(format!("{}  <{n}> {}", "  ".repeat(depth), component_fields(c)));
                            if n == "HorizontalList" || n == "FormMenu" {
                                let names = navigation_items(c).map(|items| items.into_iter().map(|b| unity::name(b)).collect::<Vec<_>>().join(", "));
                                details.push(format!("{}  <cursor navigation> {}", "  ".repeat(depth), names.unwrap_or_else(|e| e)));
                            }
                        }
                        if n != "Transform" && n != "RectTransform" {
                            comps.push(n);
                        }
                    }
                }
            }
        }
        let pos = unity::call_unbox::<[f32; 2]>(tr, "get_anchoredPosition").map(|p| format!(" pos({:.0},{:.0})", p[0], p[1])).unwrap_or_default();
        let size = unity::call_unbox::<[f32; 2]>(tr, "get_sizeDelta").map(|p| format!(" size({:.0},{:.0})", p[0], p[1])).unwrap_or_default();
        out.push_str(&format!("{}{} [{}] {}{pos}{size}\n", "  ".repeat(depth), path.rsplit('/').next().unwrap_or(&path), if active { "on" } else { "off" }, comps.join(",")));
        for d in details {
            out.push_str(&d);
            out.push('\n');
        }
        if depth < MAX_DEPTH {
            for c in children(tr).into_iter().rev() {
                let name = unity::name(go_of(c).unwrap_or(null_mut()));
                stack.push((c, depth + 1, format!("{path}/{name}")));
            }
        }
    }
    std::fs::write(crate::game_dir().join("jibbers_ui_dump.txt"), out).map_err(|e| e.to_string())?;
    Ok(count)
}

/// Field names, types and simple values of a component (and its parents), for the UI dump.
unsafe fn component_fields(c: *mut Object) -> String {
    let a = api();
    let mut parts = Vec::new();
    let mut k = (a.object_get_class)(c);
    while !k.is_null() && parts.len() < 120 {
        let mut it: *mut c_void = null_mut();
        loop {
            let f = (a.class_get_fields)(k, &mut it);
            if f.is_null() {
                break;
            }
            if (a.field_get_flags)(f) & 0x10 != 0 {
                continue;
            }
            let name = il2cpp::cstr((a.field_get_name)(f));
            let ty_ptr = (a.field_get_type)(f);
            let ty = type_name(ty_ptr);
            // field_get_value writes the whole value. Nullable<Color> alone needs 20 bytes;
            // Graphic and other components can contain larger structs as well.
            let fk = (a.class_from_type)(ty_ptr);
            if fk.is_null() {
                continue;
            }
            let value_type = (a.class_is_valuetype)(fk);
            let size = if value_type { (a.class_value_size)(fk, null_mut()).max(0) as usize } else { std::mem::size_of::<usize>() };
            let mut buf = vec![0u8; size.max(16)];
            (a.field_get_value)(c, f, buf.as_mut_ptr() as *mut c_void);
            let value = match ty.as_str() {
                "System.Boolean" => (buf[0] != 0).to_string(),
                "System.Int32" => i32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]).to_string(),
                "System.Single" => f32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]).to_string(),
                "UnityEngine.Color" => {
                    let f = |i: usize| f32::from_le_bytes([buf[i * 4], buf[i * 4 + 1], buf[i * 4 + 2], buf[i * 4 + 3]]);
                    format!("({:.2},{:.2},{:.2},{:.2})", f(0), f(1), f(2), f(3))
                }
                _ if value_type => "-".to_string(),
                _ => {
                    let p = usize::from_le_bytes([buf[0], buf[1], buf[2], buf[3], buf[4], buf[5], buf[6], buf[7]]) as *mut Object;
                    if p.is_null() {
                        "null".to_string()
                    } else if ty.ends_with("[]") {
                        if ty == "UIBehaviour[]" {
                            let mods = unity::array_items(p).into_iter().any(|w| unity::name(w) == "Mod_Tab");
                            format!("[{}; Mods tab={mods}]", (a.array_length)(p))
                        } else {
                            format!("[{}]", (a.array_length)(p))
                        }
                    } else if matches!(ty.as_str(), "UnityEngine.RectTransform" | "UnityEngine.UI.Image" | "NavigatableMenu" | "HorizontalList") {
                        format!("{} ({p:p})", unity::name(p))
                    } else {
                        "set".to_string()
                    }
                }
            };
            parts.push(format!("{name}:{ty}={value}"));
        }
        k = (a.class_get_parent)(k);
    }
    parts.join("; ")
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
    fn host_services_are_hidden_but_managed_and_legacy_mods_stay_visible() {
        assert!(!has_menu_section(&json!({"ok": true, "id": "dotnet", "settings": [], "show_in_menu": false})));
        for sdk in ["dotnet 0.2.0", "0.1.2"] {
            assert!(has_menu_section(&json!({"ok": true, "id": "fpv", "sdk": sdk, "settings": []})));
        }
        assert!(!has_menu_section(&json!({"ok": false, "settings": []})));
        assert!(!has_menu_section(&json!({"ok": true, "settings": null})));
    }

    #[test]
    fn store_warns_about_unverified_mods_by_file_name() {
        assert_eq!(store_text("Up to date".into(), &[]), "Up to date");
        let text = store_text("Up to date".into(), &["JibbersMods/a.dll".into(), "JibbersMods/cs/B/B.dll".into()]);
        assert!(text.starts_with("Up to date\n\nWarning: not from the signed mod catalog: a.dll, B.dll."), "{text}");
    }

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
    let page = find(options(ui_root()?), &format!("TabMenu/Windows/{OURS}Page"));
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
    // The phone's native rows are larger than the compact desktop Options rows.
    let phone = !find(ui_root()?, OPTIONS[0]).is_null();
    if let Ok(style) = unity::field_obj(form, "localStyle") {
        if !style.is_null() {
            let values = [("scale", if phone { 1.0f32 } else { 0.56f32 }), ("paddingLeft", 0.0), ("paddingRight", 0.0), ("paddingTop", 0.0)];
            for (name, v) in values {
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
            let mut name = info["name"].as_str().unwrap_or("Mod").to_string();
            if info["unverified"] == true {
                name.push_str(" (unverified)");
            }
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
    #[cfg(not(target_os = "macos"))]
    {
    let store = arg_delegate(accordion, 1, on_store as *const () as usize, 0)?;
    let mut closed = false;
    il2cpp::invoke(accordion, form as _, &mut [il2cpp::string("Install and update mods") as _, store as _, &mut closed as *mut bool as _])?;
    }
    STORE_SEQUENCE.store(jibbers_distribution::manager::snapshot(&crate::game_dir()).catalog.map(|c| c.sequence).unwrap_or(0), std::sync::atomic::Ordering::Relaxed);
    Ok((form, made))
}

unsafe extern "C" fn on_store(_id: usize, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| unsafe {
        if let Err(error) = fill_store() { log::info(format!("mod catalog menu: {error}")); }
        refresh_soon();
    });
}

unsafe fn fill_store() -> R<()> {
    let form = our_form()?;
    let snap = jibbers_distribution::manager::snapshot(&crate::game_dir());
    let info = form_method(Some("Info"), &["System.Func<System.String>", "System.Boolean", "System.String"])?;
    let status = arg_delegate(info, 0, store_message as *const () as usize, 0)?;
    let mut wrap = true;
    il2cpp::invoke(info, form as _, &mut [status as _, &mut wrap as *mut bool as _, il2cpp::string("") as _])?;
    let button = form_method(Some("Button"), &["System.String", "System.Action"])?;
    let check = arg_delegate(button, 1, store_check as *const () as usize, 0)?;
    il2cpp::invoke(button, form as _, &mut [il2cpp::string("Check for updates") as _, check as _])?;
    let toggle = form_method(None, &["System.String", "System.Func<System.Boolean>", "System.Action<System.Boolean>", "System.Boolean"])?;
    let mut ids = vec![String::new()]; // index 0 is the auto-update preference
    let mut entries = vec![("Automatically update on launch".to_string(), snap.config.auto_update)];
    if let Some(catalog) = snap.catalog {
        for p in catalog.selectable_packages()? {
            let installed = snap.installed.packages.get(&p.id).map(|p| p.version.as_str());
            let state = match installed { Some(v) if v == p.version => "installed", Some(_) => "update available", None => "install" };
            entries.push((format!("{} {} ({state})", p.name, p.version), snap.config.wanted.contains(&p.id)));
            ids.push(p.id.clone());
        }
    }
    *STORE_IDS.lock().unwrap() = ids;
    for (id, (label, mut value)) in entries.into_iter().enumerate() {
        let get = arg_delegate(toggle, 1, store_get as *const () as usize, id)?;
        let set = arg_delegate(toggle, 2, store_set as *const () as usize, id)?;
        il2cpp::invoke(toggle, form as _, &mut [il2cpp::string(&label) as _, get as _, set as _, &mut value as *mut bool as _])?;
    }
    Ok(())
}

unsafe extern "C" fn store_message(_id: usize, _m: *const c_void) -> *mut Object {
    std::panic::catch_unwind(|| il2cpp::string(&store_text(jibbers_distribution::manager::message(&crate::game_dir()), &crate::core::unverified()))).unwrap_or(null_mut())
}
/// The catalog status, then a warning naming every mod DLL that isn't from the signed catalog.
fn store_text(message: String, unverified: &[String]) -> String {
    if unverified.is_empty() { return message; }
    let names: Vec<&str> = unverified.iter().map(|p| p.rsplit('/').next().unwrap_or(p)).collect();
    format!("{message}\n\nWarning: not from the signed mod catalog: {}. These run with full access to your PC; keep them only if you trust where they came from.", names.join(", "))
}
unsafe extern "C" fn store_check(_id: usize, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| jibbers_distribution::manager::check(&crate::game_dir()));
}
unsafe extern "C" fn store_get(id: usize, _m: *const c_void) -> bool {
    std::panic::catch_unwind(|| {
        let key = STORE_IDS.lock().unwrap().get(id).cloned();
        if id == 0 { jibbers_distribution::manager::selected(&crate::game_dir(), None) }
        else { key.is_some_and(|key| jibbers_distribution::manager::selected(&crate::game_dir(), Some(&key))) }
    }).unwrap_or(false)
}
unsafe extern "C" fn store_set(id: usize, value: bool, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| {
        if APPLYING.load(std::sync::atomic::Ordering::Relaxed) { return; }
        let root = crate::game_dir();
        let result = if id == 0 { jibbers_distribution::manager::set_auto_update(&root, value) }
        else {
            let key = STORE_IDS.lock().unwrap().get(id).cloned();
            key.ok_or_else(|| "catalog changed; reopen Mods".to_string()).and_then(|key| jibbers_distribution::manager::select(&root, &key, value))
        };
        if let Err(error) = result { jibbers_distribution::manager::set_message(&root, error); }
        refresh_soon();
    });
}

/// The game fills an Accordion through this callback (when the section is built or opened): add
/// that plugin's rows. `id` = section index.
unsafe extern "C" fn on_section(id: usize, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| unsafe {
        if let Err(e) = fill_section(id) {
            log::info(format!("mods menu: section {id}: {e}"));
        } else {
            // Let the form finish constructing the new widgets before collecting mouse targets.
            refresh_soon();
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
            let mm = *symbols::live("ui.main_menu")?.first().ok_or("main menu not open")?;
            let options_win = symbols::get_obj(mm, "ui.main_menu.options").ok().filter(|o| !o.is_null()).ok_or("options window not found")?;
            let ui_window = unity::class("Assembly-CSharp.dll", "", "UIWindow")?;
            let close = il2cpp::method_sig(ui_window, "Close", &["UIWindow", "System.Boolean"]).ok_or("UIWindow.Close missing")?;
            let mut instant = true;
            il2cpp::invoke(close, mm as *mut c_void, &mut [options_win as *mut c_void, &mut instant as *mut bool as _])?;
            // the Mods tab is the last one
            let content = options(ui_root()?);
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
            let row = find(options(ui_root()?), "TabMenu/Tabs");
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
                "layout" => {
                    symbols::call(component(list, "NavigatableMenu")?, "ui.navigatable_menu.layout", &mut [])?;
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
            // test: open accordion `i` the way a click does (FormMenu's section open/close)
            let form = our_form()?;
            let rows = symbols::get_obj(form, "ui.form_menu.rows")?;
            let items = unity::field_obj(rows, "_items")?;
            let row = *unity::array_items(items).get(i as usize).ok_or("no such row")?;
            let mut yes = true;
            symbols::call(form, "ui.form_menu.set_open", &mut [row as *mut c_void, &mut yes as *mut bool as _])?;
            return Ok(json!({"ok": true}));
        }
        if let Some(i) = req.get("fold").and_then(Value::as_u64) {
            on_header(i as usize, null_mut(), std::ptr::null());
            return Ok(json!({"ok": true}));
        }
        build(req)
    }
}
