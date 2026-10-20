//! FPV Drone: a flyable quadcopter on top of the game's own drone view.
//!
//! The game's drone is `FollowCam`'s OrbitCam state: while in it the game shows the camera to other
//! riders as a `Drone` model. Taking off switches FollowCam into that state with the game's own
//! setter, then this mod flies the camera from a FollowCam.LateUpdate pre-hook (after
//! FollowCam.Update has placed it). Rider physics are never written, and landing puts the camera
//! state back. Game classes are reached through `symbols` (ours are in fpv_symbols.json); the mouse
//! comes from Rewired, the game's input library.
mod flight;
mod fx;
mod input;
mod math;
#[doc(hidden)]
pub mod synth;

use flight::{Config, Drone, Hit, Mode, Target, World};
use il2cpp::{api, Class, Method, Object};
use input::{Actions, Input, InputConfig, Keys, Mouse, Throttle};
use jibbers_mod_sdk::plugin::{Info, Plugin};
use jibbers_mod_sdk::unity::{self, Vec3, R};
use jibbers_mod_sdk::{declare_plugin, host, il2cpp, json, log, settings, symbols, Setting, Value};
use math::{blend, Quat, V3};
use std::{
    ffi::c_void,
    ptr::null_mut,
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Mutex,
    },
};
use windows_sys::Win32::{
    System::Threading::GetCurrentProcessId,
    UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId},
};

const PHYSICS: &str = "UnityEngine.PhysicsModule.dll";
const REWIRED: &str = "Rewired_Core.dll";
const SYMBOLS: &str = include_str!("../fpv_symbols.json");
/// Bone names that make a good point to track on a rider, best first.
const BONES: [&str; 6] = ["pelvis", "hips", "hip", "spine", "torso", "root"];
const NEAR_CLIP: f32 = 0.05;

static LIVE: AtomicBool = AtomicBool::new(true);
static TOGGLE: AtomicBool = AtomicBool::new(false);
static REVISION: AtomicU64 = AtomicU64::new(1);
static STATE: Mutex<Option<State>> = Mutex::new(None);

