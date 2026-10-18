//! Reversible Rewired gameplay binding masks and raw west-face button polling.
//!
//! MAIN THREAD ONLY, including `restore` before plugin unload. Call `update` each
//! frame before consuming Reshred input. No bindings are deleted or reassigned.
//! Unknown categories/controllers fail closed; `info` inventories them read-only.

use crate::il2cpp::{self, api, Class, Method, Object};
use crate::unity::{self, R};
use serde_json::{json, Value};
use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::ffi::c_void;
use std::ptr::null_mut;
use std::time::{Duration, Instant};

const IMAGE: &str = "Rewired_Core.dll";
const RESCAN: Duration = Duration::from_millis(750);
const MAX_ITEMS: usize = 16384;

/// Never use the legacy u32 IL2CPP GC handles (Unity 6 truncates them).
struct Root(isize);

impl Root {
    unsafe fn new(object: *mut Object) -> R<Self> {
        if object.is_null() {
            return Err("Rewired returned a null object".into());
        }
        Ok(Self(unity::gc_handle(object)?))
    }

    unsafe fn get(&self) -> R<*mut Object> {
        let object = unity::gc_target(self.0)?;
        if object.is_null() {
            Err("Rewired GCHandle has no target".into())
        } else {
            Ok(object)
        }
    }
}

