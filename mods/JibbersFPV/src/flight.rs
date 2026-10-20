//! The drone itself, without game objects: flight modes, physics and collision response.
//!
//! Cine mode is the game's own `Drone` logic (Drone.LateUpdate): it seeks a target pose at
//! `min(distance / followTime, sqrt(2 * maxAcceleration * distance))`, steers with an
//! acceleration clamped to `maxAcceleration`, turns with `1 - exp(-turnSpeed * dt)` and tilts the
//! body towards its acceleration up to `maxTilt`. Acro and Angle are a thrust model on top.
use crate::math::*;

pub const G: f32 = 9.81;
/// Longest physics step; frames are split so fast flight cannot tunnel through thin geometry.
const SUBSTEP: f32 = 1. / 240.;
const SKIN: f32 = 0.02;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Mode {
    Acro,
    Angle,
    Cine,
}
impl Mode {
    pub const NAMES: [&'static str; 3] = ["Acro", "Angle", "Cine"];
    pub fn next(self) -> Self {
        match self {
            Mode::Acro => Mode::Angle,
            Mode::Angle => Mode::Cine,
            Mode::Cine => Mode::Acro,
        }
    }
    pub fn name(self) -> &'static str {
        Self::NAMES[self as usize]
    }
    pub fn parse(s: &str) -> Self {
        match s {
            "Acro" => Mode::Acro,
            "Cine" => Mode::Cine,
            _ => Mode::Angle,
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct Config {
    pub thrust_to_weight: f32,
    /// Level top speed at full throttle, m/s; sets the drag coefficient.
    pub top_speed: f32,
    /// Betaflight "actual" rates: centre sensitivity and max rate in deg/s, expo 0..1.
    pub rate_center: f32,
    pub rate_max: f32,
    pub expo: f32,
    /// Master scale on every rotation the sticks command (rates, yaw, orbit, gimbal).
    pub sensitivity: f32,
    pub max_angle: f32,
    pub altitude_hold: bool,
    pub climb_speed: f32,
    pub cine_speed: f32,
    /// The game drone's followTime, maxAcceleration and turnSpeed.
    pub follow_time: f32,
    pub max_acceleration: f32,
    pub turn_speed: f32,
    pub follow_target: bool,
    pub chase: bool,
    /// Camera uptilt in Acro and Angle, degrees.
    pub uptilt: f32,
    pub collisions: bool,
    pub crash: bool,
    pub radius: f32,
}
impl Default for Config {
    fn default() -> Self {
        Self {
            thrust_to_weight: 4.5,
            top_speed: 38.,
            rate_center: 150.,
            rate_max: 550.,
            expo: 0.54,
            sensitivity: 0.6,
            max_angle: 50.,
            altitude_hold: true,
            climb_speed: 8.,
            cine_speed: 12.,
            follow_time: 0.4,
            max_acceleration: 25.,
            turn_speed: 4.,
            follow_target: true,
            chase: false,
            uptilt: 25.,
            collisions: true,
            crash: false,
            radius: 0.2,
        }
    }
}

/// One frame of pilot input. Sticks are -1..1: `pitch` > 0 is stick forward (nose down / move
/// forward), `roll` and `yaw` > 0 are right, `climb` > 0 is up.
#[derive(Clone, Copy, Debug, Default)]
pub struct Controls {
    /// Absolute throttle 0..1 when the pilot has one (trigger or full-range stick); otherwise
    /// `climb` is a centred throttle whose middle is hover.
    pub throttle: Option<f32>,
    pub climb: f32,
    pub yaw: f32,
    pub pitch: f32,
    pub roll: f32,
    /// Mouse look this frame, radians (positive: right / down).
    pub look_yaw: f32,
    pub look_pitch: f32,
    /// Gimbal tilt rate in Cine, -1..1 (positive: down).
    pub gimbal: f32,
    pub precision: bool,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Hit {
    pub distance: f32,
    pub normal: V3,
}
/// Collision queries against the map.
pub trait World {
    /// Sphere sweep (a ray when `radius` is 0) along the unit `direction`.
    fn sweep(&mut self, origin: V3, direction: V3, length: f32, radius: f32) -> Option<Hit>;
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Target {
    pub position: V3,
    pub velocity: V3,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Report {
    /// Fastest velocity into a surface this frame, m/s.
    pub impact: f32,
    pub crashed: bool,
    pub ground: Option<f32>,
    /// The state was not finite and has been reset; the caller should recall the drone.
    pub invalid: bool,
}

/// Maps a centred throttle stick so the middle hovers: -1 is motors idle, 1 is full power.
pub fn centre_hover(climb: f32, hover: f32) -> f32 {
    let c = climb.clamp(-1., 1.);
    if c >= 0. {
        hover + (1. - hover) * c
    } else {
        hover * (1. + c)
    }
}

/// Betaflight "actual" rates, in rad/s.
pub fn rate(stick: f32, cfg: &Config) -> f32 {
    let x = stick.clamp(-1., 1.);
    let expo = cfg.expo.clamp(0., 1.);
    let curved = x.abs() * (x.powi(5) * expo + x * (1. - expo));
    let movement = (cfg.rate_max - cfg.rate_center).max(0.);
    (x * cfg.rate_center + movement * curved).to_radians()
}

#[derive(Clone, Debug)]
pub struct Drone {
    pub mode: Mode,
    pub position: V3,
    pub velocity: V3,
    /// Body orientation (world from body).
    pub attitude: Quat,
    /// Body angular velocity, rad/s (x pitch, y yaw, z roll).
    pub rates: V3,
    /// Current thrust acceleration, m/s² (the motors spin up and down, they don't jump).
    pub thrust: f32,
    /// Heading and gimbal pitch (positive down) in Angle/Cine, radians.
    pub heading: f32,
    pub gimbal: f32,
    /// Seconds left before the motors re-arm after a crash.
    pub disarmed: f32,
    /// Locked-target follow offset (drone minus target) and its angle from "behind" in chase.
    pub follow: Option<V3>,
    chase_angle: f32,
    accel: V3,
    sway: Quat,
    time: f32,
}

impl Drone {
    /// A drone hovering where the camera is, facing where it looks.
    pub fn new(position: V3, camera: Quat, mode: Mode) -> Self {
        let f = camera.forward();
        let heading = f.horizontal().unit_or(camera.up().horizontal().unit()).yaw();
        Self {
            mode,
            position,
            velocity: V3::default(),
            attitude: Quat::yaw(heading),
            rates: V3::default(),
            thrust: G,
            heading,
            gimbal: (-f.1).clamp(-1., 1.).asin().clamp(-1.2, 1.4),
            disarmed: 0.,
            follow: None,
            chase_angle: 0.,
            accel: V3::default(),
            sway: IDENTITY,
            time: 0.,
        }
    }

    pub fn set_mode(&mut self, mode: Mode, cfg: &Config) {
        if mode == self.mode {
            return;
        }
        let camera = self.camera(cfg, 0.);
        if mode == Mode::Cine {
            let f = camera.forward();
            self.heading = f.horizontal().unit_or(self.attitude.up().horizontal().unit()).yaw();
            self.gimbal = (-f.1).clamp(-1., 1.).asin().clamp(-1.2, 1.4);
        } else {
            if self.mode == Mode::Cine {
                self.attitude = Quat::yaw(self.heading);
            }
            let f = self.attitude.forward().horizontal();
            self.heading = f.unit_or(-self.attitude.up().horizontal().unit()).yaw();
            self.rates = V3::default();
            self.thrust = self.thrust.max(G);
        }
        self.mode = mode;
    }

    /// Locks onto (or releases) a target; the current offset becomes the follow offset.
    pub fn lock(&mut self, target: Option<V3>) {
        self.follow = target.map(|t| {
            let o = self.position - t;
            let h = o.horizontal();
            let flat = h.unit_or(V3(0., 0., -1.)) * h.len().clamp(3., 40.);
            flat + V3(0., o.1.clamp(0.5, 25.), 0.)
        });
        self.chase_angle = 0.;
    }

    /// Puts the drone behind and above `target`, looking at it.
    pub fn recall(&mut self, target: V3, forward: V3) {
        let back = forward.horizontal().unit_or(FORWARD);
        self.position = target - back * 4. + UP * 2.;
        self.velocity = V3::default();
        self.rates = V3::default();
        self.thrust = G;
        self.disarmed = 0.;
        let look = target + UP * 0.5 - self.position;
        self.heading = look.yaw();
        self.gimbal = (-look.unit().1).asin();
        self.attitude = Quat::yaw(self.heading);
        if self.follow.is_some() {
            self.lock(Some(target));
        }
    }

    /// Motor output as a fraction of full thrust (what an OSD shows as throttle).
    pub fn power(&self, cfg: &Config) -> f32 {
        (self.thrust / (cfg.thrust_to_weight.max(1.1) * G)).clamp(0., 1.)
    }

    /// Where the camera looks: uptilted from the body in Acro/Angle, a stabilised gimbal in Cine.
    pub fn camera(&self, cfg: &Config, vibration: f32) -> Quat {
        let base = match self.mode {
            Mode::Cine => self.sway * Quat::yaw(self.heading) * Quat::pitch(self.gimbal),
            _ => self.attitude * Quat::pitch(-cfg.uptilt.to_radians()),
        };
        if vibration <= 0. || self.disarmed > 0. {
            return base;
        }
        // Prop buzz: a fraction of a degree, stronger with throttle.
        let t = self.time;
        let power = 0.35 + (self.thrust / (cfg.thrust_to_weight * G)).clamp(0., 1.);
        let amp = (0.12 * vibration * power).to_radians();
        let jitter = V3(
            (t * 61.3).sin() + 0.5 * (t * 143.7).sin(),
            0.4 * (t * 89.1).sin(),
            (t * 73.9).sin() + 0.5 * (t * 127.3).sin(),
        ) * amp;
        base.rotate_local(jitter)
    }

    pub fn step(
        &mut self,
        dt: f32,
        ctl: &Controls,
        target: Option<Target>,
        world: &mut impl World,
        cfg: &Config,
    ) -> Report {
        let mut report = Report::default();
        let dt = dt.clamp(0., 0.1);
        if dt <= 0. {
            return report;
        }
        self.time += dt;
        let s = if ctl.precision { 0.5 } else { 1. };
        report.ground = world
            .sweep(self.position, -UP, 6., 0.)
            .map(|h| h.distance);
        let ground_effect = report
            .ground
            .map(|h| 1. + 0.18 * (1. - h / 1.2).clamp(0., 1.))
            .unwrap_or(1.);

        // Mouse look acts once per frame.
        match self.mode {
            Mode::Acro => {
                self.attitude = self.attitude.rotate_local(V3(ctl.look_pitch, ctl.look_yaw, 0.))
            }
            Mode::Angle => self.heading += ctl.look_yaw,
            Mode::Cine => {
                self.heading += ctl.look_yaw;
                self.gimbal = (self.gimbal + ctl.look_pitch).clamp(-1.2, 1.4);
            }
        }

        // The follow goal is planned once per frame: keep it on the target's side of walls.
        let goal = match (self.mode, target, self.follow) {
            (Mode::Cine, Some(t), Some(_)) if cfg.follow_target => {
                self.steer_follow(dt, ctl, s, t, cfg);
                Some(self.clear_goal(t, world, cfg))
            }
            _ => None,
        };

        let steps = (dt / SUBSTEP).ceil().clamp(1., 32.) as usize;
        let h = dt / steps as f32;
        for _ in 0..steps {
            match self.mode {
                Mode::Acro | Mode::Angle => self.fly(h, ctl, s, ground_effect, cfg),
                Mode::Cine => self.cine(h, ctl, s, target, goal, report.ground, cfg),
            }
            let impact = self.travel(h, world, cfg);
            report.impact = report.impact.max(impact);
            self.disarmed = (self.disarmed - h).max(0.);
        }
        if cfg.crash && self.mode != Mode::Cine && report.impact > 9. && self.disarmed <= 0. {
            // Hard hit: the motors cut out and the quad tumbles until they re-arm.
            self.disarmed = 1.2;
            self.rates += V3(report.impact * 0.5, report.impact * 0.15, report.impact * 0.35);
            report.crashed = true;
        }
        if !(self.position.finite() && self.velocity.finite() && self.attitude.finite() && self.rates.finite()) {
            self.velocity = V3::default();
            self.rates = V3::default();
            self.attitude = IDENTITY;
            report.invalid = true;
        }
        report
    }

    /// Acro and Angle: rate or self-levelling attitude control, thrust along body up, drag.
    fn fly(&mut self, h: f32, ctl: &Controls, s: f32, ground_effect: f32, cfg: &Config) {
        let max_thrust = cfg.thrust_to_weight.max(1.1) * G;
        let k = cfg.sensitivity.clamp(0.05, 2.);
        let wanted_rates = if self.mode == Mode::Acro {
            V3(rate(ctl.pitch * s, cfg), rate(ctl.yaw * s, cfg), -rate(ctl.roll * s, cfg)) * k
        } else {
            self.heading += (rate(ctl.yaw * s, cfg) * 0.5 * k).clamp(-3., 3.) * h;
            let tilt = cfg.max_angle.clamp(5., 85.).to_radians();
            let level = Quat::yaw(self.heading)
                * Quat::pitch(ctl.pitch * s * tilt)
                * Quat::roll(-ctl.roll * s * tilt);
            let error = (self.attitude.conj() * level).to_rotation_vector();
            (error * 10.).clamp_len(14.)
        };
        // The flight controller follows the sticks within a few tens of milliseconds.
        if self.disarmed <= 0. {
            self.rates += (wanted_rates - self.rates) * blend(h, 0.03);
        }
        self.attitude = self.attitude.rotate_local(self.rates * h);

        let hover = G / max_thrust;
        let wanted = if self.mode == Mode::Angle && cfg.altitude_hold && ctl.throttle.is_none() {
            // Centred stick holds altitude; deflection commands a climb rate.
            let climb = ctl.climb * s * cfg.climb_speed;
            let up = self.attitude.up().1.max(0.3);
            (G + (climb - self.velocity.1) * 3.5) / up
        } else {
            ctl.throttle.unwrap_or_else(|| centre_hover(ctl.climb, hover)) * max_thrust
        };
        let wanted = if self.disarmed > 0. { 0. } else { wanted.clamp(0., max_thrust) };
        self.thrust += (wanted - self.thrust) * blend(h, 0.035);

        let quad = max_thrust * 0.95 / cfg.top_speed.max(5.).powi(2);
        let v = self.velocity;
        let acc = self.attitude.up() * (self.thrust * ground_effect) - UP * G - v * 0.05 - v * (v.len() * quad);
        self.velocity += acc * h;
        self.accel = acc;
    }

    /// Cine: the game drone's pursuit towards a velocity or follow goal, with a stabilised gimbal.
    #[allow(clippy::too_many_arguments)]
    fn cine(
        &mut self,
        h: f32,
        ctl: &Controls,
        s: f32,
        target: Option<Target>,
        goal: Option<V3>,
        ground: Option<f32>,
        cfg: &Config,
    ) {
        let follow_time = cfg.follow_time.max(0.05);
        let mut wanted = if let (Some(goal), Some(t)) = (goal, target) {
            let diff = goal - self.position;
            let distance = diff.len();
            let speed = (distance / follow_time).min((2. * cfg.max_acceleration * distance).sqrt());
            diff.unit_or(V3::default()) * speed + t.velocity
        } else {
            self.heading += ctl.yaw * s * 2. * cfg.sensitivity * h;
            let forward = V3(self.heading.sin(), 0., self.heading.cos());
            let right = V3(forward.2, 0., -forward.0);
            (forward * ctl.pitch + right * ctl.roll).clamp_len(1.) * (cfg.cine_speed * s)
                + UP * (ctl.climb * s * cfg.climb_speed)
        };
        if ground.is_some_and(|g| g < 0.8) {
            wanted.1 = wanted.1.max(0.);
        }
        self.gimbal = (self.gimbal + ctl.gimbal * s * 1.4 * cfg.sensitivity * h).clamp(-1.2, 1.4);
        let accel = ((wanted - self.velocity) * (1. / follow_time)).clamp_len(cfg.max_acceleration);
        self.velocity += accel * h;
        self.accel = accel;

        if let Some(t) = target {
            // Keep the rider framed (chest height), turning like the game drone.
            let look = t.position + UP * 0.4 - self.position;
            if look.len() > 0.5 {
                let k = blend(h, 1. / cfg.turn_speed.max(0.5));
                self.heading += angle_delta(self.heading, look.yaw()) * k;
                let pitch = (-look.unit().1).asin().clamp(-1.2, 1.4);
                self.gimbal += (pitch - self.gimbal) * k;
            }
        }
        // The game's tilt from acceleration, felt through the gimbal as a little sway.
        let lean = UP + accel * 0.035;
        let tilt = Quat::from_to(UP, lean);
        let limited = IDENTITY.slerp(tilt, (35f32.to_radians() / tilt.to_rotation_vector().len().max(1e-4)).min(1.));
        self.attitude = limited * Quat::yaw(self.heading);
        self.sway = self.sway.slerp(IDENTITY.slerp(limited, 0.2), blend(h, 0.15));
        self.thrust = G;
    }

    fn steer_follow(&mut self, dt: f32, ctl: &Controls, s: f32, t: Target, cfg: &Config) {
        let Some(mut o) = self.follow else { return };
        let flat = o.horizontal();
        let distance = (flat.len() * (1. - ctl.pitch * s * 0.9 * dt)).clamp(2., 60.);
        let height = (o.1 + ctl.climb * s * cfg.climb_speed * dt).clamp(-3., 40.);
        let orbit = -ctl.roll * s * 1.5 * cfg.sensitivity * dt;
        let mut direction = Quat::yaw(orbit) * flat.unit_or(V3(0., 0., -1.));
        let moving = t.velocity.horizontal();
        if cfg.chase && moving.len() > 3. {
            // Hold a camera angle relative to "behind the rider", swinging round with their line.
            self.chase_angle += orbit;
            let behind = Quat::yaw(self.chase_angle) * (-moving.unit());
            let turn = angle_delta(direction.yaw(), behind.yaw()) * blend(dt, 1.2);
            direction = Quat::yaw(turn) * direction;
        }
        o = direction * distance + UP * height;
        self.follow = Some(o);
    }

    /// The follow point, pulled in towards the target when a wall or the ground is in the way.
    fn clear_goal(&self, t: Target, world: &mut impl World, cfg: &Config) -> V3 {
        let offset = self.follow.unwrap_or_default();
        let origin = t.position + UP * 0.8;
        let mut goal = t.position + offset;
        let delta = goal - origin;
        let length = delta.len();
        if length > 0.5 {
            if let Some(hit) = world.sweep(origin, delta * (1. / length), length, cfg.radius * 1.5) {
                goal = origin + delta * ((hit.distance - 0.3).max(0.8) / length);
            }
        }
        if let Some(hit) = world.sweep(goal + UP * 2., -UP, 3.2, 0.) {
            goal.1 += 3.2 - hit.distance;
        }
        goal
    }

    /// Moves along the velocity, sliding along whatever is hit. Returns the impact speed.
    fn travel(&mut self, h: f32, world: &mut impl World, cfg: &Config) -> f32 {
        let mut remaining = self.velocity * h;
        let mut impact = 0f32;
        let bounce = if self.mode == Mode::Cine { 0. } else { 0.25 };
        for _ in 0..3 {
            let length = remaining.len();
            if length < 1e-6 {
                break;
            }
            let dir = remaining * (1. / length);
            let hit = if cfg.collisions {
                world.sweep(self.position, dir, length + SKIN, cfg.radius)
            } else {
                None
            };
            let Some(hit) = hit else {
                self.position += remaining;
                break;
            };
            let moved = (hit.distance - SKIN).clamp(0., length);
            self.position += dir * moved;
            let n = hit.normal.unit_or(-dir);
            let into = self.velocity.dot(n);
            if into < 0. {
                impact = impact.max(-into);
                let tangent = self.velocity - n * into;
                self.velocity = tangent * 0.85 - n * (into * bounce);
            }
            remaining = dir * (length - moved);
            remaining -= n * remaining.dot(n).min(0.);
        }
        impact
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Flat ground at y = 0, optionally with a wall at z = `wall`.
    struct Ground {
        wall: Option<f32>,
    }
    impl World for Ground {
        fn sweep(&mut self, o: V3, d: V3, len: f32, r: f32) -> Option<Hit> {
            let mut best: Option<Hit> = None;
            if d.1 < -1e-6 {
                let t = (o.1 - r) / -d.1;
                if (0. ..=len).contains(&t) {
                    best = Some(Hit { distance: t, normal: UP });
                }
            }
            if let Some(w) = self.wall {
                if d.2 > 1e-6 {
                    let t = (w - r - o.2) / d.2;
                    if (0. ..=len).contains(&t) && best.is_none_or(|b| t < b.distance) {
                        best = Some(Hit { distance: t, normal: V3(0., 0., -1.) });
                    }
                }
            }
            best
        }
    }
    fn run(d: &mut Drone, seconds: f32, ctl: Controls, target: Option<Target>, cfg: &Config) -> Report {
        let mut world = Ground { wall: None };
        let mut last = Report::default();
        for _ in 0..(seconds * 60.) as usize {
            last = d.step(1. / 60., &ctl, target, &mut world, cfg);
            assert!(d.position.finite() && d.attitude.finite());
            assert!(d.position.1 >= cfg.radius - 0.03, "never sinks into the ground: {:?}", d.position);
        }
        last
    }

    #[test]
    fn centred_stick_is_hover_and_ends_are_idle_and_full() {
        assert_eq!(centre_hover(0., 0.25), 0.25);
        assert_eq!(centre_hover(1., 0.25), 1.);
        assert_eq!(centre_hover(-1., 0.25), 0.);
    }
    #[test]
    fn rates_reach_max_at_full_stick() {
        let c = Config::default();
        assert!((rate(1., &c) - c.rate_max.to_radians()).abs() < 1e-4);
        assert!((rate(-1., &c) + c.rate_max.to_radians()).abs() < 1e-4);
        assert!(rate(0.5, &c) < rate(1., &c) * 0.5, "expo softens the centre");
    }
    #[test]
    fn acro_centre_throttle_hovers_level() {
        let c = Config::default();
        let mut d = Drone::new(V3(0., 20., 0.), IDENTITY, Mode::Acro);
        run(&mut d, 4., Controls::default(), None, &c);
        assert!((d.position.1 - 20.).abs() < 0.5, "{:?}", d.position);
    }
    #[test]
    fn acro_pitch_flips_and_returns_finite() {
        let c = Config::default();
        let mut d = Drone::new(V3(0., 50., 0.), IDENTITY, Mode::Acro);
        run(&mut d, 0.3, Controls { pitch: 1., ..Default::default() }, None, &c);
        assert!(d.attitude.forward().1 < -0.5 || d.attitude.up().1 < 0., "nose went over");
        run(&mut d, 1., Controls::default(), None, &c);
        assert!(d.rates.len() < 0.2, "stops rotating when the stick centres");
    }
    #[test]
    fn angle_mode_levels_and_holds_altitude() {
        let c = Config::default();
        let mut d = Drone::new(V3(0., 30., 0.), IDENTITY, Mode::Angle);
        run(&mut d, 1.5, Controls { pitch: 1., roll: 0.5, ..Default::default() }, None, &c);
        assert!(d.velocity.horizontal().len() > 5., "tilting moves the quad");
        run(&mut d, 6., Controls::default(), None, &c);
        assert!(d.attitude.up().1 > 0.98, "self levels");
        assert!((d.position.1 - 30.).abs() < 3., "altitude held: {}", d.position.1);
    }
    #[test]
    fn diving_into_the_ground_bounces_and_can_crash() {
        let mut c = Config::default();
        let mut d = Drone::new(V3(0., 15., 0.), Quat::pitch(std::f32::consts::PI), Mode::Acro);
        d.attitude = Quat::pitch(std::f32::consts::PI);
        let r = run(&mut d, 1.5, Controls { throttle: Some(1.), ..Default::default() }, None, &c);
        assert!(r.ground.is_some());
        c.crash = true;
        let mut d = Drone::new(V3(0., 15., 0.), IDENTITY, Mode::Acro);
        d.attitude = Quat::pitch(std::f32::consts::PI);
        let mut world = Ground { wall: None };
        let mut crashed = false;
        for _ in 0..120 {
            crashed |= d.step(1. / 60., &Controls { throttle: Some(1.), ..Default::default() }, None, &mut world, &c).crashed;
        }
        assert!(crashed);
    }
    #[test]
    fn walls_stop_fast_flight() {
        let c = Config::default();
        let mut d = Drone::new(V3(0., 5., 0.), IDENTITY, Mode::Cine);
        let mut world = Ground { wall: Some(30.) };
        for _ in 0..600 {
            d.step(1. / 30., &Controls { pitch: 1., ..Default::default() }, None, &mut world, &c);
            assert!(d.position.2 <= 30. - c.radius + 0.01);
        }
    }
    #[test]
    fn cine_follow_arrives_like_the_game_drone() {
        let c = Config::default();
        let t = Target { position: V3(0., 0., 0.), velocity: V3::default() };
        let mut d = Drone::new(V3(0., 4., -6.), IDENTITY, Mode::Cine);
        d.lock(Some(t.position));
        d.position = V3(30., 12., 20.);
        let mut closest = f32::MAX;
        let mut world = Ground { wall: None };
        for _ in 0..600 {
            d.step(1. / 60., &Controls::default(), Some(t), &mut world, &c);
            closest = closest.min((d.position - V3(0., 4., -6.)).len());
        }
        assert!((d.position - V3(0., 4., -6.)).len() < 0.3, "{:?}", d.position);
        let facing = d.camera(&c, 0.).forward();
        assert!(facing.dot((t.position + UP * 0.4 - d.position).unit()) > 0.99);
    }
    #[test]
    fn cine_keeps_up_with_a_fast_rider() {
        let c = Config { chase: true, ..Config::default() };
        let mut d = Drone::new(V3(0., 3., -6.), IDENTITY, Mode::Cine);
        d.lock(Some(V3(0., 0., 0.)));
        let mut world = Ground { wall: None };
        for i in 0..900 {
            let p = V3(0., 0., i as f32 * (20. / 60.));
            let t = Target { position: p, velocity: V3(0., 0., 20.) };
            d.step(1. / 60., &Controls::default(), Some(t), &mut world, &c);
            if i > 240 {
                assert!((d.position - p).len() < 12., "falls behind: {:?} vs {:?}", d.position, p);
                assert!(d.position.2 < p.2, "chases from behind");
            }
        }
    }
    #[test]
    fn mode_switches_keep_the_view() {
        let c = Config::default();
        let start = Quat::yaw(1.0) * Quat::pitch(0.2);
        let mut d = Drone::new(V3(0., 10., 0.), start, Mode::Acro);
        let before = d.camera(&c, 0.).forward();
        d.set_mode(Mode::Cine, &c);
        assert!(d.camera(&c, 0.).forward().dot(before) > 0.999, "the gimbal takes over the view");
        // Leaving Cine levels the quad (a pitched gimbal must not fling it) but keeps the heading.
        d.set_mode(Mode::Angle, &c);
        assert!((d.heading - 1.0).abs() < 0.01);
        assert!(d.attitude.up().1 > 0.999);
    }
}
