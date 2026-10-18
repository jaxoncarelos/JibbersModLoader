//! Reshred: hold X to rewind the rider ~1.5 s, with the exact pose, speed and game state they had then.
//!
//! Physics is MuJoCo (`Mujoco.MjScene`: `Model`/`Data` = mjModel*/mjData*). Before every physics step
//! (pre-hook on `MjScene.FixedUpdate`) the full integration state (`mj_getState`) is copied into a ring
//! buffer covering the last few seconds, together with the plain int/bool state of the game's ragdoll
//! controller objects (crash flags, counters) and the replay clip's frame counters. Rewinding writes an
//! older snapshot back (`mj_setState` + `mj_forward`); the game's own sync then moves the Unity
//! transforms. Restoring the clip's counters drops the rewound stretch from the replay recorder, so saved
//! clips play on continuously.
//!
//! The game's class and field names are obfuscated, so everything is found by shape (field types, method
//! signatures). Input is polled once per frame (pre-hook on `EnvironmentManager.Update`): the keyboard
//! key or XInput X, held for `holdTime` seconds, with a cloned "Respawn" label as the progress indicator.

use crate::il2cpp::{self, api, cstr, type_name, Class, Field, Object};
use crate::unity::{self, R};
use crate::{game_dir, log, modmenu, reshred_input};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::VecDeque;
use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Instant;
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress, LoadLibraryA};
use windows_sys::Win32::System::Threading::GetCurrentProcessId;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
use windows_sys::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};

/// mjtState bits: everything mj_step integrates (mjSTATE_INTEGRATION) except the simulation time,
/// which keeps running forward.
const STATE_SIG: u32 = ((1 << 13) - 1) & !1;
/// Seconds of history kept: how far back the rewind can go.
const HISTORY: f32 = 6.0;
const SETTINGS_FILE: &str = "jibbers_reshred.json";
const GAME_DLL: &str = "Assembly-CSharp.dll";

// ---- settings ----------------------------------------------------------------------------------

#[derive(Clone, Copy, Serialize, Deserialize, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Settings {
    pub enabled: bool,
    /// Windows virtual-key code of the keyboard key (0x58 = X).
    pub key: u32,
    /// Also react to X on an XInput controller.
    pub controller: bool,
    /// Seconds the button must be held before the rewind starts.
    pub hold_time: f32,
    /// How fast time runs backwards while held (1 = real time).
    pub speed: f32,
    /// Show the rewind label while holding.
    pub indicator: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Settings { enabled: true, key: 0x58, controller: true, hold_time: 0.2, speed: 2.0, indicator: true }
    }
}

impl Settings {
    fn sanitized(mut self) -> Self {
        let defaults = Self::default();
        if !self.hold_time.is_finite() { self.hold_time = defaults.hold_time; }
        if !self.speed.is_finite() { self.speed = defaults.speed; }
        self.hold_time = self.hold_time.clamp(0.1, 3.0);
        self.speed = self.speed.clamp(0.5, 6.0);
        self.key = self.key.min(255);
        self
    }
}

static SETTINGS: Mutex<Option<Settings>> = Mutex::new(None);

fn settings_path() -> std::path::PathBuf {
    game_dir().join(SETTINGS_FILE)
}

fn load_settings() -> Settings {
    let path = settings_path();
    match std::fs::read_to_string(&path) {
        Ok(s) => serde_json::from_str::<Settings>(&s).map(Settings::sanitized).unwrap_or_else(|e| {
            log::info(format!("reshred: {} is invalid ({e}); using defaults", path.display()));
            Settings::default()
        }),
        Err(_) => {
            let s = Settings::default();
            let _ = std::fs::write(&path, serde_json::to_string_pretty(&s).unwrap_or_default());
            s
        }
    }
}

fn settings() -> Settings {
    let mut g = SETTINGS.lock().unwrap();
    *g.get_or_insert_with(load_settings)
}

fn save(s: Settings) {
    *SETTINGS.lock().unwrap() = Some(s);
    let _ = std::fs::write(settings_path(), serde_json::to_string_pretty(&s).unwrap_or_default());
}

// ---- MuJoCo and game handles --------------------------------------------------------------------

type StateSize = unsafe extern "C" fn(m: *const c_void, sig: u32) -> i32;
type GetState = unsafe extern "C" fn(m: *const c_void, d: *mut c_void, state: *mut f64, sig: u32);
type SetState = unsafe extern "C" fn(m: *const c_void, d: *mut c_void, state: *const f64, sig: u32);
type Forward = unsafe extern "C" fn(m: *const c_void, d: *mut c_void);

struct Mj {
    size: StateSize,
    get: GetState,
    set: SetState,
    forward: Forward,
}

unsafe fn mj() -> R<Mj> {
    let module = GetModuleHandleA(c"pcore.dll".as_ptr() as _);
    if module.is_null() {
        return Err("MuJoCo (pcore.dll) not loaded".into());
    }
    let sym = |n: &[u8]| GetProcAddress(module, n.as_ptr()).ok_or_else(|| format!("pcore.dll has no {}", String::from_utf8_lossy(&n[..n.len() - 1])));
    Ok(Mj {
        size: std::mem::transmute(sym(b"mj_stateSize\0")?),
        get: std::mem::transmute(sym(b"mj_getState\0")?),
        set: std::mem::transmute(sym(b"mj_setState\0")?),
        forward: std::mem::transmute(sym(b"mj_forward\0")?),
    })
}