impl Drop for Root {
    fn drop(&mut self) {
        // All construction, use and destruction of these roots is main-thread-only.
        unsafe { let _ = unity::gc_free(self.0); }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Config {
    key: u32,
    controller: bool,
}

struct Mask {
    element: Root,
    original: bool,
    active: bool,
}

struct Pad {
    controller: Root,
    button: Root,
    element_id: i32,
    button_index: usize,
    name: String,
    identity: Value,
}

#[derive(Default)]
struct State {
    config: Option<Config>,
    masks: Vec<Mask>,
    pads: Vec<Pad>,
    scanned: Option<Instant>,
    scan_error: Option<String>,
    last_error: Option<String>,
}

// No raw managed object pointers persist between calls. Metadata pointers alone
// are cached for the lifetime of the loaded IL2CPP domain (not managed objects).
type MethodKey = (usize, String, usize, String);
thread_local! {
    static STATE: RefCell<State> = RefCell::new(State::default());
    static METHODS: RefCell<HashMap<MethodKey, *const Method>> = RefCell::new(HashMap::new());
}

/// Exact name first, explicit-interface suffix second, inherited methods included.
/// Return-type selection is essential for non-generic GetAllMaps/GetEnumerator.
unsafe fn resolve(k: *const Class, name: &str, argc: usize, ret: &str) -> R<*const Method> {
    let key = (k as usize, name.to_owned(), argc, ret.to_owned());
    if let Some(m) = METHODS.with(|cache| cache.borrow().get(&key).copied()) {
        return Ok(m);
    }
    let suffix = format!(".{name}");
    let mut explicit = None;
    let mut cur = k;
    while !cur.is_null() {
        let mut iter = null_mut();
        loop {
            let m = (api().class_get_methods)(cur, &mut iter);
            if m.is_null() { break; }
            if (api().method_get_param_count)(m) as usize != argc { continue; }
            let actual = il2cpp::cstr((api().method_get_name)(m));
            if actual != name && !actual.ends_with(&suffix) { continue; }
            let result = il2cpp::type_name((api().method_get_return_type)(m));
            if (!ret.is_empty() && result != ret) || result.contains("<T>") { continue; }
            if actual == name {
                METHODS.with(|cache| { cache.borrow_mut().insert(key, m); });
                return Ok(m);
            }
            explicit.get_or_insert(m);
        }
        cur = (api().class_get_parent)(cur);
    }
    if let Some(m) = explicit {
        METHODS.with(|cache| { cache.borrow_mut().insert(key, m); });
        Ok(m)
    } else {
        Err(format!("Rewired method {name}/{argc} -> {ret} not found"))
    }
}

unsafe fn call(object: *mut Object, name: &str, args: &mut [*mut c_void], ret: &str) -> R<*mut Object> {
    if object.is_null() { return Err(format!("Rewired {name} on null")); }
    let m = resolve((api().object_get_class)(object), name, args.len(), ret)?;
    il2cpp::invoke(m, object, args).map_err(|e| format!("Rewired {name}: {e}"))
}

/// Only these scalar layouts are read here. In particular, never unbox an
/// arbitrary reference as an integer, or construct a Rust bool from unchecked bytes.
trait Scalar: Copy {
    unsafe fn accepts(k: *const Class) -> bool;
    unsafe fn read(data: *const u8) -> R<Self>;
}

impl Scalar for bool {
    unsafe fn accepts(k: *const Class) -> bool {
        il2cpp::type_name((api().class_get_type)(k)) == "System.Boolean"
    }

    unsafe fn read(data: *const u8) -> R<Self> {
        match *data {
            0 => Ok(false),
            1 => Ok(true),
            _ => Err("Rewired returned an invalid Boolean".into()),
        }
    }
}

impl Scalar for i32 {
    unsafe fn accepts(k: *const Class) -> bool {
        if il2cpp::type_name((api().class_get_type)(k)) == "System.Int32" { return true; }
        // ControllerType, KeyboardKeyCode and ModifierKeyFlags are enums. Check
        // their underlying type rather than assuming every enum occupies 4 bytes.
        let parent = (api().class_get_parent)(k);
        if parent.is_null() || il2cpp::type_name((api().class_get_type)(parent)) != "System.Enum" {
            return false;
        }
        il2cpp::field(k, "value__").is_some_and(|field|
            il2cpp::type_name((api().field_get_type)(field)) == "System.Int32")
    }

    unsafe fn read(data: *const u8) -> R<Self> {
        Ok(std::ptr::read_unaligned(data as *const i32))
    }
}

unsafe fn scalar<T: Scalar>(result: *mut Object, name: &str) -> R<T> {
    if result.is_null() { return Err(format!("Rewired {name} returned null")); }
    let k = (api().object_get_class)(result);
    let mut align = 0;
    if !(api().class_is_valuetype)(k) || !T::accepts(k) ||
        (api().class_value_size)(k as *mut Class, &mut align) != std::mem::size_of::<T>() as i32 {
        return Err(format!("Rewired {name} returned an unexpected scalar type"));
    }
    let data = (api().object_unbox)(result);
    if data.is_null() { return Err(format!("Rewired {name} could not be unboxed")); }
    T::read(data as *const u8)
}

unsafe fn value<T: Scalar>(object: *mut Object, name: &str) -> R<T> {
    scalar(call(object, name, &mut [], "")?, name)
}

/// Validate field metadata before letting IL2CPP write into a pointer-sized slot.
unsafe fn reference_field(object: *mut Object, name: &str) -> R<*mut Object> {
    if object.is_null() { return Err(format!("Rewired field {name} on null")); }
    let field = il2cpp::field((api().object_get_class)(object), name)
        .ok_or_else(|| format!("Rewired field {name} not found"))?;
    let ty = (api().field_get_type)(field);
    let kind = (api().type_get_type)(ty);
    let generic_class = if kind == 0x15 { (api().class_from_type)(ty) } else { null_mut() };
    let reference = matches!(kind, 0x12 | 0x14 | 0x1c | 0x1d) ||
        (!generic_class.is_null() && !(api().class_is_valuetype)(generic_class));
    if !reference || (api().field_get_flags)(field) & 0x10 != 0 {
        return Err(format!("Rewired field {name} is not an instance object reference"));
    }
    let mut result = null_mut();
    (api().field_get_value)(object, field, &mut result as *mut *mut Object as _);
    Ok(result)
}

unsafe fn integer_field(object: *mut Object, name: &str) -> R<i32> {
    if object.is_null() { return Err(format!("Rewired field {name} on null")); }
    let field = il2cpp::field((api().object_get_class)(object), name)
        .ok_or_else(|| format!("Rewired field {name} not found"))?;
    if il2cpp::type_name((api().field_get_type)(field)) != "System.Int32" ||
        (api().field_get_flags)(field) & 0x10 != 0 {
        return Err(format!("Rewired field {name} is not an instance Int32"));
    }
    let mut result = 0i32;
    (api().field_get_value)(object, field, &mut result as *mut i32 as _);
    Ok(result)
}

unsafe fn text(object: *mut Object, name: &str) -> R<String> {
    Ok(il2cpp::read_string(call(object, name, &mut [], "System.String")?))
}

unsafe fn reinput(name: &str) -> R<Root> {
    let k = unity::class(IMAGE, "Rewired", "ReInput")?;
    Root::new(unity::call(k, null_mut(), name, &mut [])?)
}

/// Walk managed IEnumerable, never assume a List/array memory layout. Root the
/// collection, enumerator and all returned objects, including boxed enumerators.
unsafe fn items(collection: *mut Object) -> R<Vec<Root>> {
    let collection = Root::new(collection)?;
    let object = collection.get()?;
    let k = (api().object_get_class)(object);
    // Prefer IEnumerator over generic IEnumerator<T>. Both use explicit method
    // suffix lookup when a concrete collection exposes only interface methods.
    let m = resolve(k, "GetEnumerator", 0, "System.Collections.IEnumerator")
        .or_else(|_| resolve(k, "GetEnumerator", 0, ""))?;
    let enumerator = Root::new(il2cpp::invoke(m, object, &mut [])?)?;
    let object = enumerator.get()?;
    let boxed = (api().class_is_valuetype)((api().object_get_class)(object));
    let this = if boxed { (api().object_unbox)(object) } else { object };
    let next = resolve((api().object_get_class)(object), "MoveNext", 0, "System.Boolean")?;
    let current = resolve((api().object_get_class)(object), "get_Current", 0, "System.Object")
        .or_else(|_| resolve((api().object_get_class)(object), "get_Current", 0, ""))?;
    let result = (|| {
        let mut out = Vec::new();
        loop {
            let more = il2cpp::invoke(next, this, &mut [])?;
            if !scalar::<bool>(more, "MoveNext")? { return Ok(out); }
            if out.len() >= MAX_ITEMS { return Err("Rewired enumeration limit exceeded".into()); }
            let item = il2cpp::invoke(current, this, &mut [])?;
            if !item.is_null() { out.push(Root::new(item)?); }
        }
    })();
    if let Ok(dispose) = resolve((api().object_get_class)(object), "Dispose", 0, "System.Void") {
        let _ = il2cpp::invoke(dispose, this, &mut []);
    }
    result
}

fn normalized(name: &str) -> String {
    name.chars().filter(char::is_ascii_alphanumeric).flat_map(char::to_lowercase).collect()
}

/// Deliberately an allowlist, not "everything except UI". Category IDs are not
/// stable. Unknown/default/combined categories must be reviewed via info first.
/// Offline serialized level0/482 config confirms this build's categories include
/// Ragdoll, UI, Replay, Default, Default - Debug and SpawnSelector. Only Ragdoll
/// from that set is gameplay-owned; don't mask shared/default navigation maps.
fn gameplay_category(name: &str) -> bool {
    matches!(normalized(name).as_str(),
        "gameplay" | "game" | "ingame" | "player" | "playergameplay" |
        "ski" | "skiing" | "skier" | "skiinggameplay" | "ragdoll" |
        "ragdollcontrols" | "ragdollgameplay" | "gameplayragdoll")
}

/// Reshred stores Win32 virtual keys; Rewired.KeyboardKeyCode uses Unity values.
/// Unsupported VKs return None rather than accidentally masking another key.
fn keyboard_code(vk: u32) -> Option<i32> {
    Some(match vk {
        0x41..=0x5a => (vk + 32) as i32,
        0x30..=0x39 => vk as i32,
        0x08 => 8, 0x09 => 9, 0x0d => 13, 0x1b => 27, 0x20 => 32,
        0x2e => 127, 0x26 => 273, 0x28 => 274, 0x27 => 275, 0x25 => 276,
        0x2d => 277, 0x24 => 278, 0x23 => 279, 0x21 => 280, 0x22 => 281,
        0x70..=0x7e => (vk - 0x70 + 282) as i32,
        0x60..=0x69 => (vk - 0x60 + 256) as i32,
        0x6e => 266, 0x6f => 267, 0x6a => 268, 0x6d => 269, 0x6b => 270,
        0xba => 59, 0xbb => 61, 0xbc => 44, 0xbd => 45, 0xbe => 46,
        0xbf => 47, 0xc0 => 96, 0xdb => 91, 0xdc => 92, 0xdd => 93, 0xde => 39,
        _ => return None,
    })
}

fn west_name(name: &str, hardware: &str) -> bool {
    let name = normalized(name);
    let hardware = normalized(hardware);
    if matches!(name.as_str(), "square" | "squarebutton" | "buttonsquare" |
        "west" | "buttonwest" | "westbutton" | "facebuttonwest" |
        "leftfacebutton" | "actionleft" | "xsquare" | "buttonxsquare") {
        return true;
    }
    // Nintendo's X is NORTH. Never infer position from X on unknown devices.
    let xbox = hardware.contains("xbox") || hardware.contains("xinput") ||
        hardware.contains("microsoft");
    xbox && matches!(name.as_str(), "x" | "buttonx" | "xbutton")
}

unsafe fn controller_identity(controller: *mut Object) -> Value {
    json!({
        "id": integer_field(controller, "id").ok(),
        "name": text(controller, "get_name").ok(),
        "hardware_name": text(controller, "get_hardwareName").ok(),
        "hardware_identifier": text(controller, "get_hardwareIdentifier").ok(),
        "connected": value::<bool>(controller, "get_isConnected").ok(),
    })
}

unsafe fn discover_pads(controller: *mut Object) -> R<(Vec<Pad>, Vec<Value>)> {
    if controller.is_null() { return Ok((vec![], vec![])); }
    let identity = controller_identity(controller);
    let hardware = format!("{} {} {}",
        identity["name"].as_str().unwrap_or_default(),
        identity["hardware_name"].as_str().unwrap_or_default(),
        identity["hardware_identifier"].as_str().unwrap_or_default());
    let mut found = Vec::new();
    let mut inventory = Vec::new();
    for (index, button) in items(call(controller, "get_Buttons", &mut [], "")?)?.into_iter().enumerate() {
        let object = button.get()?;
        let identifier = Root::new(call(object, "get_elementIdentifier", &mut [], "")?)?;
        let identifier = identifier.get()?;
        let id = value::<i32>(identifier, "get_id")?;
        let name = text(identifier, "get_name")?;
        let west = west_name(&name, &hardware);
        inventory.push(json!({"element_id": id, "button_index": index, "name": name, "west": west}));
        if west {
            found.push(Pad { controller: Root::new(controller)?, button, element_id: id,
                button_index: index, name, identity: identity.clone() });
        }
    }
    Ok((found, inventory))
}

struct Binding {
    element: Root,
    kind: i32,
    key: i32,
    modifiers: i32,
    west: bool,
}

#[derive(Default)]
struct Inventory {
    bindings: Vec<Binding>,
    pads: Vec<Pad>,
    maps: Vec<Value>,
    controllers: Vec<Value>,
    categories: Vec<Value>,
    controller_errors: Vec<String>,
}

/// Read-only: neither info nor inventory changes the enabled state of any map.
unsafe fn inventory(diagnostic: bool, controllers_enabled: bool) -> R<Inventory> {
    let players = reinput("get_players")?;
    let mapping = reinput("get_mapping")?;
    let mut result = Inventory::default();
    let mut categories = HashMap::new();
    for category in items(call(mapping.get()?, "get_MapCategories", &mut [], "")?)? {
        let object = category.get()?;
        let id = value::<i32>(object, "get_id")?;
        let name = text(object, "get_name")?;
        result.categories.push(json!({"id": id, "name": name, "gameplay": gameplay_category(&name)}));
        categories.insert(id, name);
    }
    let mut seen_controllers = HashSet::new();
    let mut seen_maps = HashSet::new();
    for player in items(call(players.get()?, "get_Players", &mut [], "")?)? {
        let player = player.get()?;
        let player_id = value::<i32>(player, "get_id").ok();
        let controllers = Root::new(reference_field(player, "controllers")?)?;
        let maps = Root::new(reference_field(controllers.get()?, "maps")?)?;
        let maps = call(maps.get()?, "GetAllMaps", &mut [],
            "System.Collections.Generic.IEnumerable<Rewired.ControllerMap>")?;
        for map in items(maps)? {
            let map = map.get()?;
            if !seen_maps.insert(map as usize) { continue; }
            let kind = value::<i32>(map, "get_controllerType")?;
            if kind != 0 && kind != 2 { continue; }
            if kind == 2 && !diagnostic && !controllers_enabled { continue; }
            // A joystick API mismatch must not discard keyboard bindings already
            // found, nor prevent processing keyboard maps later in the collection.
            let map_result = (|| -> R<()> {
                let id = value::<i32>(map, "get_categoryId")?;
                let category = if let Some(name) = categories.get(&id) { name.clone() } else {
                    let mut id_arg = id;
                    let helper = mapping.get()?;
                    let method = il2cpp::method_sig((api().object_get_class)(helper), "GetMapCategory", &["System.Int32"])
                        .ok_or("Rewired GetMapCategory(Int32) not found")?;
                    let object = Root::new(il2cpp::invoke(method, helper, &mut [&mut id_arg as *mut i32 as _])?)?;
                    text(object.get()?, "get_name")?
                };
                let gameplay = gameplay_category(&category);
                let controller = if kind == 2 { call(map, "get_controller", &mut [], "")? } else { null_mut() };
                let controller_root = if controller.is_null() { None } else { Some(Root::new(controller)?) };
                if kind == 2 && !controller.is_null() && seen_controllers.insert(controller as usize) {
                    let (pads, buttons) = discover_pads(controller)?;
                    if diagnostic { result.controllers.push(json!({"identity": controller_identity(controller), "buttons": buttons})); }
                    result.pads.extend(pads);
                }
                // Do not allocate roots for UI/replay bindings during frame updates.
                if !diagnostic && !gameplay { return Ok(()); }
                let west_ids: HashSet<i32> = result.pads.iter().filter(|pad|
                    pad.controller.get().ok() == Some(controller)).map(|pad| pad.element_id).collect();
                let mut elements = Vec::new();
                for element in items(call(map, "get_AllMaps", &mut [], "")?)? {
                    let object = element.get()?;
                    let element_id = if diagnostic || kind == 2 { value::<i32>(object, "get_elementIdentifierId")? } else { 0 };
                    let key = if kind == 0 { value::<i32>(object, "get_keyboardKeyCode")? } else { 0 };
                    let modifiers = if kind == 0 { value::<i32>(object, "get_modifierKeyFlags")? } else { 0 };
                    let west = kind == 2 && west_ids.contains(&element_id);
                    if diagnostic {
                        elements.push(json!({
                            "id": value::<i32>(object, "get_id").ok(),
                            "action_id": value::<i32>(object, "get_actionId").ok(),
                            "action_name": text(object, "get_actionDescriptiveName").ok(),
                            "element_id": element_id,
                            "element_name": text(object, "get_elementIdentifierName").ok(),
                            "keyboard_key_code": key,
                            "modifier_flags": if kind == 0 { Some(modifiers) } else { None },
                            "enabled": value::<bool>(object, "get_enabled").ok(),
                            "west": west,
                        }));
                    }
                    if gameplay { result.bindings.push(Binding { element, kind, key, modifiers, west }); }
                }
                if diagnostic {
                    result.maps.push(json!({"player_id": player_id, "category_id": id, "category": category,
                        "gameplay": gameplay, "controller_type": kind,
                        "controller_id": value::<i32>(map, "get_controllerId").ok(),
                        "enabled": value::<bool>(map, "get_enabled").ok(), "elements": elements}));
                }
                drop(controller_root);
                Ok(())
            })();
            if kind == 2 {
                if let Err(e) = map_result { result.controller_errors.push(e); }
            } else {
                map_result?;
            }
        }
    }
    Ok(result)
}

unsafe fn set_enabled(object: *mut Object, mut enabled: bool) -> R<()> {
    if object.is_null() { return Err("Rewired set_enabled on null".into()); }
    let method = resolve((api().object_get_class)(object), "set_enabled", 1, "System.Void")?;
    if il2cpp::type_name((api().method_get_param)(method, 0)) != "System.Boolean" {
        return Err("Rewired set_enabled parameter is not Boolean".into());
    }
    il2cpp::invoke(method, object, &mut [&mut enabled as *mut bool as _])?;
    Ok(())
}

/// Retain failures for retry rather than discarding the only restoration record.
unsafe fn restore_masks(state: &mut State) -> R<()> {
    let mut failures = Vec::new();
    let mut error = None;
    for mask in state.masks.drain(..) {
        match mask.element.get().and_then(|object| set_enabled(object, mask.original)) {
            Ok(()) => {},
            Err(e) => { error = Some(e); failures.push(mask); }
        }
    }
    state.masks = failures;
    state.pads.clear();
    state.scanned = None;
    state.scan_error = None;
    if let Some(e) = error { Err(format!("Rewired restoration pending: {e}")) } else { Ok(()) }
}

unsafe fn rescan(state: &mut State, config: Config) -> R<()> {
    let scan = inventory(false, config.controller)?;
    let key = keyboard_code(config.key);
    let mut selected = Vec::new();
    let mut desired = HashSet::new();
    for binding in scan.bindings {
        if (binding.kind == 0 && key == Some(binding.key) && binding.modifiers == 0) ||
            (config.controller && binding.kind == 2 && binding.west) {
            desired.insert(binding.element.get()? as usize);
            selected.push(binding);
        }
    }
    // Maps can be removed or rebound without a config change. Restore and
    // release obsolete roots instead of disabling an unrelated new binding
    // forever or retaining every old scene's maps until plugin unload.
    let mut retained = Vec::new();
    let mut restoration_error = None;
    for mut mask in state.masks.drain(..) {
        let object = mask.element.get();
        mask.active = object.as_ref().is_ok_and(|o| desired.contains(&(*o as usize)));
        if mask.active {
            retained.push(mask);
        } else {
            match object.and_then(|o| set_enabled(o, mask.original)) {
                Ok(()) => {},
                Err(e) => { restoration_error = Some(e); retained.push(mask); },
            }
        }
    }
    state.masks = retained;
    let mut seen = HashSet::new();
    for mask in &state.masks { seen.insert(mask.element.get()? as usize); }
    for binding in selected {
        let object = binding.element.get()?;
        if !seen.insert(object as usize) { continue; }
        let original = value::<bool>(object, "get_enabled")?;
        // Save before mutation, including originally-disabled bindings.
        state.masks.push(Mask { element: binding.element, original, active: true });
        set_enabled(object, false)?;
    }
    state.pads = if config.controller { scan.pads } else { vec![] };
    if let Some(e) = restoration_error { return Err(format!("Rewired obsolete map restoration pending: {e}")); }
    if !scan.controller_errors.is_empty() {
        return Err(format!("Rewired controller inventory incomplete: {}", scan.controller_errors.join("; ")));
    }
    Ok(())
}

unsafe fn update_inner(state: &mut State, enabled: bool, config: Config) -> R<()> {
    if !enabled || state.config != Some(config) {
        state.config = None;
        restore_masks(state)?;
        if !enabled { return Ok(()); }
        state.config = Some(config);
    }
    if state.scanned.map_or(true, |last| last.elapsed() >= RESCAN) {
        // Rate limit attempts, not error visibility. Keep returning the same
        // failure during cooldown so both status and the caller's log dedup stay stable.
        state.scanned = Some(Instant::now());
        state.scan_error = rescan(state, config).err();
    }
    // A game context change may re-enable an existing element. Cheap cached
    // lookups reassert the mask immediately, without re-enumerating all maps.
    for mask in &state.masks {
        if !mask.active { continue; }
        let object = mask.element.get()?;
        if value::<bool>(object, "get_enabled")? { set_enabled(object, false)?; }
    }
    match &state.scan_error {
        Some(error) => Err(error.clone()),
        None => Ok(()),
    }
}

/// Maintain gameplay masks. Config changes restore the old mask first. Unsupported
/// VKs leave keyboard bindings untouched; controller discovery remains available.
/// No calls to this function are made by diagnostic research or `info`.
pub unsafe fn update(enabled: bool, key: u32, controller: bool) -> Result<(), String> {
    STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        let result = update_inner(&mut state, enabled, Config { key, controller });
        state.last_error = result.as_ref().err().cloned();
        result
    })
}