struct Fpv;
impl Plugin for Fpv {
    const INFO: Info = Info {
        id: "fpv",
        name: "FPV Drone",
        version: env!("CARGO_PKG_VERSION"),
        author: "Jibbers mods",
        description: "Fly the game's drone camera as a real FPV quad: Acro, Angle and Cine (lock-on follow) modes. F9 or L3+R3 to take off.",
    };
    fn settings() -> Vec<Setting> {
        vec![
            Setting::choice("mode", "Flight mode", &Mode::NAMES, "Angle")
                .section("Flight")
                .tooltip("Acro: rate mode, full FPV. Angle: self-levelling with altitude hold. Cine: smooth gimbal drone that can lock onto and follow a rider."),
            Setting::slider("twr", "Thrust to weight", 4.5, 2., 8., 0.1).section("Flight"),
            Setting::slider("top_speed", "Top speed (m/s)", 38., 15., 60., 1.).section("Flight"),
            Setting::slider("max_angle", "Angle mode max tilt", 50., 20., 80., 1.).section("Flight"),
            Setting::toggle("altitude_hold", "Angle mode altitude hold", true)
                .section("Flight")
                .tooltip("Centred throttle holds height. Off: the throttle is direct, like Acro."),
            Setting::slider("uptilt", "Camera uptilt", 25., 0., 60., 1.).section("Flight"),
            Setting::slider("fov", "Field of view", 85., 50., 130., 1.).section("Flight"),
            Setting::toggle("hud", "Show the OSD", true)
                .section("Display")
                .tooltip("Mode, throttle, speed, altitude, flight time and the locked rider."),
            Setting::toggle("sound", "Drone sound", true).section("Display"),
            Setting::slider("volume", "Drone volume", 0.7, 0., 1., 0.05).section("Display"),
            Setting::slider("sensitivity", "Stick sensitivity", 0.6, 0.1, 1.5, 0.05)
                .section("Rates")
                .tooltip("Scales how fast the sticks turn the drone in every mode."),
            Setting::slider("rate_center", "Centre rate (deg/s)", 150., 50., 500., 5.).section("Rates"),
            Setting::slider("rate_max", "Max rate (deg/s)", 550., 200., 1500., 10.).section("Rates"),
            Setting::slider("expo", "Expo", 0.54, 0., 1., 0.01).section("Rates"),
            Setting::slider("cine_speed", "Cine speed (m/s)", 12., 3., 30., 0.5).section("Cine"),
            Setting::toggle("follow", "Follow the locked rider", true)
                .section("Cine")
                .tooltip("Off: a locked rider is only tracked by the gimbal, the sticks still fly the drone."),
            Setting::toggle("chase", "Chase from behind", false)
                .section("Cine")
                .tooltip("Keep the camera angle relative to the rider's line instead of a fixed offset."),
            Setting::slider("follow_time", "Follow smoothing (s)", 0.4, 0.1, 1.5, 0.05)
                .section("Cine")
                .tooltip("The game drone's followTime."),
            Setting::slider("max_accel", "Max acceleration (m/s²)", 25., 5., 60., 1.).section("Cine"),
            Setting::slider("turn_speed", "Gimbal turn speed", 4., 1., 12., 0.5).section("Cine"),
            Setting::toggle("collisions", "Collide with the map", true).section("Physics"),
            Setting::toggle("crash", "Disarm on hard crashes", false)
                .section("Physics")
                .tooltip("Acro/Angle: a hard hit cuts the motors for a moment and the quad tumbles."),
            Setting::slider("vibration", "Prop vibration", 0.3, 0., 1., 0.05).section("Physics"),
            Setting::choice("throttle", "Gamepad throttle", &Throttle::NAMES, Throttle::NAMES[0]).section("Input"),
            Setting::toggle("invert", "Invert pitch", false).section("Input"),
            Setting::toggle("mouse_look", "Mouse look", true).section("Input"),
            Setting::slider("mouse_sens", "Mouse sensitivity", 0.03, 0.005, 0.3, 0.005).section("Input"),
            Setting::slider("deadzone", "Stick deadzone", 0.08, 0., 0.3, 0.01).section("Input"),
            Setting::key("toggle_key", "Take off / land", 0x78).section("Hotkeys"),
            Setting::key("mode_key", "Cycle flight mode", 0x79).section("Hotkeys"),
            Setting::key("lock_key", "Lock target (hold: release)", 0x04).section("Hotkeys"),
            Setting::key("recall_key", "Return to rider", 0x24).section("Hotkeys"),
            Setting::key("tilt_up_key", "Tilt camera up", 0x21).section("Hotkeys"),
            Setting::key("tilt_down_key", "Tilt camera down", 0x22).section("Hotkeys"),
            Setting::key("fov_up_key", "Widen view", 0x6B).section("Hotkeys"),
            Setting::key("fov_down_key", "Narrow view", 0x6D).section("Hotkeys"),
        ]
    }
    fn init() {
        LIVE.store(true, Ordering::Release);
        let hooked = symbols::register(SYMBOLS).and_then(|()| unsafe { symbols::hook("camera.follow.late_update", on_frame) });
        match hooked {
            Ok(()) => log::info("fpv: ready; F9 or L3+R3 to take off"),
            Err(e) => {
                LIVE.store(false, Ordering::Release);
                log::info(format!("fpv: unavailable: {e}"));
            }
        }
    }
    fn changed(_key: &str, _value: &Value) {
        REVISION.fetch_add(1, Ordering::Release);
    }
    fn command(cmd: &str, _req: &Value) -> Option<R<Value>> {
        match cmd {
            "fpv.status" => Some(Ok(STATE
                .lock()
                .ok()
                .and_then(|s| s.as_ref().map(State::status))
                .unwrap_or_else(|| json!({"hooked": LIVE.load(Ordering::Acquire), "status": "no camera yet"})))),
            "fpv.toggle" => {
                TOGGLE.store(true, Ordering::Release);
                Some(Ok(json!({"requested": true})))
            }
            _ => None,
        }
    }
    fn unload() {
        LIVE.store(false, Ordering::Release);
        // Unload runs away from Unity's main thread: put the camera back and drop handles there.
        let result = host::pump::run(|| unsafe {
            if let Ok(mut state) = STATE.lock() {
                if let Some(mut s) = state.take() {
                    s.release(true);
                }
            }
        });
        if let Err(e) = result {
            log::info(format!("fpv: cleanup: {e}"));
        }
    }
}
declare_plugin!(Fpv);

fn config() -> Config {
    let f = |k: &str| settings::get_f64(k) as f32;
    Config {
        thrust_to_weight: f("twr"),
        top_speed: f("top_speed"),
        rate_center: f("rate_center"),
        rate_max: f("rate_max"),
        expo: f("expo"),
        sensitivity: f("sensitivity"),
        max_angle: f("max_angle"),
        altitude_hold: settings::get_bool("altitude_hold"),
        cine_speed: f("cine_speed"),
        follow_time: f("follow_time"),
        max_acceleration: f("max_accel"),
        turn_speed: f("turn_speed"),
        follow_target: settings::get_bool("follow"),
        chase: settings::get_bool("chase"),
        uptilt: f("uptilt"),
        collisions: settings::get_bool("collisions"),
        crash: settings::get_bool("crash"),
        ..Config::default()
    }
}
fn input_config() -> InputConfig {
    let k = settings::get_key;
    InputConfig {
        throttle: Throttle::parse(&settings::get_string("throttle")),
        invert_pitch: settings::get_bool("invert"),
        mouse_look: settings::get_bool("mouse_look"),
        mouse_sensitivity: settings::get_f64("mouse_sens") as f32,
        deadzone: settings::get_f64("deadzone") as f32,
        keys: Keys {
            toggle: k("toggle_key"),
            mode: k("mode_key"),
            lock: k("lock_key"),
            recall: k("recall_key"),
            tilt_up: k("tilt_up_key"),
            tilt_down: k("tilt_down_key"),
            fov_up: k("fov_up_key"),
            fov_down: k("fov_down_key"),
        },
    }
}