/// Everything resolved once: native functions, field handles, the physics step.
struct Ctx {
    mj: Mj,
    scene_instance: *mut Field,
    scene_model: *mut Field,
    scene_data: *mut Field,
    scene_paused: *mut Field,
    dt: f32,
    rec_instance: Option<*mut Field>,
    rec_ragdoll: Option<*mut Field>,
    rec_clip: Option<*mut Field>,
    ragdoll_refs: Vec<*mut Field>,
}
unsafe impl Send for Ctx {}
unsafe impl Sync for Ctx {}

static CTX: OnceLock<Ctx> = OnceLock::new();
static CTX_TRIES: AtomicU32 = AtomicU32::new(0);

/// The context; building it is retried every 300th call (physics steps) unless `force`.
unsafe fn ctx(force: bool) -> Option<&'static Ctx> {
    if let Some(c) = CTX.get() {
        return Some(c);
    }
    let n = CTX_TRIES.fetch_add(1, Ordering::Relaxed);
    if !force && n % 300 != 0 {
        return None;
    }
    match build_ctx() {
        Ok(c) => {
            log::info(format!(
                "reshred: ready (dt {:.4}s, recorder {}, clip {}, ragdoll {}, {} state objects)",
                c.dt,
                c.rec_instance.is_some(),
                c.rec_clip.is_some(),
                c.rec_ragdoll.is_some(),
                c.ragdoll_refs.len()
            ));
            let _ = CTX.set(c);
            CTX.get()
        }
        Err(e) => {
            if n == 0 || force {
                log::info(format!("reshred: not ready: {e}"));
            }
            None
        }
    }
}

unsafe fn is_static(f: *const Field) -> bool {
    (api().field_get_flags)(f) & 0x10 != 0
}

/// Fields of `k` and its parents, up to the first class that has a namespace (the game's own classes
/// have none).
unsafe fn chain_fields(k: *mut Class) -> Vec<*mut Field> {
    let a = api();
    let mut out = vec![];
    let mut cur = k as *const Class;
    while !cur.is_null() && cstr((a.class_get_namespace)(cur)).is_empty() {
        let mut it: *mut c_void = null_mut();
        loop {
            let f = (a.class_get_fields)(cur, &mut it);
            if f.is_null() {
                break;
            }
            out.push(f);
        }
        cur = (a.class_get_parent)(cur);
    }
    out
}

/// Two or three lowercase letters: how the game names its obfuscated members and classes. A field
/// backing a property (`<abc>k__BackingField`) counts by its inner name.
fn obfuscated(name: &str) -> bool {
    let n = name.strip_prefix('<').and_then(|s| s.strip_suffix(">k__BackingField")).unwrap_or(name);
    (2..=3).contains(&n.len()) && n.bytes().all(|b| b.is_ascii_lowercase())
}

unsafe fn field_class(f: *const Field) -> *mut Class {
    (api().class_from_type)((api().field_get_type)(f))
}

unsafe fn obj_field(o: *mut Object, f: *mut Field) -> *mut Object {
    let mut v: *mut Object = null_mut();
    (api().field_get_value)(o, f, &mut v as *mut _ as _);
    v
}

unsafe fn static_obj(f: *mut Field) -> *mut Object {
    let mut v: *mut Object = null_mut();
    (api().field_static_get_value)(f, &mut v as *mut _ as _);
    v
}

/// The replay clip: an obfuscated class with an indexer, `Add` and `Serialize`.
unsafe fn is_clip_class(k: *mut Class) -> bool {
    !k.is_null() && ["get_Item", "Add", "Serialize"].iter().all(|m| il2cpp::method(k, m, 1).is_some())
}

unsafe fn build_ctx() -> R<Ctx> {
    let mj = mj()?;
    let sk = unity::class("Mujoco.Runtime.dll", "Mujoco", "MjScene")?;
    (api().runtime_class_init)(sk);
    let fld = |n: &str| il2cpp::field(sk, n).ok_or_else(|| format!("MjScene.{n} missing"));
    let (scene_instance, scene_model, scene_data, scene_paused) = (fld("_instance")?, fld("Model")?, fld("Data")?, fld("IsPaused")?);

    let dt = unity::class(unity::CORE, "UnityEngine", "Time")
        .and_then(|k| unity::call(k, null_mut(), "get_fixedDeltaTime", &mut []))
        .map(|o| il2cpp::unbox::<f32>(o))
        .ok()
        .filter(|d| (0.0005..0.05).contains(d))
        .unwrap_or(0.003);

    let (mut rec_instance, mut rec_ragdoll, mut rec_clip, mut ragdoll_refs) = (None, None, None, vec![]);
    if let Ok(rk) = unity::class(GAME_DLL, "", "Recorder") {
        (api().runtime_class_init)(rk);
        for f in chain_fields(rk) {
            let ty = type_name((api().field_get_type)(f));
            if is_static(f) {
                if ty == "Recorder" {
                    rec_instance = Some(f);
                }
            } else if ty == "RagdollController" {
                rec_ragdoll.get_or_insert(f);
            } else if rec_clip.is_none() && obfuscated(&ty) && is_clip_class(field_class(f)) {
                rec_clip = Some(f);
            }
        }
    }
    if let Ok(gk) = unity::class(GAME_DLL, "", "RagdollController") {
        for f in chain_fields(gk) {
            if is_static(f) {
                continue;
            }
            let k = field_class(f);
            if !k.is_null() && !(api().class_is_valuetype)(k) && obfuscated(&type_name((api().field_get_type)(f))) {
                ragdoll_refs.push(f);
            }
        }
    }
    Ok(Ctx { mj, scene_instance, scene_model, scene_data, scene_paused, dt, rec_instance, rec_ragdoll, rec_clip, ragdoll_refs })
}

