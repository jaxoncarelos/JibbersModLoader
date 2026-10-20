//! The game's own events: subscribe to `PlayerSpawnEvent`, `MapLoadEvent`, `TrickPerformedEvent`
//! and the rest by symbol name, through the game's event bus (`events.bus`). No hook slot is used.
//!
//! ```ignore
//! fn init() {
//!     host::pump::later("my_mod", Duration::from_secs(60), || unsafe {
//!         events::subscribe("event.trick_performed", on_trick)?;
//!         Ok(())
//!     });
//! }
//!
//! fn on_trick(ev: &events::Event) -> Result<(), String> {
//!     let name = unsafe { ev.string("event.trick_performed.name") }?;
//!     log::info(format!("trick: {name}"));
//!     Ok(())
//! }
//! ```
//!
//! The generated bindings give each event a typed view: `game::event::TrickPerformed::subscribe(|t|
//! { log::info(format!("{}", unsafe { t.name() }?)); Ok(()) })`.
//!
//! Callbacks run on the main thread, inside the game's dispatch: keep them short, and do not
//! subscribe or unsubscribe from inside one (the game is iterating its listener list). An `Err` or a
//! panic is logged once per subscription and the subscription keeps running.
//!
//! The SDK removes every subscription of a plugin after its `Plugin::unload`, so the game never
//! calls into an unloaded DLL. Event symbols are listed in docs/SYMBOLS-REFERENCE.md (`event.*`).

use crate::delegates;
use crate::il2cpp::{self, api, Class, Object};
use crate::symbols;
use crate::unity::{self, R};
use std::ffi::c_void;
use std::sync::Mutex;

/// A received event. Read its fields with the event symbol's member paths.
pub struct Event {
    /// The event's id (`event.trick_performed`).
    pub id: &'static str,
    /// The struct's data (no object header).
    pub data: *const u8,
    pub class: *mut Class,
}

impl Event {
    unsafe fn offset(&self, path: &str) -> R<usize> {
        let f = symbols::field(path)?;
        // Value-type field offsets count the absent object header.
        (api().field_get_offset)(f).checked_sub(16).ok_or_else(|| format!("{path}: bad offset"))
    }

    /// A value field (`i32`, `bool`, `f32`, a `#[repr(C)]` struct, ...).
    pub unsafe fn get<T: Copy>(&self, path: &str) -> R<T> {
        Ok(std::ptr::read_unaligned(self.data.add(self.offset(path)?) as *const T))
    }

    /// An object-reference field.
    pub unsafe fn get_obj(&self, path: &str) -> R<*mut Object> {
        self.get::<*mut Object>(path)
    }

    /// A string field.
    pub unsafe fn string(&self, path: &str) -> R<String> {
        Ok(il2cpp::read_string(self.get_obj(path)?))
    }

    /// The whole event as JSON (for logs).
    pub unsafe fn describe(&self, depth: u32) -> serde_json::Value {
        crate::inspect::describe_slot((api().class_get_type)(self.class), self.data, depth)
    }
}

/// An event callback: a function or closure.
pub type Callback = std::sync::Arc<dyn Fn(&Event) -> Result<(), String> + Send + Sync>;

/// Handle to one subscription.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Subscription(usize);

struct Sub {
    id: &'static str,
    class: *mut Class,
    size: usize,
    cb: Callback,
    list: isize,
    delegate: isize,
    failed: bool,
    live: bool,
}

// Raw IL2CPP pointers; only used on the main thread.
unsafe impl Send for Sub {}

static SUBS: Mutex<Vec<Sub>> = Mutex::new(Vec::new());
static OWNER: Mutex<isize> = Mutex::new(0);

/// Called by the game for every subscriber of every event type: `ctx` is the subscription index.
unsafe extern "C" fn trampoline(ctx: usize, arg: usize, _method: *const c_void) {
    let Some((id, class, size, cb)) = SUBS.lock().ok().and_then(|s| s.get(ctx).filter(|s| s.live).map(|s| (s.id, s.class, s.size, s.cb.clone()))) else {
        return;
    };
    let copy = arg.to_ne_bytes();
    let data = if delegates::struct_in_register(size) { copy.as_ptr() } else { arg as *const u8 };
    let ev = Event { id, data, class };
    let err = match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| cb(&ev))) {
        Ok(Ok(())) => return,
        Ok(Err(e)) => e,
        Err(_) => "panic".into(),
    };
    if let Ok(mut s) = SUBS.lock() {
        if let Some(sub) = s.get_mut(ctx) {
            if !std::mem::replace(&mut sub.failed, true) {
                crate::log::info(format!("events: {id} callback failed: {err} (further failures not logged)"));
            }
        }
    }
}

unsafe fn owner() -> R<*mut Object> {
    let mut h = OWNER.lock().map_err(|_| "events state poisoned")?;
    if *h == 0 {
        *h = unity::gc_handle(delegates::new_owner()?)?;
    }
    unity::gc_target(*h)
}