fn v3(p: Vec3) -> V3 {
    V3(p.x, p.y, p.z)
}
fn unity_vec(p: V3) -> Vec3 {
    Vec3 { x: p.0, y: p.1, z: p.2 }
}
unsafe fn focus() -> bool {
    let window = GetForegroundWindow();
    let mut pid = 0;
    if window.is_null() {
        return false;
    }
    GetWindowThreadProcessId(window, &mut pid);
    pid == GetCurrentProcessId()
}
unsafe fn instance_id(o: *mut Object) -> R<i32> {
    unity::call_unbox::<i32>(o, "GetInstanceID")
}

/// RaycastHit is read through its own accessors; its native size and layout are never assumed.
struct Physics {
    ray: usize,
    sphere: usize,
    hit_size: usize,
    distance: usize,
    normal: usize,
}
impl Physics {
    unsafe fn new() -> R<Self> {
        let k = unity::class(PHYSICS, "UnityEngine", "Physics")?;
        let hit = unity::class(PHYSICS, "UnityEngine", "RaycastHit")?;
        let ray = il2cpp::method_sig(
            k,
            "Raycast",
            &["UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction"],
        )
        .ok_or("Physics.Raycast signature missing")?;
        let sphere = il2cpp::method_sig(
            k,
            "SphereCast",
            &["UnityEngine.Vector3", "System.Single", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction"],
        )
        .ok_or("Physics.SphereCast signature missing")?;
        let hit_size = (api().class_value_size)(hit, null_mut()) as usize;
        if hit_size == 0 || hit_size > 512 {
            return Err("unexpected RaycastHit size".into());
        }
        let distance = il2cpp::method(hit, "get_distance", 0).ok_or("RaycastHit.distance missing")?;
        let normal = il2cpp::method(hit, "get_normal", 0).ok_or("RaycastHit.normal missing")?;
        Ok(Self { ray: ray as usize, sphere: sphere as usize, hit_size, distance: distance as usize, normal: normal as usize })
    }
    unsafe fn cast(&self, origin: V3, direction: V3, mut length: f32, mut radius: f32, mut mask: i32) -> R<Option<Hit>> {
        let mut origin = unity_vec(origin);
        let mut direction = unity_vec(direction);
        let mut hit = [0u64; 64];
        let mut triggers = 1i32; // QueryTriggerInteraction.Ignore
        debug_assert!(self.hit_size <= std::mem::size_of_val(&hit));
        let p = &mut origin as *mut Vec3 as *mut c_void;
        let d = &mut direction as *mut Vec3 as *mut c_void;
        let h = hit.as_mut_ptr() as *mut c_void;
        let len = &mut length as *mut f32 as *mut c_void;
        let mask = &mut mask as *mut i32 as *mut c_void;
        let q = &mut triggers as *mut i32 as *mut c_void;
        let result = if radius > 0. {
            il2cpp::invoke(self.sphere as *const Method, null_mut(), &mut [p, &mut radius as *mut f32 as _, d, h, len, mask, q])?
        } else {
            il2cpp::invoke(self.ray as *const Method, null_mut(), &mut [p, d, h, len, mask, q])?
        };
        if result.is_null() {
            return Err("physics cast returned null".into());
        }
        if !il2cpp::unbox::<bool>(result) {
            return Ok(None);
        }
        let distance = il2cpp::invoke(self.distance as *const Method, h, &mut [])?;
        let normal = il2cpp::invoke(self.normal as *const Method, h, &mut [])?;
        if distance.is_null() || normal.is_null() {
            return Err("hit accessor returned null".into());
        }
        let distance = il2cpp::unbox::<f32>(distance);
        let normal = v3(il2cpp::unbox::<Vec3>(normal));
        if !distance.is_finite() || distance < 0. || !normal.finite() {
            return Err("invalid collision hit".into());
        }
        Ok(Some(Hit { distance, normal }))
    }
}

/// The mouse through Rewired's public API (`ReInput.controllers.Mouse`): raw deltas that keep
/// coming while the game has the cursor locked.
struct RewiredMouse {
    controllers: usize,
    mouse: usize,
    ready: usize,
    axis: usize,
    button: usize,
}
impl RewiredMouse {
    unsafe fn new() -> R<Self> {
        let re = unity::class(REWIRED, "Rewired", "ReInput")?;
        let axes = unity::class(REWIRED, "Rewired", "ControllerWithAxes")?;
        let controller = unity::class(REWIRED, "Rewired", "Controller")?;
        let controllers = il2cpp::method(re, "get_controllers", 0).ok_or("ReInput.controllers missing")?;
        let ready = il2cpp::method(re, "get_isReady", 0).ok_or("ReInput.isReady missing")?;
        let helper = (api().class_from_type)((api().method_get_return_type)(controllers));
        if helper.is_null() {
            return Err("ReInput.controllers type missing".into());
        }
        let mouse = il2cpp::method(helper, "get_Mouse", 0).ok_or("controllers.Mouse missing")?;
        let axis = il2cpp::method_sig(axes, "GetAxisRaw", &["System.Int32"]).ok_or("GetAxisRaw(int) missing")?;
        let button = il2cpp::method_sig(controller, "GetButton", &["System.Int32"]).ok_or("GetButton(int) missing")?;
        Ok(Self {
            controllers: controllers as usize,
            mouse: mouse as usize,
            ready: ready as usize,
            axis: axis as usize,
            button: button as usize,
        })
    }
    unsafe fn read(&self) -> R<Mouse> {
        let call = |m: usize, this: *mut c_void, args: &mut [*mut c_void]| il2cpp::invoke(m as *const Method, this, args);
        let ready = call(self.ready, null_mut(), &mut [])?;
        if ready.is_null() || !il2cpp::unbox::<bool>(ready) {
            return Ok(Mouse::default());
        }
        let helper = call(self.controllers, null_mut(), &mut [])?;
        if helper.is_null() {
            return Ok(Mouse::default());
        }
        let mouse = call(self.mouse, helper, &mut [])?;
        if mouse.is_null() {
            return Ok(Mouse::default());
        }
        let read = |m: usize, mut i: i32| call(m, mouse, &mut [&mut i as *mut i32 as _]);
        let axis = |r: *mut Object| if r.is_null() { 0. } else { il2cpp::unbox::<f32>(r) };
        let (x, y) = (axis(read(self.axis, 0)?), axis(read(self.axis, 1)?));
        let right = read(self.button, 1)?;
        let finite = |v: f32| if v.is_finite() { v } else { 0. };
        Ok(Mouse { delta: (finite(x), finite(y)), right: !right.is_null() && il2cpp::unbox::<bool>(right) })
    }
}

/// The map as the flight model sees it. A failed cast is kept and reported after the step.
struct Cast<'a> {
    physics: &'a Physics,
    mask: i32,
    error: Option<String>,
}
impl World for Cast<'_> {
    fn sweep(&mut self, origin: V3, direction: V3, length: f32, radius: f32) -> Option<Hit> {
        if self.error.is_some() {
            return None;
        }
        match unsafe { self.physics.cast(origin, direction, length, radius, self.mask) } {
            Ok(hit) => hit,
            Err(e) => {
                self.error = Some(e);
                None
            }
        }
    }
}