/// (model, data) of the live physics scene, or null pointers.
unsafe fn scene_ptrs(cx: &Ctx) -> (*mut c_void, *mut c_void) {
    let s = static_obj(cx.scene_instance);
    if s.is_null() {
        return (null_mut(), null_mut());
    }
    let (mut m, mut d): (*mut c_void, *mut c_void) = (null_mut(), null_mut());
    (api().field_get_value)(s, cx.scene_model, &mut m as *mut _ as _);
    (api().field_get_value)(s, cx.scene_data, &mut d as *mut _ as _);
    (m, d)
}

unsafe fn paused(cx: &Ctx) -> bool {
    let mut p = false;
    (api().field_static_get_value)(cx.scene_paused, &mut p as *mut bool as _);
    p
}

// ---- game state snapshots ------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    /// The replay clip: its int and double fields (frame counters, timestamps).
    Clip,
    /// Ragdoll controller and its logic objects: obfuscated int and bool fields (flags, counters).
    State,
}

type Layout = Arc<Vec<(usize, usize)>>;

/// (field offset, byte size) of the fields of `k` that `kind` saves.
unsafe fn layout(k: *mut Class, kind: Kind) -> Vec<(usize, usize)> {
    let a = api();
    let mut out = vec![];
    for f in chain_fields(k) {
        if is_static(f) {
            continue;
        }
        let ty = type_name((a.field_get_type)(f));
        let name = cstr((a.field_get_name)(f));
        let size = match (kind, ty.as_str()) {
            (Kind::Clip, "System.Int32") => 4,
            (Kind::Clip, "System.Double") => 8,
            (Kind::State, "System.Int32") if obfuscated(&name) => 4,
            (Kind::State, "System.Boolean") if obfuscated(&name) => 1,
            _ => continue,
        };
        out.push(((a.field_get_offset)(f), size));
    }
    out
}

/// The objects whose state is saved now.
unsafe fn targets(cx: &Ctx) -> Vec<(*mut Object, Kind)> {
    let mut out: Vec<(*mut Object, Kind)> = vec![];
    let Some(fi) = cx.rec_instance else { return out };
    let rec = static_obj(fi);
    if rec.is_null() {
        return out;
    }
    if let Some(f) = cx.rec_clip {
        let c = obj_field(rec, f);
        if !c.is_null() {
            out.push((c, Kind::Clip));
        }
    }
    if let Some(f) = cx.rec_ragdoll {
        let r = obj_field(rec, f);
        if !r.is_null() {
            out.push((r, Kind::State));
            for rf in &cx.ragdoll_refs {
                let o = obj_field(r, *rf);
                if !o.is_null() && !out.iter().any(|(p, _)| *p == o) {
                    out.push((o, Kind::State));
                }
            }
        }
    }
    out
}

#[derive(Clone)]
struct Slot {
    obj: *mut Object,
    layout: Layout,
    bytes: Vec<u8>,
}

#[derive(Clone)]
struct Frame {
    state: Vec<f64>,
    slots: Vec<Slot>,
}

struct History {
    model: usize,
    data: usize,
    rider: usize,
    clip: usize,
    size: usize,
    cap: usize,
    frames: VecDeque<Frame>,
    /// Fractional physics steps carried between scrub steps (speeds that aren't whole numbers).
    scrub_acc: f32,
    steps: u64,
    layouts: Vec<(usize, Kind, Layout)>,
}
unsafe impl Send for History {}

static HISTORY_BUF: Mutex<Option<History>> = Mutex::new(None);

/// How many history frames to step back this physics step at `speed` (carrying the remainder).
fn scrub_steps(acc: &mut f32, speed: f32) -> usize {
    *acc += speed.max(0.0);
    let whole = acc.floor();
    *acc -= whole;
    whole as usize
}

/// Index of the frame to show after stepping back `k` frames from a history of `len`: never before
/// the oldest one.
fn scrub_target(len: usize, k: usize) -> Option<usize> {
    len.checked_sub(1).map(|last| last.saturating_sub(k))
}

impl History {
    unsafe fn layout_of(&mut self, obj: *mut Object, kind: Kind) -> Layout {
        let k = (api().object_get_class)(obj);
        if let Some((_, _, l)) = self.layouts.iter().find(|(c, kd, _)| *c == k as usize && *kd == kind) {
            return l.clone();
        }
        let l = Arc::new(layout(k, kind));
        self.layouts.push((k as usize, kind, l.clone()));
        l
    }
}

/// Before each MjScene.FixedUpdate: record the state about to be stepped.
unsafe extern "C" fn before_fixed_update(this: *mut c_void, _arg: *mut c_void) {
    let _ = std::panic::catch_unwind(|| unsafe { record(this) });
}

