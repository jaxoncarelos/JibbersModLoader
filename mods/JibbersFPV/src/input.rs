//! Pilot input: an XInput gamepad, the keyboard, and the mouse as the game's input library
//! (Rewired) reads it.
use crate::flight::Controls;
use windows_sys::Win32::UI::Input::{
    KeyboardAndMouse::GetAsyncKeyState,
    XboxController::{
        XInputGetState, XINPUT_GAMEPAD_DPAD_DOWN, XINPUT_GAMEPAD_DPAD_LEFT, XINPUT_GAMEPAD_DPAD_RIGHT,
        XINPUT_GAMEPAD_DPAD_UP, XINPUT_GAMEPAD_LEFT_SHOULDER, XINPUT_GAMEPAD_LEFT_THUMB,
        XINPUT_GAMEPAD_RIGHT_SHOULDER, XINPUT_GAMEPAD_RIGHT_THUMB, XINPUT_GAMEPAD_Y, XINPUT_STATE,
    },
};

/// Holding the lock button this long clears the lock instead of picking a target.
const HOLD_TO_CLEAR: f32 = 0.45;
/// Polling an empty XInput slot is slow, so look for a newly connected pad only this often.
const RESCAN: f32 = 2.;

const VK_SPACE: u32 = 0x20;
const VK_LEFT: u32 = 0x25;
const VK_UP: u32 = 0x26;
const VK_RIGHT: u32 = 0x27;
const VK_DOWN: u32 = 0x28;
const VK_LSHIFT: u32 = 0xA0;
const VK_LCONTROL: u32 = 0xA2;
const FLIGHT_KEYS: [u32; 13] = [
    b'W' as u32, b'A' as u32, b'S' as u32, b'D' as u32, b'Q' as u32, b'E' as u32, b'C' as u32,
    VK_SPACE, VK_LCONTROL, VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN,
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Throttle {
    CentreHover,
    FullRange,
    Trigger,
}
impl Throttle {
    pub const NAMES: [&'static str; 3] = ["Left stick (centre hover)", "Left stick (full range)", "Right trigger"];
    pub fn parse(s: &str) -> Self {
        match s {
            "Left stick (full range)" => Throttle::FullRange,
            "Right trigger" => Throttle::Trigger,
            _ => Throttle::CentreHover,
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct Keys {
    pub toggle: u32,
    pub mode: u32,
    pub lock: u32,
    pub recall: u32,
    pub tilt_up: u32,
    pub tilt_down: u32,
    pub fov_up: u32,
    pub fov_down: u32,
}

#[derive(Clone, Copy, Debug)]
pub struct InputConfig {
    pub throttle: Throttle,
    pub invert_pitch: bool,
    pub mouse_look: bool,
    /// Degrees of look per mouse count.
    pub mouse_sensitivity: f32,
    pub deadzone: f32,
    pub keys: Keys,
}

/// This frame's mouse movement (counts, +x right, +y up) and right button.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Mouse {
    pub delta: (f32, f32),
    pub right: bool,
}

/// One-shot commands this frame. `tilt` and `fov` count presses (+ up, - down).
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Actions {
    pub toggle: bool,
    pub mode: bool,
    pub lock: bool,
    pub unlock: bool,
    pub recall: bool,
    pub tilt: i32,
    pub fov: i32,
}

#[derive(Default, Clone, Copy)]
struct Pad {
    lx: f32,
    ly: f32,
    rx: f32,
    ry: f32,
    lt: f32,
    rt: f32,
    buttons: u16,
}

#[derive(Default)]
pub struct Input {
    slot: Option<u32>,
    rescan: f32,
    pad_buttons: u16,
    key_down: [bool; 8],
    lock_held: Option<f32>,
    lock_cleared: bool,
    ramps: [f32; 4],
    pub pad_connected: bool,
}

/// Radial deadzone, rescaled so the edge of the deadzone is zero and full tilt is 1.
pub fn deadzone(x: f32, y: f32, dz: f32) -> (f32, f32) {
    let m = (x * x + y * y).sqrt();
    if m <= dz || m < 1e-6 {
        return (0., 0.);
    }
    let scale = ((m - dz) / (1. - dz).max(1e-3)).min(1.) / m;
    (x * scale, y * scale)
}

/// Digital keys as an analogue axis: eases in, snaps back faster.
fn ramp(value: &mut f32, target: f32, dt: f32) {
    let rate = if target == 0. || target.signum() != value.signum() { 9. } else { 5. };
    let step = rate * dt;
    *value = if (target - *value).abs() <= step { target } else { *value + step * (target - *value).signum() };
}

fn down(vk: u32) -> bool {
    vk != 0 && unsafe { GetAsyncKeyState(vk as i32) } as u16 & 0x8000 != 0
}
fn axis(neg: &[u32], pos: &[u32]) -> f32 {
    (pos.iter().any(|&k| down(k)) as i32 - neg.iter().any(|&k| down(k)) as i32) as f32
}

impl Input {
    fn poll_pad(&mut self, dt: f32, dz: f32) -> Option<Pad> {
        self.rescan -= dt;
        let slots: Vec<u32> = match self.slot {
            Some(s) => vec![s],
            None if self.rescan <= 0. => {
                self.rescan = RESCAN;
                (0..4).collect()
            }
            None => vec![],
        };
        for s in slots {
            let mut state: XINPUT_STATE = unsafe { std::mem::zeroed() };
            if unsafe { XInputGetState(s, &mut state) } == 0 {
                self.slot = Some(s);
                let g = state.Gamepad;
                let (lx, ly) = deadzone(g.sThumbLX as f32 / 32767., g.sThumbLY as f32 / 32767., dz);
                let (rx, ry) = deadzone(g.sThumbRX as f32 / 32767., g.sThumbRY as f32 / 32767., dz);
                let trigger = |v: u8| ((v as f32 / 255. - 0.03) / 0.97).clamp(0., 1.);
                return Some(Pad { lx, ly, rx, ry, lt: trigger(g.bLeftTrigger), rt: trigger(g.bRightTrigger), buttons: g.wButtons });
            }
            self.slot = None;
        }
        None
    }

    /// Reads everything for one frame. `focused` gates the keyboard and pad (the game window
    /// must be in front); `dt` is real time so hotkeys still work while the game is paused.
    pub fn update(&mut self, dt: f32, focused: bool, mouse: Mouse, cfg: &InputConfig) -> (Controls, Actions) {
        let mut actions = Actions::default();
        let pad = if focused { self.poll_pad(dt, cfg.deadzone) } else { None };
        self.pad_connected = pad.is_some();
        let buttons = pad.map_or(0, |p| p.buttons);
        let pressed = buttons & !self.pad_buttons;
        let chord = XINPUT_GAMEPAD_LEFT_THUMB | XINPUT_GAMEPAD_RIGHT_THUMB;
        actions.toggle = buttons & chord == chord && self.pad_buttons & chord != chord;
        actions.mode = pressed & XINPUT_GAMEPAD_LEFT_SHOULDER != 0;
        actions.recall = pressed & XINPUT_GAMEPAD_Y != 0;
        actions.tilt = (pressed & XINPUT_GAMEPAD_DPAD_UP != 0) as i32 - (pressed & XINPUT_GAMEPAD_DPAD_DOWN != 0) as i32;
        actions.fov = (pressed & XINPUT_GAMEPAD_DPAD_RIGHT != 0) as i32 - (pressed & XINPUT_GAMEPAD_DPAD_LEFT != 0) as i32;
        let pad_lock = buttons & XINPUT_GAMEPAD_RIGHT_SHOULDER != 0;
        self.pad_buttons = buttons;

        let k = cfg.keys;
        let codes = [k.toggle, k.mode, k.lock, k.recall, k.tilt_up, k.tilt_down, k.fov_up, k.fov_down];
        let mut edge = [false; 8];
        let mut key_lock = false;
        for (i, &code) in codes.iter().enumerate() {
            let d = focused && down(code);
            edge[i] = d && !self.key_down[i];
            self.key_down[i] = d;
            if i == 2 {
                key_lock = d;
            }
        }
        actions.toggle |= edge[0];
        actions.mode |= edge[1];
        actions.recall |= edge[3];
        actions.tilt += edge[4] as i32 - edge[5] as i32;
        actions.fov += edge[6] as i32 - edge[7] as i32;

        // Lock: tap to pick (or cycle) a target, hold to let go.
        if pad_lock || key_lock {
            let held = self.lock_held.get_or_insert(0.);
            *held += dt;
            if *held >= HOLD_TO_CLEAR && !self.lock_cleared {
                self.lock_cleared = true;
                actions.unlock = true;
            }
        } else if let Some(held) = self.lock_held.take() {
            actions.lock = held < HOLD_TO_CLEAR && !self.lock_cleared;
            self.lock_cleared = false;
        }

        let mut c = Controls::default();
        if !focused {
            self.ramps = [0.; 4];
            return (c, actions);
        }
        // Keyboard: W/S pitch, A/D roll, Q/E (or arrows) yaw, Space/Ctrl climb.
        let targets = [
            axis(&[b'S' as u32, VK_DOWN], &[b'W' as u32, VK_UP]),
            axis(&[b'A' as u32], &[b'D' as u32]),
            axis(&[b'Q' as u32, VK_LEFT], &[b'E' as u32, VK_RIGHT]),
            axis(&[VK_LCONTROL, b'C' as u32], &[VK_SPACE]),
        ];
        for (v, t) in self.ramps.iter_mut().zip(targets) {
            ramp(v, t, dt);
        }
        let [kp, kr, ky, kc] = self.ramps;
        let keyboard = FLIGHT_KEYS.iter().any(|&key| down(key));
        let invert = if cfg.invert_pitch { -1. } else { 1. };

        let (mut yaw, mut climb, mut roll, mut pitch) = (0., 0., 0., 0.);
        let mut throttle = None;
        if let Some(p) = pad {
            yaw = p.lx;
            roll = p.rx;
            pitch = p.ry;
            match cfg.throttle {
                Throttle::CentreHover => climb = p.ly,
                Throttle::FullRange => {
                    climb = p.ly;
                    throttle = Some((p.ly + 1.) * 0.5);
                }
                Throttle::Trigger => {
                    climb = p.rt - p.lt;
                    throttle = Some(p.rt);
                }
            }
            c.precision = cfg.throttle != Throttle::Trigger && p.lt > 0.5;
            let held = |b: u16| (buttons & b != 0) as i32 as f32;
            c.gimbal = held(XINPUT_GAMEPAD_DPAD_DOWN) - held(XINPUT_GAMEPAD_DPAD_UP);
        }
        if keyboard {
            throttle = None;
        }
        c.throttle = throttle;
        c.yaw = (yaw + ky).clamp(-1., 1.);
        c.climb = (climb + kc).clamp(-1., 1.);
        c.roll = (roll + kr).clamp(-1., 1.);
        c.pitch = ((pitch + kp) * invert).clamp(-1., 1.);
        c.precision |= mouse.right || down(VK_LSHIFT);
        c.gimbal += (focused && down(k.tilt_down)) as i32 as f32 - (focused && down(k.tilt_up)) as i32 as f32;
        if cfg.mouse_look {
            let s = cfg.mouse_sensitivity.to_radians();
            c.look_yaw = mouse.delta.0 * s;
            c.look_pitch = -mouse.delta.1 * s * invert;
        }
        (c, actions)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn deadzone_is_radial_and_rescaled() {
        assert_eq!(deadzone(0.05, 0.05, 0.1), (0., 0.));
        let (x, y) = deadzone(1., 0., 0.1);
        assert!((x - 1.).abs() < 1e-6 && y == 0.);
        let (x, _) = deadzone(0.55, 0., 0.1);
        assert!((x - 0.5).abs() < 1e-6);
    }
    #[test]
    fn keyboard_axes_ease_in_and_snap_back() {
        let mut v = 0.;
        ramp(&mut v, 1., 0.1);
        assert!((v - 0.5).abs() < 1e-6);
        ramp(&mut v, 1., 0.2);
        assert_eq!(v, 1.);
        ramp(&mut v, 0., 0.05);
        assert!((v - 0.55).abs() < 1e-6);
        ramp(&mut v, -1., 1.);
        assert_eq!(v, -1.);
    }
}