struct Locked {
    handle: isize,
    id: i32,
    name: String,
    last: V3,
    velocity: V3,
}
struct Candidate {
    transform: *mut Object,
    id: i32,
    name: String,
}

struct State {
    owner: i32,
    follow: isize,
    camera: isize,
    transform: isize,
    follow_state: i32,
    orbit_state: i32,
    physics: Physics,
    mouse: Option<RewiredMouse>,
    mask: i32,
    ragdoll: Option<usize>,
    children: Option<usize>,
    input: Input,
    cfg: Config,
    input_cfg: InputConfig,
    revision: u64,
    enabled: bool,
    mode: Mode,
    fov: f32,
    vibration: f32,
    drone: Option<Drone>,
    previous: i32,
    original: Option<(f32, f32)>,
    lock: Option<Locked>,
    fx: Option<fx::Fx>,
    flight_time: f32,
    status: String,
}

impl State {
    unsafe fn new(follow: *mut Object) -> R<Self> {
        let state_value = |path: &str| -> R<i32> {
            let mut v = 0i32;
            (api().field_static_get_value)(symbols::field(path)?, &mut v as *mut i32 as _);
            Ok(v)
        };
        let follow_state = state_value("camera.cam_state.follow")?;
        let orbit_state = state_value("fpv.cam_state.orbit")?;
        if let Err(e) = symbols::method("fpv.follow.set_mode") {
            log::info(format!("fpv: writing the camera state directly: {e}"));
        }
        let mask = match symbols::get::<i32>(follow, "camera.follow.ground_layer")? {
            0 => -5, // Physics.DefaultRaycastLayers
            m => m,
        };
        let tr = unity::callv(follow, "get_transform", &mut [])?;
        let ck = unity::class(unity::CORE, "UnityEngine", "Camera")?;
        let cam = unity::callv(follow, "GetComponent", &mut [unity::type_object(ck)])?;
        if unity::is_null(cam) {
            return Err("FollowCam has no Camera component".into());
        }
        let physics = Physics::new()?;
        let mouse = RewiredMouse::new().map_err(|e| log::info(format!("fpv: no mouse look: {e}"))).ok();
        // Optional: lock-on to other riders.
        let ragdoll = symbols::class("player.ragdoll").ok().map(|c| c as usize);
        let children = unity::class(unity::CORE, "UnityEngine", "Component")
            .ok()
            .and_then(|c| il2cpp::method_sig(c, "GetComponentsInChildren", &["System.Type", "System.Boolean"]))
            .map(|m| m as usize);
        let owner = instance_id(follow)?;
        let mut handles = Vec::new();
        for o in [follow, cam, tr] {
            match unity::gc_handle(o) {
                Ok(h) => handles.push(h),
                Err(e) => {
                    for h in handles {
                        let _ = unity::gc_free(h);
                    }
                    return Err(e);
                }
            }
        }
        Ok(Self {
            owner,
            follow: handles[0],
            camera: handles[1],
            transform: handles[2],
            follow_state,
            orbit_state,
            physics,
            mouse,
            mask,
            ragdoll,
            children,
            input: Input::default(),
            cfg: Config::default(),
            input_cfg: input_config(),
            revision: 0,
            enabled: true,
            mode: Mode::Angle,
            fov: 85.,
            vibration: 0.3,
            drone: None,
            previous: follow_state,
            original: None,
            lock: None,
            fx: None,
            flight_time: 0.,
            status: "ready".into(),
        })
    }