/// The bus's listener list for event class `k`, created if the game has none yet.
unsafe fn listeners(k: *mut Class) -> R<*mut Object> {
    let dict = symbols::get_static_obj("events.bus.handlers")?;
    if dict.is_null() {
        return Err("event bus not initialised yet".into());
    }
    let key = delegates::type_of(k) as *mut c_void;
    let has = unity::callv(dict, "ContainsKey", &mut [key])?;
    if !has.is_null() && il2cpp::unbox::<bool>(has) {
        return unity::callv(dict, "get_Item", &mut [key]);
    }
    let value_k = *delegates::generic_args((api().object_get_class)(dict))?.get(1).ok_or("bus dictionary has no value type")?;
    let list = (api().object_new)(value_k);
    unity::callv(list, ".ctor", &mut [])?;
    unity::callv(dict, "set_Item", &mut [key, list as *mut c_void])?;
    Ok(list)
}

/// Subscribes `cb` to the event symbol `id` (`event.player_spawn`). Main thread only, and not from
/// inside an event callback.
pub unsafe fn subscribe(id: &'static str, cb: impl Fn(&Event) -> Result<(), String> + Send + Sync + 'static) -> R<Subscription> {
    let cb: Callback = std::sync::Arc::new(cb);
    let k = symbols::class(id)?;
    if !(api().class_is_valuetype)(k) {
        return Err(format!("{id} is not an event struct"));
    }
    let list = listeners(k)?;
    let ctx = SUBS.lock().map_err(|_| "events state poisoned")?.len();
    let d = delegates::native(delegates::action_of(k)?, trampoline as *const c_void, ctx)?;
    // List<(object owner, Delegate handler)>.Add: the tuple is passed as a pointer to its data.
    let mut tuple: [*mut Object; 2] = [owner()?, d];
    unity::callv(list, "Add", &mut [tuple.as_mut_ptr() as *mut c_void])?;
    let sub = Sub { id, class: k, size: delegates::value_size(k), cb, list: unity::gc_handle(list)?, delegate: unity::gc_handle(d)?, failed: false, live: true };
    SUBS.lock().map_err(|_| "events state poisoned")?.push(sub);
    Ok(Subscription(ctx))
}

unsafe fn remove_from_list(list: *mut Object, d: *mut Object) -> R<()> {
    let n = il2cpp::unbox::<i32>(unity::callv(list, "get_Count", &mut [])?);
    for i in (0..n).rev() {
        let mut idx = i;
        let item = unity::callv(list, "get_Item", &mut [&mut idx as *mut i32 as *mut c_void])?;
        let pair = il2cpp::unbox::<[*mut Object; 2]>(item);
        if pair[1] == d {
            unity::callv(list, "RemoveAt", &mut [&mut idx as *mut i32 as *mut c_void])?;
        }
    }
    Ok(())
}

/// Removes one subscription. Main thread only, not from inside an event callback.
pub unsafe fn unsubscribe(s: Subscription) -> R<()> {
    let (list, d) = {
        let mut subs = SUBS.lock().map_err(|_| "events state poisoned")?;
        let sub = subs.get_mut(s.0).ok_or("unknown subscription")?;
        if !std::mem::replace(&mut sub.live, false) {
            return Ok(());
        }
        (std::mem::take(&mut sub.list), std::mem::take(&mut sub.delegate))
    };
    let r = remove_from_list(unity::gc_target(list)?, unity::gc_target(d)?);
    let _ = unity::gc_free(list);
    let _ = unity::gc_free(d);
    r
}

/// Removes every subscription of this plugin. Main thread only. The SDK calls it after
/// `Plugin::unload`.
pub unsafe fn unsubscribe_all() -> R<()> {
    let n = SUBS.lock().map_err(|_| "events state poisoned")?.len();
    let mut first_err = Ok(());
    for i in 0..n {
        if let Err(e) = unsubscribe(Subscription(i)) {
            if first_err.is_ok() {
                first_err = Err(e);
            }
        }
    }
    first_err
}

/// Whether this plugin has any live subscription (decides whether unload needs the main thread).
pub fn any() -> bool {
    SUBS.lock().map(|s| s.iter().any(|s| s.live)).unwrap_or(false)
}

/// Sends an event to every listener on the game's bus, as the game's own dispatch does. `data`
/// is the struct's bytes (zeroed when empty). Main thread only.
///
/// Only listeners run: whatever else the game does when it raises that event does not happen.
/// Prefer the game's own actions; use this for events the game raises only from debug input.
pub unsafe fn raise(id: &str, data: &[u8]) -> R<usize> {
    let k = symbols::class(id)?;
    let size = delegates::value_size(k);
    if data.len() > size {
        return Err(format!("{id} is {size} bytes, got {}", data.len()));
    }
    let mut buf = vec![0u8; size.max(8)];
    buf[..data.len()].copy_from_slice(data);
    let list = listeners(k)?;
    let n = il2cpp::unbox::<i32>(unity::callv(list, "get_Count", &mut [])?);
    let mut sent = 0;
    for i in 0..n {
        let mut idx = i;
        let item = unity::callv(list, "get_Item", &mut [&mut idx as *mut i32 as *mut c_void])?;
        let pair = il2cpp::unbox::<[*mut Object; 2]>(item);
        // runtime_invoke takes a pointer to a value-type argument.
        delegates::invoke(pair[1], &mut [buf.as_mut_ptr() as *mut c_void])?;
        sent += 1;
    }
    Ok(sent)
}

/// Number of listeners on the bus for an event (the game's and every plugin's). Main thread only.
pub unsafe fn listener_count(id: &str) -> R<usize> {
    let list = listeners(symbols::class(id)?)?;
    Ok(il2cpp::unbox::<i32>(unity::callv(list, "get_Count", &mut [])?) as usize)
}