/// Call on disable AND before unload, on the main thread while IL2CPP is alive.
/// Best effort; failures remain rooted for a subsequent restore/update retry and
/// are exposed by info(). Never turns originally-disabled mappings on.
pub unsafe fn restore() {
    STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        state.config = None;
        state.last_error = restore_masks(&mut state).err();
    });
}

/// Held-state (not rising edge) of discovered raw west buttons. Reading the raw
/// Button value bypasses disabled ActionElementMaps. GetButton(int) takes an
/// INDEX, not an element identifier ID; never pass a guessed ID to it.
pub unsafe fn pad_down() -> bool {
    STATE.with(|cell| {
        let state = cell.borrow();
        if !state.config.is_some_and(|config| config.controller) { return false; }
        state.pads.iter().any(|pad| {
            let Ok(controller) = pad.controller.get() else { return false; };
            if !value::<bool>(controller, "get_isConnected").unwrap_or(false) { return false; }
            pad.button.get().and_then(|button| value::<bool>(button, "get_value")).unwrap_or(false)
        })
    })
}

/// Cached diagnostics only: never recursively inspect arbitrary Rewired objects.
pub fn status() -> Value {
    STATE.with(|cell| {
        let state = cell.borrow();
        json!({"enabled": state.config.is_some(), "maskedBindings": state.masks.iter().filter(|m| m.active).count(),
            "rawControllers": state.pads.len(), "lastError": state.last_error})
    })
}