    fn status(&self) -> Value {
        let drone = self.drone.as_ref().map(|d| {
            json!({
                "mode": d.mode.name(),
                "speed": d.velocity.len(),
                "position": [d.position.0, d.position.1, d.position.2],
                "disarmed": d.disarmed > 0.,
            })
        });
        json!({
            "hooked": LIVE.load(Ordering::Acquire),
            "flying": self.drone.is_some(),
            "drone": drone,
            "locked": self.lock.as_ref().map(|l| l.name.clone()),
            "gamepad": self.input.pad_connected,
            "mouse": self.mouse.is_some(),
            "status": self.status,
        })
    }

    unsafe fn cam_state(&self, follow: *mut Object) -> R<i32> {
        symbols::get::<i32>(follow, "camera.follow.mode")
    }
    /// The game's setter runs its enter/exit work (cursor, orbit flags); the field write is the
    /// fallback when the setter didn't resolve or didn't take.
    unsafe fn set_cam_state(&self, follow: *mut Object, mut value: i32) -> R<()> {
        if symbols::method("fpv.follow.set_mode").is_ok() {
            symbols::call(follow, "fpv.follow.set_mode", &mut [&mut value as *mut i32 as _])?;
        }
        if self.cam_state(follow)? != value {
            symbols::set(follow, "camera.follow.mode", value)?;
        }
        Ok(())
    }

    fn refresh(&mut self) {
        let revision = REVISION.load(Ordering::Acquire);
        if revision == self.revision {
            return;
        }
        self.revision = revision;
        self.cfg = config();
        self.input_cfg = input_config();
        self.enabled = settings::enabled();
        self.fov = settings::get_f64("fov") as f32;
        self.vibration = settings::get_f64("vibration") as f32;
        self.mode = Mode::parse(&settings::get_string("mode"));
        if let Some(d) = self.drone.as_mut() {
            d.set_mode(self.mode, &self.cfg);
        }
    }

    unsafe fn frame(&mut self, follow: *mut Object) -> R<()> {
        self.refresh();
        let focused = focus() && self.enabled;
        let time = unity::class(unity::CORE, "UnityEngine", "Time")?;
        let dt = il2cpp::unbox::<f32>(unity::call(time, null_mut(), "get_deltaTime", &mut [])?);
        let real_dt = il2cpp::unbox::<f32>(unity::call(time, null_mut(), "get_unscaledDeltaTime", &mut [])?).clamp(0., 0.1);
        let mouse = match (&self.mouse, self.drone.is_some() && focused) {
            (Some(m), true) => m.read().unwrap_or_else(|e| {
                // Mouse look is optional: lose it, keep flying.
                log::info(format!("fpv: mouse look stopped: {e}"));
                self.mouse = None;
                Mouse::default()
            }),
            _ => Mouse::default(),
        };
        let (controls, actions) = self.input.update(real_dt, focused, mouse, &self.input_cfg);
        let toggle = actions.toggle || TOGGLE.swap(false, Ordering::AcqRel);
        let state = self.cam_state(follow)?;

        if self.drone.is_none() {
            if toggle && self.enabled {
                self.take_off(follow, state)?;
            }
            if self.drone.is_none() {
                self.status = "landed".into();
                return Ok(());
            }
        } else if toggle || state != self.orbit_state || !self.enabled {
            let why = if state != self.orbit_state { "the game left the drone view" } else { "landed" };
            self.land(follow, state == self.orbit_state, why);
            return Ok(());
        }

        self.act(follow, &actions)?;
        let target = self.track(dt)?;
        let paused = dt < 1e-4 || !focused;
        let mut crashed = false;
        if !paused {
            self.flight_time += dt;
            let mut world = Cast { physics: &self.physics, mask: self.mask, error: None };
            let Some(drone) = self.drone.as_mut() else { return Ok(()) };
            let report = drone.step(dt, &controls, target, &mut world, &self.cfg);
            let position = drone.position;
            if let Some(e) = world.error {
                return Err(e);
            }
            if report.crashed {
                crashed = true;
                log::info(format!("fpv: crashed at {:.0} m/s", report.impact));
            }
            let lost = match self.rider(follow)? {
                Some((p, _)) => {
                    let d = position - p;
                    d.len() > 4000. || (report.ground.is_none() && d.1 < -150.)
                }
                None => false,
            };
            if report.invalid || lost {
                log::info("fpv: drone lost; returning to the rider");
                self.recall(follow)?;
            }
        }
        self.apply()?;
        self.show(real_dt, paused, crashed);
        if let Some(d) = &self.drone {
            self.status = format!(
                "flying {} {:.0} km/h{}{}",
                d.mode.name(),
                d.velocity.len() * 3.6,
                self.lock.as_ref().map(|l| format!(", locked on {}", l.name)).unwrap_or_default(),
                if paused { " (paused)" } else { "" },
            );
        }
        Ok(())
    }