unsafe fn record(this: *mut c_void) {
    let Some(cx) = ctx(false) else { return };
    let Ok(mut g) = HISTORY_BUF.try_lock() else { return };
    if paused(cx) {
        return;
    }
    let (mut m, mut d): (*mut c_void, *mut c_void) = (null_mut(), null_mut());
    (api().field_get_value)(this as *mut Object, cx.scene_model, &mut m as *mut _ as _);
    (api().field_get_value)(this as *mut Object, cx.scene_data, &mut d as *mut _ as _);
    if m.is_null() || d.is_null() {
        return;
    }
    let size = (cx.mj.size)(m, STATE_SIG).max(0) as usize;
    if size == 0 || size > 1_000_000 { return; }
    let live = targets(cx);
    let rider = live.iter().find(|(_, kind)| *kind == Kind::State).map_or(0, |(o, _)| *o as usize);
    let clip = live.iter().find(|(_, kind)| *kind == Kind::Clip).map_or(0, |(o, _)| *o as usize);
    // a new or rebuilt physics scene invalidates the history
    if g.as_ref().map(|h| h.model != m as usize || h.data != d as usize || h.size != size || h.rider != rider || h.clip != clip).unwrap_or(true) {
        let cap = (HISTORY / cx.dt) as usize + 1;
        *g = Some(History { model: m as usize, data: d as usize, rider, clip, size, cap, frames: VecDeque::with_capacity(cap), scrub_acc: 0.0, steps: 0, layouts: vec![] });
    }
    let h = g.as_mut().unwrap();
    if SCRUBBING.load(Ordering::Acquire) {
        // Hold to scrub: instead of recording, each physics step goes back through the history and
        // shows that moment (the step that follows only nudges it by one frame).
        let k = scrub_steps(&mut h.scrub_acc, settings().speed);
        if let Some(i) = scrub_target(h.frames.len(), k) {
            h.frames.truncate(i + 1);
            if let Err(e) = restore_frame(cx, m, d, h.size, &h.frames[i], &live) {
                log::info(format!("reshred: scrub: {e}"));
            }
        }
        return;
    }
    h.scrub_acc = 0.0;
    let mut frame = if h.frames.len() >= h.cap { h.frames.pop_front().unwrap() } else { Frame { state: vec![0.0; size], slots: vec![] } };
    (cx.mj.get)(m, d, frame.state.as_mut_ptr(), STATE_SIG);
    frame.slots.clear();
    for (obj, kind) in live {
        let lay = h.layout_of(obj, kind);
        let mut bytes = Vec::with_capacity(lay.iter().map(|(_, n)| n).sum());
        for &(off, n) in lay.iter() {
            bytes.extend_from_slice(std::slice::from_raw_parts((obj as *const u8).add(off), n));
        }
        frame.slots.push(Slot { obj, layout: lay, bytes });
    }
    h.frames.push_back(frame);
    h.steps += 1;
}

/// True while the button is held past the hold time (set by the input poll, read by the recorder).
static SCRUBBING: AtomicBool = AtomicBool::new(false);

/// Writes `frame` back into the physics scene and the game's objects (main thread); returns how many
/// game objects had their state restored.
unsafe fn restore_frame(cx: &Ctx, m: *mut c_void, d: *mut c_void, size: usize, frame: &Frame, live: &[(*mut Object, Kind)]) -> R<usize> {
    if frame.state.len() != size || (cx.mj.size)(m, STATE_SIG) != size as i32 {
        return Err("physics state size changed; wait for fresh history".into());
    }
    (cx.mj.set)(m, d, frame.state.as_ptr(), STATE_SIG);
    (cx.mj.forward)(m, d);
    let mut restored = 0;
    // only objects that are still the live ones: an old pointer may belong to a freed object
    for s in &frame.slots {
        if !live.iter().any(|(o, _)| *o == s.obj) {
            continue;
        }
        let mut at = 0;
        for &(off, n) in s.layout.iter() {
            let Some(bytes) = s.bytes.get(at..at + n) else { break };
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), (s.obj as *mut u8).add(off), n);
            at += n;
        }
        restored += 1;
    }
    Ok(restored)
}

/// Jumps `back` seconds into the past at once (developer command; main thread).
pub unsafe fn rewind(back: f32) -> R<Value> {
    if !back.is_finite() || back <= 0.0 {
        return Err("rewind must be a finite positive duration".into());
    }
    let cx = ctx(true).ok_or("reshred is not ready (no physics scene yet?)")?;
    if paused(cx) { return Err("physics is paused".into()); }
    let (m, d) = scene_ptrs(cx);
    if m.is_null() || d.is_null() {
        return Err("no physics scene".into());
    }
    let mut g = HISTORY_BUF.lock().unwrap();
    let h = g.as_mut().ok_or("no history yet")?;
    if h.model != m as usize || h.data != d as usize {
        return Err("physics scene changed; history reset".into());
    }
    let live = targets(cx);
    let rider = live.iter().find(|(_, kind)| *kind == Kind::State).map_or(0, |(o, _)| *o as usize);
    let clip = live.iter().find(|(_, kind)| *kind == Kind::Clip).map_or(0, |(o, _)| *o as usize);
    if rider == 0 || rider != h.rider || clip != h.clip {
        return Err("Rider changed - waiting for fresh history".into());
    }
    let n = h.frames.len();
    let i = scrub_target(n, (back / cx.dt).round() as usize).ok_or("history empty")?;
    let restored = restore_frame(cx, m, d, h.size, &h.frames[i], &live)?;
    // everything after the restored moment is gone: we continue from there
    h.frames.truncate(i + 1);
    let got_back = (n - 1 - i) as f32 * cx.dt;
    log::info(format!("reshred: back {got_back:.2}s ({} steps, {restored} state objects)", n - 1 - i));
    Ok(json!({"ok": true, "back": got_back, "steps": n - 1 - i, "frames": h.frames.len(), "stateObjects": restored}))
}