/// Read-only live inventory. Includes all keyboard/joystick categories, bindings,
/// modifier flags, Unity/Rewired X=120, hardware identities and discovered indices.
/// Unknown category/controller labels are visible rather than silently guessed.
pub unsafe fn info() -> R<Value> {
    let scan = inventory(true, true);
    STATE.with(|cell| {
        let state = cell.borrow();
        let pads: Vec<Value> = state.pads.iter().map(|pad| json!({
            "controller": pad.identity, "element_id": pad.element_id,
            "button_index": pad.button_index, "name": pad.name,
        })).collect();
        let original: Vec<Value> = state.masks.iter().map(|mask| json!({
            "original_enabled": mask.original, "active": mask.active,
            "element_id": mask.element.get().and_then(|o| value::<i32>(o, "get_elementIdentifierId")).ok(),
        })).collect();
        let mut out = json!({"ok": scan.as_ref().is_ok_and(|s| s.controller_errors.is_empty()), "enabled": state.config.is_some(),
            "key_vk": state.config.map(|c| c.key),
            "keyboard_key_code": state.config.and_then(|c| keyboard_code(c.key)),
            "x_key_code": keyboard_code(0x58), "controller": state.config.map(|c| c.controller),
            "masked_count": state.masks.len(), "original_bindings": original,
            "raw_buttons": pads, "last_error": state.last_error,
            "category_policy": "explicit gameplay/ragdoll allowlist; unknown/UI/replay untouched"});
        match scan {
            Ok(scan) => { out["categories"] = json!(scan.categories); out["maps"] = json!(scan.maps); out["controllers"] = json!(scan.controllers); out["controller_errors"] = json!(scan.controller_errors); },
            Err(e) => { out["inventory_error"] = json!(e); },
        }
        Ok(out)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn converts_virtual_letters_to_unity_keys() {
        assert_eq!(keyboard_code(0x58), Some(120));
        assert_eq!(keyboard_code(0x41), Some(97));
        assert_eq!(keyboard_code(0x70), Some(282));
        assert_eq!(keyboard_code(0), None);
        assert_eq!(keyboard_code(0x10000), None);
    }

    #[test]
    fn leaves_non_gameplay_contexts_untouched() {
        for category in ["Gameplay", "Ragdoll", "Skiing", "In Game"] { assert!(gameplay_category(category)); }
        for category in ["UI", "Replay", "Replay Gameplay", "Menu", "Default", "Default - Debug", "SpawnSelector", "", "Photo Mode"] {
            assert!(!gameplay_category(category));
        }
    }

    #[test]
    fn scan_failure_stays_visible_during_cooldown() {
        let config = Config { key: 0x58, controller: true };
        let error = "Rewired controller inventory incomplete: missing method".to_owned();
        STATE.with(|cell| {
            let mut state = cell.borrow_mut();
            assert!(state.masks.is_empty() && state.pads.is_empty());
            state.config = Some(config);
            state.scanned = Some(Instant::now());
            state.scan_error = Some(error.clone());
        });
        // Empty masks and a fresh scan timestamp exercise the public update and
        // diagnostic state paths without loading IL2CPP or touching any game.
        unsafe {
            for _ in 0..2 {
                assert_eq!(update(true, config.key, config.controller), Err(error.clone()));
                assert_eq!(status()["lastError"], json!(error));
            }
            assert!(update(false, config.key, config.controller).is_ok());
        }
        assert_eq!(status()["lastError"], Value::Null);
        STATE.with(|cell| assert!(cell.borrow().scan_error.is_none()));
    }

    #[test]
    fn checked_scalars_and_nulls_need_no_runtime() {
        unsafe {
            assert!(!<bool as Scalar>::read(&0).unwrap());
            assert!(<bool as Scalar>::read(&1).unwrap());
            assert!(<bool as Scalar>::read(&2).is_err());
            let bytes = 120i32.to_ne_bytes();
            assert_eq!(<i32 as Scalar>::read(bytes.as_ptr()).unwrap(), 120);
            assert!(scalar::<bool>(null_mut(), "test").is_err());
            assert!(reference_field(null_mut(), "controllers").is_err());
            assert!(integer_field(null_mut(), "id").is_err());
            assert!(set_enabled(null_mut(), false).is_err());
        }
    }

    #[test]
    fn never_guesses_x_on_unknown_or_nintendo_controllers() {
        assert!(west_name("X", "Xbox One Controller"));
        assert!(west_name("X Button", "XInput Gamepad"));
        assert!(west_name("Square", "DualShock 4"));
        assert!(west_name("Button West", "Generic Gamepad"));
        assert!(!west_name("X", "Nintendo Switch Pro Controller"));
        assert!(!west_name("X", "Generic Gamepad"));
        assert!(!west_name("Button 2", "Xbox One Controller"));
        assert!(!west_name("Left Stick X", "Xbox One Controller"));
    }

    #[test]
    fn empty_restore_and_poll_need_no_runtime() {
        unsafe { restore(); assert!(!pad_down()); }
    }
}