    /// OSD and sound. A failure only switches them off; the drone keeps flying.
    unsafe fn show(&mut self, real_dt: f32, paused: bool, crashed: bool) {
        let (Some(d), Some(fx)) = (self.drone.as_ref(), self.fx.as_mut()) else { return };
        // Height over whatever is below, up to 300 m.
        let altitude = self.physics.cast(d.position, V3(0., -1., 0.), 300., 0., self.mask).ok().flatten().map(|h| h.distance);
        let osd = fx::Osd {
            mode: d.mode.name(),
            power: d.power(&self.cfg),
            speed: d.velocity.len(),
            altitude,
            flight_time: self.flight_time,
            locked: self.lock.as_ref().map(|l| l.name.clone()),
            disarmed: d.disarmed > 0.,
            paused,
            crosshair: d.mode != Mode::Cine,
            gamepad: self.input.pad_connected,
        };
        let volume = settings::get_f64("volume") as f32;
        if let Err(e) = fx.update(real_dt, &osd, volume, crashed) {
            log::info(format!("fpv: OSD and sound stopped: {e}"));
            fx.destroy();
            self.fx = None;
        }
    }

    unsafe fn take_off(&mut self, follow: *mut Object, state: i32) -> R<()> {
        if state != self.follow_state && state != self.orbit_state {
            log::info("fpv: switch back to the normal follow camera to take off");
            return Ok(());
        }
        if symbols::get::<bool>(follow, "camera.follow.lock_input")? {
            log::info("fpv: the camera is locked right now (menu or cutscene)");
            return Ok(());
        }
        let cam = unity::gc_target(self.camera)?;
        let tr = unity::gc_target(self.transform)?;
        if unity::is_null(cam) || unity::is_null(tr) {
            return Err("camera destroyed".into());
        }
        let position = v3(unity::call_unbox::<Vec3>(tr, "get_position")?);
        let [x, y, z, w] = unity::call_unbox::<[f32; 4]>(tr, "get_rotation")?;
        let original = (unity::call_unbox::<f32>(cam, "get_fieldOfView")?, unity::call_unbox::<f32>(cam, "get_nearClipPlane")?);
        if state != self.orbit_state {
            self.set_cam_state(follow, self.orbit_state)?;
        }
        self.previous = state;
        self.original = Some(original);
        self.drone = Some(Drone::new(position, Quat(x, y, z, w).normalized(), self.mode));
        self.flight_time = 0.;
        let (fx, errors) = fx::Fx::new(settings::get_bool("hud"), settings::get_bool("sound"));
        for e in errors {
            log::info(format!("fpv: {e}"));
        }
        self.fx = Some(fx);
        log::info(format!("fpv: take off in {} mode", self.mode.name()));
        Ok(())
    }

    /// Restores the lens and (when asked) the camera state the drone took off from.
    unsafe fn land(&mut self, follow: *mut Object, restore_state: bool, why: &str) {
        if let Ok(cam) = unity::gc_target(self.camera) {
            if let (false, Some((mut fov, mut near))) = (unity::is_null(cam), self.original.take()) {
                let _ = unity::callv(cam, "set_fieldOfView", &mut [&mut fov as *mut f32 as _]);
                let _ = unity::callv(cam, "set_nearClipPlane", &mut [&mut near as *mut f32 as _]);
            }
        }
        self.original = None;
        if restore_state && !unity::is_null(follow) && self.previous != self.orbit_state {
            if let Err(e) = self.set_cam_state(follow, self.previous) {
                log::info(format!("fpv: could not restore the camera: {e}"));
            }
        }
        self.drone = None;
        self.unlock();
        if let Some(mut fx) = self.fx.take() {
            fx.destroy();
        }
        log::info(format!("fpv: {why}"));
    }