fn history_len() -> usize {
    HISTORY_BUF.try_lock().ok().and_then(|g| g.as_ref().map(|h| h.frames.len())).unwrap_or(0)
}

// ---- input and indicator -------------------------------------------------------------------------

struct Hold {
    /// Seconds the button has been held in this press.
    held: f32,
    /// The rewind is running (the hold time passed and the button is still down).
    scrubbing: bool,
    /// History length when the rewind started: how far it has gone is measured from it.
    start_len: usize,
    /// The press began while typing in a text field.
    ignore: bool,
    was_down: bool,
    last: Option<Instant>,
    /// Pointer-sized managed GC handle of the cloned label (0 = none).
    /// Unity 6 uses pointer handles; the legacy u32 il2cpp_gchandle exports truncate them.
    label: isize,
    input_error: Option<String>,
    pad_frame: u32,
    pad_connected: [bool; 4],
}

static HOLD: Mutex<Hold> = Mutex::new(Hold { held: 0.0, scrubbing: false, start_len: 0, ignore: false, was_down: false, last: None, label: 0, input_error: None, pad_frame: 0, pad_connected: [false; 4] });

/// True when the foreground window belongs to this process (the game).
unsafe fn game_focused() -> bool {
    let fg = GetForegroundWindow();
    if fg.is_null() {
        return false;
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(fg, &mut pid);
    pid == GetCurrentProcessId()
}

#[repr(C)]
#[derive(Default, Clone, Copy)]
struct XInputState {
    packet: u32,
    buttons: u16,
    lt: u8,
    rt: u8,
    thumbs: [i16; 4],
}

type XInputGetState = unsafe extern "system" fn(u32, *mut XInputState) -> u32;

fn xinput() -> Option<XInputGetState> {
    static F: OnceLock<Option<usize>> = OnceLock::new();
    let p = *F.get_or_init(|| unsafe {
        for dll in [c"xinput1_4.dll", c"xinput1_3.dll", c"xinput9_1_0.dll"] {
            let m = LoadLibraryA(dll.as_ptr() as _);
            if !m.is_null() {
                if let Some(f) = GetProcAddress(m, c"XInputGetState".as_ptr() as _) {
                    return Some(f as usize);
                }
            }
        }
        None
    });
    p.map(|p| unsafe { std::mem::transmute::<usize, XInputGetState>(p) })
}

/// X (0x4000) held on any connected pad. Pads that are not connected are only polled now and then,
/// since asking for them is slow on some systems.
unsafe fn pad_x(h: &mut Hold) -> bool {
    let Some(get) = xinput() else { return false };
    h.pad_frame = h.pad_frame.wrapping_add(1);
    let probe = h.pad_frame == 1 || h.pad_frame % 180 == 0;
    let mut down = false;
    for i in 0..4 {
        if !h.pad_connected[i] && !probe {
            continue;
        }
        let mut st = XInputState::default();
        let ok = get(i as u32, &mut st) == 0;
        h.pad_connected[i] = ok;
        down |= ok && st.buttons & 0x4000 != 0;
    }
    down
}

/// True while a text field has keyboard focus (so typing an X into chat is not a rewind).
unsafe fn typing() -> bool {
    let Ok(k) = unity::class("Unity.TextMeshPro.dll", "TMPro", "TMP_InputField") else { return false };
    unity::find_objects(k)
        .map(|v| v.into_iter().any(|o| unity::call_unbox::<bool>(o, "get_isFocused").unwrap_or(false)))
        .unwrap_or(false)
}

/// The label's Transform, cloned from the game's "Respawn" text on first use.
unsafe fn label(h: &mut Hold) -> R<*mut Object> {
    if h.label != 0 {
        let o = unity::gc_target(h.label)?;
        if !o.is_null() && !unity::is_null(o) {
            return Ok(o);
        }
        unity::gc_free(h.label)?;
        h.label = 0;
    }
    let canvas = modmenu::find(modmenu::ui_root()?, "MainCanvas");
    let tpl = modmenu::find(canvas, "Respawn");
    if tpl.is_null() {
        return Err("UI/MainCanvas/Respawn not found".into());
    }
    let c = modmenu::clone(tpl, canvas, "Reshred_Label")?;
    let configured = (|| -> R<()> {
    // its ReactiveText would keep rewriting the text to the respawn state
    if let Ok(rt) = modmenu::component(c, "ReactiveText") {
        if let Ok(k) = unity::class(unity::CORE, "UnityEngine", "Object") {
            let _ = unity::call(k, null_mut(), "DestroyImmediate", &mut [rt as *mut c_void]);
        }
    }
    // The game's Respawn template is parked above the screen until its own code
    // moves/fades it in. A clone must have an independent, visible HUD placement.
    let mut anchor = [0.5f32, 0.18];
    unity::callv(c, "set_anchorMin", &mut [anchor.as_mut_ptr() as _])?;
    unity::callv(c, "set_anchorMax", &mut [anchor.as_mut_ptr() as _])?;
    let mut pivot = [0.5f32, 0.5];
    unity::callv(c, "set_pivot", &mut [pivot.as_mut_ptr() as _])?;
    let mut pos = [0.0f32; 3];
    unity::callv(c, "set_anchoredPosition3D", &mut [pos.as_mut_ptr() as _])?;
    let mut size = [720.0f32, 72.0];
    unity::callv(c, "set_sizeDelta", &mut [size.as_mut_ptr() as _])?;
    let mut scale = [1.0f32; 3];
    unity::callv(c, "set_localScale", &mut [scale.as_mut_ptr() as _])?;
    let tmp = modmenu::component(c, "TMP_Text")?;
    let mut white = [1.0f32; 4];
    unity::callv(tmp, "set_color", &mut [white.as_mut_ptr() as _])?;
    let mut alpha = 1.0f32;
    let _ = unity::callv(tmp, "set_alpha", &mut [&mut alpha as *mut f32 as _]);
    let mut font_size = 30.0f32;
    let _ = unity::callv(tmp, "set_fontSize", &mut [&mut font_size as *mut f32 as _]);
    let mut center = 514i32; // TextAlignmentOptions.Center
    let _ = unity::callv(tmp, "set_alignment", &mut [&mut center as *mut i32 as _]);
    let mut no = false;
    let _ = unity::callv(tmp, "set_raycastTarget", &mut [&mut no as *mut bool as _]);
    let mut yes = true;
    let _ = unity::callv(tmp, "set_enabled", &mut [&mut yes as *mut bool as _]);
    let _ = unity::callv(c, "SetAsLastSibling", &mut []);
    Ok(())
    })();
    if let Err(e) = configured {
        let _ = modmenu::destroy(c);
        return Err(e);
    }
    h.label = unity::gc_handle(c)?;
    Ok(c)
}

unsafe fn show_label(h: &mut Hold, text: &str) {
    match label(h) {
        Ok(tr) => {
            modmenu::set_text(tr, "", text);
            modmenu::set_active(tr, true);
        }
        Err(e) => {
            // one message is enough; the rewind itself does not depend on the label
            static TOLD: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
            if !TOLD.swap(true, Ordering::Relaxed) {
                log::info(format!("reshred: no indicator: {e}"));
            }
        }
    }
}

unsafe fn hide_label(h: &Hold) {
    if h.label == 0 {
        return;
    }
    let Ok(o) = unity::gc_target(h.label) else { return };
    if !o.is_null() && !unity::is_null(o) {
        modmenu::set_active(o, false);
    }
}

/// Runs before every `EnvironmentManager.Update` (main thread).
unsafe extern "C" fn before_env_update(_this: *mut c_void, _arg: *mut c_void) {
    let _ = std::panic::catch_unwind(|| unsafe { tick() });
}

unsafe fn tick() {
    let s = settings();
    let Ok(mut h) = HOLD.try_lock() else { return };
    let error = reshred_input::update(s.enabled, s.key, s.controller).err();
    if error != h.input_error {
        if let Some(ref e) = error { log::info(format!("reshred: input reservation: {e}")); }
        h.input_error = error;
    }
    let now = Instant::now();
    let dt = h.last.map(|t| now.duration_since(t).as_secs_f32()).unwrap_or(0.0).min(0.25);
    h.last = Some(now);

    let down = s.enabled && game_focused() && ((s.key != 0 && GetAsyncKeyState(s.key as i32) as u16 & 0x8000 != 0) || (s.controller && (reshred_input::pad_down() || pad_x(&mut h))));
    if !down {
        if h.scrubbing {
            end_scrub(&mut h);
        }
        if h.was_down {
            hide_label(&h);
        }
        h.was_down = false;
        h.held = 0.0;
        return;
    }
    if !h.was_down {
        h.was_down = true;
        h.held = 0.0;
        h.ignore = typing();
    }
    if h.ignore {
        return;
    }
    // No rewind in menus/loading/paused scenes, but never silently hide a failed
    // initialization: an onscreen status distinguishes it from an unrecognized key.
    let dt_phys = ctx(false).map(|cx| cx.dt);
    let ready = dt_phys.is_some_and(|dt| {
        let steps = history_len();
        !paused_now() && if h.scrubbing { steps > 0 } else { steps as f32 * dt > 0.3 }
    });
    if !ready {
        h.held = 0.0;
        if h.scrubbing {
            end_scrub(&mut h);
        }
        if s.indicator { show_label(&mut h, "Reshred waiting for active physics/history"); }
        else { hide_label(&h); }
        return;
    }
    let dt_phys = dt_phys.unwrap_or(0.003);
    h.held += dt;
    if !h.scrubbing {
        if h.held >= s.hold_time {
            h.scrubbing = true;
            h.start_len = history_len();
            SCRUBBING.store(true, Ordering::Release);
        } else if s.indicator {
            let pct = (h.held / s.hold_time * 100.0).min(99.0) as u32;
            let warning = if h.input_error.is_some() { " - input conflict (see log)" } else { "" };
            show_label(&mut h, &format!("Hold to rewind {pct}%{warning}"));
            return;
        } else {
            hide_label(&h);
            return;
        }
    }
    if s.indicator {
        let back = h.start_len.saturating_sub(history_len()) as f32 * dt_phys;
        let total = h.start_len.saturating_sub(1) as f32 * dt_phys;
        let at_start = history_len() <= 1;
        let tail = if at_start { "start of the rewind - release to resume" } else { "release to resume" };
        show_label(&mut h, &format!("Rewinding -{back:.1}s / -{total:.1}s - {tail}"));
    } else {
        hide_label(&h);
    }
}

/// The button was let go (or physics stopped): normal recording and stepping continue from the
/// moment the scrub reached.
unsafe fn end_scrub(h: &mut Hold) {
    SCRUBBING.store(false, Ordering::Release);
    h.scrubbing = false;
    if let Some(dt) = ctx(false).map(|cx| cx.dt) {
        let back = h.start_len.saturating_sub(history_len()) as f32 * dt;
        log::info(format!("reshred: resumed {back:.2}s back"));
    }
}

unsafe fn paused_now() -> bool {
    ctx(false).is_some_and(|cx| paused(cx))
}


// ---- install and commands ------------------------------------------------------------------------

/// Installs the per-step recorder and the input poll. Called once when the core loads.
pub unsafe fn install(host: &crate::abi::Host) {
    let _ = settings();
    let step = unity::class("Mujoco.Runtime.dll", "Mujoco", "MjScene").ok().and_then(|k| il2cpp::method(k, "FixedUpdate", 0));
    match step {
        Some(m) => {
            let rc = (host.pre_hook)(m as *const c_void, before_fixed_update);
            log::info(format!("hook MjScene.FixedUpdate (reshred): {}", if rc == 0 { "ok".into() } else { format!("failed ({rc})") }));
        }
        None => log::info("reshred: MjScene.FixedUpdate not found"),
    }
    let frame = unity::class(GAME_DLL, "", "EnvironmentManager").ok().and_then(|k| il2cpp::method(k, "Update", 0));
    match frame {
        Some(m) => {
            let rc = (host.pre_hook)(m as *const c_void, before_env_update);
            log::info(format!("hook EnvironmentManager.Update (reshred): {}", if rc == 0 { "ok".into() } else { format!("failed ({rc})") }));
        }
        None => log::info("reshred: EnvironmentManager.Update not found"),
    }
}

/// Called on the main thread before unload: restores bindings and removes the label.
pub fn teardown() {
    unsafe {
    SCRUBBING.store(false, Ordering::Release);
    reshred_input::restore();
        let Ok(mut h) = HOLD.lock() else { return };
        if h.label != 0 {
            if let Ok(o) = unity::gc_target(h.label) {
                if !o.is_null() && !unity::is_null(o) {
                    let _ = modmenu::destroy(o);
                }
            }
            let _ = unity::gc_free(h.label);
            h.label = 0;
        }
    }
}

/// `{"cmd":"reshred","info":true}` / `{"state":true}` / `{"back":1.5}` (developer probes; runs on the
/// main thread).
pub fn command(req: &Value) -> R<Value> {
    let req = req.clone();
    crate::pump::run(move || unsafe {
        if req.get("info").is_some() {
            let cx = ctx(true);
            let g = HISTORY_BUF.lock().unwrap();
            let mut out = match g.as_ref() {
                Some(h) => json!({"ok": true, "frames": h.frames.len(), "stateSize": h.size, "span": h.frames.len() as f32 * cx.map(|c| c.dt).unwrap_or(0.0)}),
                None => json!({"ok": true, "frames": 0}),
            };
            if let Some(cx) = cx {
                out["recorder"] = json!(cx.rec_instance.is_some());
                out["clip"] = json!(cx.rec_clip.is_some());
                out["ragdoll"] = json!(cx.rec_ragdoll.is_some());
                out["refs"] = json!(cx.ragdoll_refs.len());
                out["dt"] = json!(cx.dt);
            }
            out["scrubbing"] = json!(SCRUBBING.load(Ordering::Acquire));
            out["input"] = reshred_input::status();
            if let Ok(h) = HOLD.try_lock() {
                out["held"] = json!(h.held);
                out["inputError"] = json!(h.input_error);
            }
            return Ok(out);
        }
        if req.get("state").is_some() {
            let cx = ctx(true).ok_or("reshred is not ready")?;
            let objs: Vec<Value> = targets(cx)
                .into_iter()
                .map(|(o, kind)| {
                    let k = (api().object_get_class)(o);
                    let lay = layout(k, kind);
                    json!({"class": cstr((api().class_get_name)(k)), "kind": if kind == Kind::Clip { "clip" } else { "state" }, "fields": lay.len(), "bytes": lay.iter().map(|(_, n)| n).sum::<usize>()})
                })
                .collect();
            return Ok(json!({"ok": true, "objects": objs}));
        }
        rewind(req.get("back").and_then(Value::as_f64).unwrap_or(1.5) as f32)
    })?
}

// ---- plugin protocol (Mods menu) -----------------------------------------------------------------

fn schema() -> Value {
    let d = Settings::default();
    json!([
        {"key": "enabled", "label": "Reshred", "type": "toggle", "default": d.enabled, "section": "General", "tooltip": "Hold the button to rewind through the last 6 seconds; let go to carry on from there."},
        {"key": "key", "label": "Keyboard key", "type": "key", "default": d.key, "section": "General", "tooltip": "Key to hold (virtual-key code; X by default)."},
        {"key": "controller", "label": "Controller X", "type": "toggle", "default": d.controller, "section": "General", "tooltip": "Also rewind when holding X on a controller."},
        {"key": "holdTime", "label": "Hold time", "type": "slider", "default": d.hold_time, "min": 0.1, "max": 2.0, "step": 0.1, "section": "General", "tooltip": "Seconds to hold the button before the rewind starts."},
        {"key": "speed", "label": "Rewind speed", "type": "slider", "default": d.speed, "min": 0.5, "max": 6.0, "step": 0.5, "section": "General", "tooltip": "How fast time runs backwards while held (1 = real time)."},
        {"key": "indicator", "label": "Indicator", "type": "toggle", "default": d.indicator, "section": "General", "tooltip": "Show how far back you are while rewinding."},
    ])
}

fn values(s: &Settings) -> Value {
    let round = |x: f32| (x as f64 * 100.0).round() / 100.0;
    json!({"enabled": s.enabled, "key": s.key, "controller": s.controller, "holdTime": round(s.hold_time), "speed": round(s.speed), "indicator": s.indicator})
}

fn set(key: &str, value: &Value) -> R<Value> {
    let mut s = settings();
    let bad = || format!("{key} takes a different kind of value");
    match key {
        "enabled" => s.enabled = value.as_bool().ok_or_else(bad)?,
        "controller" => s.controller = value.as_bool().ok_or_else(bad)?,
        "indicator" => s.indicator = value.as_bool().ok_or_else(bad)?,
        "key" => s.key = value.as_u64().filter(|k| *k <= 255).ok_or_else(|| "key takes a key code (0-255)".to_string())? as u32,
        "holdTime" => s.hold_time = value.as_f64().ok_or_else(bad)? as f32,
        "speed" => s.speed = value.as_f64().ok_or_else(bad)? as f32,
        _ => return Err(format!("unknown setting {key:?}")),
    }
    let s = s.sanitized();
    save(s);
    Ok(json!({"ok": true, "values": values(&s)}))
}

/// Answers a `plugin.*` request.
pub fn plugin(req: &Value) -> R<Value> {
    match req.get("cmd").and_then(Value::as_str).unwrap_or("") {
        "plugin.info" => {
            let s = settings();
            Ok(json!({
                "ok": true,
                "id": "reshred",
                "name": "Reshred",
                "version": env!("CARGO_PKG_VERSION"),
                "author": "Jaxon",
                "description": "Hold X to rewind through the last 6 seconds, then let go to continue from that moment. Rewound time is cut from replay clips.",
                "enabled": s.enabled,
                "settings": schema(),
            }))
        }
        "plugin.get" => Ok(json!({"ok": true, "values": values(&settings())})),
        "plugin.set" => {
            let key = req.get("key").and_then(Value::as_str).ok_or("plugin.set needs a key")?;
            set(key, req.get("value").ok_or("plugin.set needs a value")?)
        }
        "plugin.enable" => {
            let on = req.get("on").and_then(Value::as_bool).ok_or("plugin.enable needs \"on\": true or false")?;
            set("enabled", &Value::Bool(on))?;
            Ok(json!({"ok": true, "enabled": on}))
        }
        other => Err(format!("unknown plugin command {other:?}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scrub_speed_carries_fractions() {
        let mut acc = 0.0;
        let steps: Vec<usize> = (0..4).map(|_| scrub_steps(&mut acc, 1.5)).collect();
        assert_eq!(steps, vec![1, 2, 1, 2]);
        let mut acc = 0.0;
        assert_eq!((0..4).map(|_| scrub_steps(&mut acc, 0.5)).sum::<usize>(), 2);
        assert_eq!(scrub_steps(&mut 0.0, -3.0), 0);
        assert_eq!(scrub_steps(&mut 0.0, 2.0), 2);
    }

    #[test]
    fn scrub_stops_at_the_oldest_frame() {
        assert_eq!(scrub_target(0, 2), None);
        assert_eq!(scrub_target(10, 0), Some(9));
        assert_eq!(scrub_target(10, 3), Some(6));
        assert_eq!(scrub_target(10, 99), Some(0));
        assert_eq!(scrub_target(1, 5), Some(0));
    }

    #[test]
    fn six_seconds_of_history_is_kept() {
        assert_eq!(HISTORY, 6.0);
        assert_eq!((HISTORY / 0.003) as usize + 1, 2001);
    }

    #[test]
    fn settings_reject_nonfinite_values() {
        let s = Settings { hold_time: f32::NAN, speed: f32::INFINITY, ..Settings::default() }.sanitized();
        assert_eq!(s.hold_time, Settings::default().hold_time);
        assert_eq!(s.speed, Settings::default().speed);
        assert_eq!(Settings { speed: 100.0, ..Settings::default() }.sanitized().speed, 6.0);
        assert_eq!(Settings { speed: 0.0, ..Settings::default() }.sanitized().speed, 0.5);
    }

    #[test]
    fn old_settings_files_still_load() {
        let s: Settings = serde_json::from_value(json!({"rewind": 1.5, "runupTime": 2.5, "controller": true, "holdTime": 0.6})).unwrap();
        assert_eq!(s.speed, 2.0);
        assert_eq!(s.hold_time, 0.6);
        assert_eq!(values(&s)["speed"], json!(2.0));
        assert!(schema().as_array().unwrap().iter().any(|entry| entry["key"] == "speed"));
        assert!(!schema().as_array().unwrap().iter().any(|entry| entry["key"] == "runupTime"));
    }
}