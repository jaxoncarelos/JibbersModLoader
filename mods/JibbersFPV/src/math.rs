//! Vector and quaternion math in Unity's conventions: left-handed, Y up, Z forward, quaternions
//! stored (x, y, z, w). Positive X rotation pitches the nose down, positive Y yaws right and
//! positive Z rolls left, exactly as `Quaternion.AngleAxis` does in the game.
use std::ops::{Add, AddAssign, Mul, Neg, Sub, SubAssign};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct V3(pub f32, pub f32, pub f32);

pub const UP: V3 = V3(0., 1., 0.);
pub const FORWARD: V3 = V3(0., 0., 1.);
pub const RIGHT: V3 = V3(1., 0., 0.);

impl Add for V3 {
    type Output = Self;
    fn add(self, b: Self) -> Self {
        V3(self.0 + b.0, self.1 + b.1, self.2 + b.2)
    }
}
impl AddAssign for V3 {
    fn add_assign(&mut self, b: Self) {
        *self = *self + b;
    }
}
impl Sub for V3 {
    type Output = Self;
    fn sub(self, b: Self) -> Self {
        V3(self.0 - b.0, self.1 - b.1, self.2 - b.2)
    }
}
impl SubAssign for V3 {
    fn sub_assign(&mut self, b: Self) {
        *self = *self - b;
    }
}
impl Mul<f32> for V3 {
    type Output = Self;
    fn mul(self, b: f32) -> Self {
        V3(self.0 * b, self.1 * b, self.2 * b)
    }
}
impl Neg for V3 {
    type Output = Self;
    fn neg(self) -> Self {
        V3(-self.0, -self.1, -self.2)
    }
}
impl V3 {
    pub fn dot(self, b: Self) -> f32 {
        self.0 * b.0 + self.1 * b.1 + self.2 * b.2
    }
    pub fn cross(self, b: Self) -> Self {
        V3(
            self.1 * b.2 - self.2 * b.1,
            self.2 * b.0 - self.0 * b.2,
            self.0 * b.1 - self.1 * b.0,
        )
    }
    pub fn len(self) -> f32 {
        self.dot(self).sqrt()
    }
    /// Unit vector, or `fallback` when too short to have a direction.
    pub fn unit_or(self, fallback: Self) -> Self {
        let n = self.len();
        if n > 1e-5 {
            self * (1. / n)
        } else {
            fallback
        }
    }
    pub fn unit(self) -> Self {
        self.unit_or(FORWARD)
    }
    pub fn finite(self) -> bool {
        self.0.is_finite() && self.1.is_finite() && self.2.is_finite()
    }
    pub fn horizontal(self) -> Self {
        V3(self.0, 0., self.2)
    }
    pub fn clamp_len(self, max: f32) -> Self {
        let n = self.len();
        if n > max && n > 0. {
            self * (max / n)
        } else {
            self
        }
    }
    /// Heading in radians about +Y, zero along +Z (`atan2(x, z)`).
    pub fn yaw(self) -> f32 {
        self.0.atan2(self.2)
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Quat(pub f32, pub f32, pub f32, pub f32);

pub const IDENTITY: Quat = Quat(0., 0., 0., 1.);

impl Default for Quat {
    fn default() -> Self {
        IDENTITY
    }
}
impl Mul for Quat {
    type Output = Self;
    /// `a * b` applies `b` first, in `a`'s local frame (Unity's operator).
    fn mul(self, r: Self) -> Self {
        let Quat(lx, ly, lz, lw) = self;
        let Quat(rx, ry, rz, rw) = r;
        Quat(
            lw * rx + lx * rw + ly * rz - lz * ry,
            lw * ry + ly * rw + lz * rx - lx * rz,
            lw * rz + lz * rw + lx * ry - ly * rx,
            lw * rw - lx * rx - ly * ry - lz * rz,
        )
    }
}
impl Mul<V3> for Quat {
    type Output = V3;
    fn mul(self, v: V3) -> V3 {
        let u = V3(self.0, self.1, self.2);
        let t = u.cross(v) * 2.;
        v + t * self.3 + u.cross(t)
    }
}
impl Quat {
    pub fn axis_angle(axis: V3, radians: f32) -> Self {
        let a = axis.unit();
        let (s, c) = (radians * 0.5).sin_cos();
        Quat(a.0 * s, a.1 * s, a.2 * s, c)
    }
    pub fn yaw(radians: f32) -> Self {
        Self::axis_angle(UP, radians)
    }
    /// Positive values pitch the nose down.
    pub fn pitch(radians: f32) -> Self {
        Self::axis_angle(RIGHT, radians)
    }
    /// Positive values roll left.
    pub fn roll(radians: f32) -> Self {
        Self::axis_angle(FORWARD, radians)
    }
    pub fn conj(self) -> Self {
        Quat(-self.0, -self.1, -self.2, self.3)
    }
    pub fn normalized(self) -> Self {
        let n = (self.0 * self.0 + self.1 * self.1 + self.2 * self.2 + self.3 * self.3).sqrt();
        if n > 1e-6 && n.is_finite() {
            Quat(self.0 / n, self.1 / n, self.2 / n, self.3 / n)
        } else {
            IDENTITY
        }
    }
    pub fn finite(self) -> bool {
        self.0.is_finite() && self.1.is_finite() && self.2.is_finite() && self.3.is_finite()
    }
    pub fn forward(self) -> V3 {
        self * FORWARD
    }
    pub fn up(self) -> V3 {
        self * UP
    }
    /// Rotation vector (axis * angle, radians) of this rotation, taking the short way round.
    pub fn to_rotation_vector(self) -> V3 {
        let q = if self.3 < 0. {
            Quat(-self.0, -self.1, -self.2, -self.3)
        } else {
            self
        };
        let v = V3(q.0, q.1, q.2);
        let s = v.len();
        if s < 1e-6 {
            return v * 2.;
        }
        v * (2. * s.atan2(q.3) / s)
    }
    /// Applies a small body-frame rotation vector (radians), as a gyro integration step.
    pub fn rotate_local(self, rotation: V3) -> Self {
        let angle = rotation.len();
        if angle < 1e-7 {
            return self;
        }
        (self * Self::axis_angle(rotation, angle)).normalized()
    }
    pub fn slerp(self, mut b: Self, t: f32) -> Self {
        let mut d = self.0 * b.0 + self.1 * b.1 + self.2 * b.2 + self.3 * b.3;
        if d < 0. {
            b = Quat(-b.0, -b.1, -b.2, -b.3);
            d = -d;
        }
        if d > 0.9995 {
            return Quat(
                self.0 + (b.0 - self.0) * t,
                self.1 + (b.1 - self.1) * t,
                self.2 + (b.2 - self.2) * t,
                self.3 + (b.3 - self.3) * t,
            )
            .normalized();
        }
        let th = d.clamp(-1., 1.).acos();
        let s = th.sin();
        let (wa, wb) = (((1. - t) * th).sin() / s, (t * th).sin() / s);
        Quat(
            self.0 * wa + b.0 * wb,
            self.1 * wa + b.1 * wb,
            self.2 * wa + b.2 * wb,
            self.3 * wa + b.3 * wb,
        )
        .normalized()
    }
    /// The shortest rotation turning direction `a` onto direction `b`.
    pub fn from_to(a: V3, b: V3) -> Self {
        let (a, b) = (a.unit(), b.unit());
        let axis = a.cross(b);
        let angle = a.dot(b).clamp(-1., 1.).acos();
        if axis.len() < 1e-6 {
            return if angle < 1. { IDENTITY } else { Self::axis_angle(a.cross(RIGHT).unit_or(UP), angle) };
        }
        Self::axis_angle(axis, angle)
    }
    pub fn to_array(self) -> [f32; 4] {
        [self.0, self.1, self.2, self.3]
    }
}

/// Frame-rate independent smoothing factor for time constant `tau`.
pub fn blend(dt: f32, tau: f32) -> f32 {
    1. - (-dt / tau.max(1e-3)).exp()
}
pub fn angle_delta(from: f32, to: f32) -> f32 {
    use std::f32::consts::{PI, TAU};
    (to - from + PI).rem_euclid(TAU) - PI
}

#[cfg(test)]
mod tests {
    use super::*;
    fn near(a: V3, b: V3) -> bool {
        (a - b).len() < 1e-4
    }
    #[test]
    fn rotations_match_unity_handedness() {
        let h = std::f32::consts::FRAC_PI_2;
        assert!(near(Quat::pitch(h) * FORWARD, V3(0., -1., 0.)), "+X pitches down");
        assert!(near(Quat::yaw(h) * FORWARD, RIGHT), "+Y yaws right");
        assert!(near(Quat::roll(h) * RIGHT, UP), "+Z rolls left");
    }
    #[test]
    fn rotation_vector_round_trips() {
        let v = V3(0.3, -0.7, 0.2);
        let q = Quat::axis_angle(v, v.len());
        assert!(near(q.to_rotation_vector(), v));
        assert!(near(IDENTITY.rotate_local(v).to_rotation_vector(), v));
    }
    #[test]
    fn slerp_endpoints_and_midpoint() {
        let a = Quat::yaw(0.);
        let b = Quat::yaw(1.);
        assert!(near(a.slerp(b, 0.).forward(), a.forward()));
        assert!(near(a.slerp(b, 1.).forward(), b.forward()));
        assert!(near(a.slerp(b, 0.5).forward(), Quat::yaw(0.5).forward()));
    }
    #[test]
    fn angle_delta_wraps() {
        assert!((angle_delta(3.1, -3.1) - (std::f32::consts::TAU - 6.2)).abs() < 1e-4);
        assert!((angle_delta(0.5, 0.2) + 0.3).abs() < 1e-5);
    }
}