    unsafe fn release(&mut self, restore: bool) {
        if self.drone.is_some() {
            let follow = unity::gc_target(self.follow).unwrap_or(null_mut());
            self.land(follow, restore, "landed (mod unloaded)");
        }
        self.unlock();
        for h in [self.follow, self.camera, self.transform] {
            let _ = unity::gc_free(h);
        }
        self.follow = 0;
        self.camera = 0;
        self.transform = 0;
    }

    unsafe fn act(&mut self, follow: *mut Object, a: &Actions) -> R<()> {
        if a.mode {
            if let Some(d) = self.drone.as_mut() {
                d.set_mode(d.mode.next(), &self.cfg);
                self.mode = d.mode;
                log::info(format!("fpv: {} mode", d.mode.name()));
            }
        }
        if a.unlock && self.lock.is_some() {
            log::info("fpv: target released");
            self.unlock();
        } else if a.lock {
            self.pick_target(follow)?;
        }
        if a.recall {
            self.recall(follow)?;
        }
        if a.tilt != 0 && self.drone.as_ref().is_some_and(|d| d.mode != Mode::Cine) {
            self.cfg.uptilt = (self.cfg.uptilt + 5. * a.tilt as f32).clamp(0., 70.);
            log::info(format!("fpv: camera uptilt {:.0} deg", self.cfg.uptilt));
        }
        if a.fov != 0 {
            self.fov = (self.fov + 5. * a.fov as f32).clamp(50., 130.);
        }
        Ok(())
    }

    /// The local rider's tracked point and facing.
    unsafe fn rider(&self, follow: *mut Object) -> R<Option<(V3, V3)>> {
        let target = symbols::get_obj(follow, "camera.follow.target")?;
        if unity::is_null(target) {
            return Ok(None);
        }
        let p = v3(unity::call_unbox::<Vec3>(target, "get_position")?);
        let f = v3(unity::call_unbox::<Vec3>(target, "get_forward")?);
        Ok(p.finite().then_some((p, f)))
    }

    unsafe fn recall(&mut self, follow: *mut Object) -> R<()> {
        let locked = self.lock.as_ref().map(|l| (l.last, l.velocity));
        let to = match locked {
            Some((p, v)) => Some((p, v)),
            None => self.rider(follow)?,
        };
        if let (Some((p, forward)), Some(d)) = (to, self.drone.as_mut()) {
            d.recall(p, forward);
        }
        Ok(())
    }

    unsafe fn candidates(&self, follow: *mut Object) -> R<Vec<Candidate>> {
        let mut out = Vec::new();
        let local = symbols::get_obj(follow, "camera.follow.target")?;
        let mut local_pos = None;
        if !unity::is_null(local) {
            local_pos = Some(v3(unity::call_unbox::<Vec3>(local, "get_position")?));
            out.push(Candidate { transform: local, id: instance_id(local)?, name: "you".into() });
        }
        let Some(k) = self.ragdoll else { return Ok(out) };
        let own = symbols::get_obj(follow, "fpv.follow.target_ragdoll").ok().filter(|r| !unity::is_null(*r));
        let own = match own {
            Some(r) => Some(instance_id(r)?),
            None => None,
        };
        for rc in unity::find_objects(k as *const Class)? {
            if unity::is_null(rc) || Some(instance_id(rc)?) == own {
                continue;
            }
            let point = match self.bone(rc)? {
                Some(b) => b,
                None => unity::callv(rc, "get_transform", &mut [])?,
            };
            let p = v3(unity::call_unbox::<Vec3>(point, "get_position")?);
            if local_pos.is_some_and(|l| (l - p).len() < 1.5) || !p.finite() {
                continue;
            }
            out.push(Candidate { transform: point, id: instance_id(point)?, name: unity::name(rc) });
        }
        Ok(out)
    }

    /// The best body point to track on a rider's ragdoll.
    unsafe fn bone(&self, rc: *mut Object) -> R<Option<*mut Object>> {
        let Some(m) = self.children else { return Ok(None) };
        let tk = unity::class(unity::CORE, "UnityEngine", "Transform")?;
        let mut inactive = true;
        let arr = il2cpp::invoke(m as *const Method, rc, &mut [unity::type_object(tk), &mut inactive as *mut bool as _])?;
        let items: Vec<(String, *mut Object)> =
            unity::array_items(arr).into_iter().map(|t| (unity::name(t).to_lowercase(), t)).collect();
        Ok(BONES.iter().find_map(|b| items.iter().find(|(n, _)| n.contains(b)).map(|(_, t)| *t)))
    }

    /// Locks the target nearest the centre of the view, or cycles to the next one.
    unsafe fn pick_target(&mut self, follow: *mut Object) -> R<()> {
        let Some((eye, forward)) = self.drone.as_ref().map(|d| (d.position, d.camera(&self.cfg, 0.).forward())) else {
            return Ok(());
        };
        let mut list: Vec<(f32, Candidate, V3)> = Vec::new();
        for c in self.candidates(follow)? {
            let p = v3(unity::call_unbox::<Vec3>(c.transform, "get_position")?);
            let angle = forward.dot((p - eye).unit()).clamp(-1., 1.).acos();
            list.push((angle, c, p));
        }
        if list.is_empty() {
            log::info("fpv: no rider to lock onto");
            return Ok(());
        }
        list.sort_by(|a, b| a.0.total_cmp(&b.0));
        let next = match &self.lock {
            Some(l) => list.iter().position(|(_, c, _)| c.id == l.id).map_or(0, |i| (i + 1) % list.len()),
            None => 0,
        };
        let (_, c, p) = list.swap_remove(next);
        self.unlock();
        let handle = unity::gc_handle(c.transform)?;
        log::info(format!("fpv: locked on {} ({} rider(s) in range)", c.name, list.len() + 1));
        self.lock = Some(Locked { handle, id: c.id, name: c.name, last: p, velocity: V3::default() });
        if let Some(d) = self.drone.as_mut() {
            d.lock(Some(p));
        }
        Ok(())
    }

    unsafe fn unlock(&mut self) {
        if let Some(l) = self.lock.take() {
            let _ = unity::gc_free(l.handle);
        }
        if let Some(d) = self.drone.as_mut() {
            d.lock(None);
        }
    }

    /// Follows the locked target and estimates its velocity, letting go if it disappears.
    unsafe fn track(&mut self, dt: f32) -> R<Option<Target>> {
        let Some(l) = self.lock.as_mut() else { return Ok(None) };
        let tr = unity::gc_target(l.handle)?;
        let p = if unity::is_null(tr) { None } else { Some(v3(unity::call_unbox::<Vec3>(tr, "get_position")?)) };
        let Some(p) = p.filter(|p| p.finite()) else {
            log::info(format!("fpv: lost {}", l.name));
            self.unlock();
            return Ok(None);
        };
        if dt > 1e-4 {
            let moved = p - l.last;
            if moved.len() > 80. * dt + 5. {
                // Respawn or rewind: don't fling the drone after a teleport.
                l.velocity = V3::default();
            } else {
                l.velocity += (moved * (1. / dt) - l.velocity) * blend(dt, 0.15);
            }
        }
        l.last = p;
        Ok(Some(Target { position: p, velocity: l.velocity }))
    }

    unsafe fn apply(&mut self) -> R<()> {
        let Some(d) = self.drone.as_ref() else { return Ok(()) };
        let cam = unity::gc_target(self.camera)?;
        let tr = unity::gc_target(self.transform)?;
        if unity::is_null(cam) || unity::is_null(tr) {
            return Err("camera destroyed".into());
        }
        let mut position = unity_vec(d.position);
        let mut rotation = d.camera(&self.cfg, self.vibration).to_array();
        unity::callv(tr, "SetPositionAndRotation", &mut [&mut position as *mut Vec3 as _, rotation.as_mut_ptr() as _])?;
        let mut fov = self.fov;
        let mut near = NEAR_CLIP;
        unity::callv(cam, "set_fieldOfView", &mut [&mut fov as *mut f32 as _])?;
        unity::callv(cam, "set_nearClipPlane", &mut [&mut near as *mut f32 as _])?;
        Ok(())
    }
}

unsafe extern "C" fn on_frame(this: *mut c_void, _arg: *mut c_void) {
    if !LIVE.load(Ordering::Acquire) || this.is_null() {
        return;
    }
    let outcome = std::panic::catch_unwind(|| unsafe { frame(this) });
    let error = match outcome {
        Ok(Ok(())) => return,
        Ok(Err(e)) => e,
        Err(_) => "panic in drone callback".into(),
    };
    if let Ok(mut state) = STATE.lock() {
        if let Some(s) = state.as_mut() {
            if s.drone.is_some() {
                s.land(this, true, "landed after an error");
            }
            s.status = error.clone();
        }
    }
    // An API mismatch is permanent for this build; don't retry every frame.
    LIVE.store(false, Ordering::Release);
    log::info(format!("fpv: stopped safely: {error}"));
}
unsafe fn frame(follow: *mut Object) -> R<()> {
    let mut state = STATE.lock().map_err(|_| "drone state lock poisoned")?;
    let owner = instance_id(follow)?;
    if state.as_ref().is_none_or(|s| s.owner != owner) {
        if let Some(mut old) = state.take() {
            // The old camera went with its scene; there is nothing to restore on it.
            old.release(false);
        }
        *state = Some(State::new(follow)?);
    }
    if let Some(s) = state.as_mut() {
        s.frame(follow)?;
    }
    Ok(())
}
