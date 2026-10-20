//! Drone sounds, synthesised so the mod ships no audio files. Loops are exactly one second long
//! with whole-number frequencies, so they repeat without a click; noise loops are crossfaded.
//! Pitch and volume follow the motors at runtime (`motor_voice`, `wind_voice`).
use std::f32::consts::TAU;

pub const RATE: usize = 44_100;

/// Deterministic white noise in -1..1.
struct Noise(u32);
impl Noise {
    fn next(&mut self) -> f32 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 17;
        self.0 ^= self.0 << 5;
        (self.0 as f32 / u32::MAX as f32) * 2. - 1.
    }
}

/// `len` samples of one-pole low-passed noise that loops without a seam.
fn noise_loop(len: usize, seed: u32, cutoff_hz: f32) -> Vec<f32> {
    let fade = len / 8;
    let mut noise = Noise(seed);
    let a = 1. - (-TAU * cutoff_hz / RATE as f32).exp();
    let mut y = 0.;
    let raw: Vec<f32> = (0..len + fade)
        .map(|_| {
            y += (noise.next() - y) * a;
            y
        })
        .collect();
    // The tail past `len` blends into the head, so the last sample leads into the first.
    let mut out = raw[..len].to_vec();
    for i in 0..fade {
        let t = i as f32 / fade as f32;
        out[i] = raw[len + i] * (1. - t) + raw[i] * t;
    }
    out
}

fn normalize(mut v: Vec<f32>, peak: f32) -> Vec<f32> {
    let max = v.iter().fold(0f32, |m, x| m.max(x.abs()));
    if max > 0. {
        v.iter_mut().for_each(|x| *x *= peak / max);
    }
    v
}

/// Four slightly detuned motors (the beating is what makes a quad sound like a quad), each a
/// band-limited sawtooth, plus prop wash. Played at pitch 1 it idles around 190 Hz.
pub fn motor_loop() -> Vec<f32> {
    let motors = [188., 191., 186., 194.];
    let wash = noise_loop(RATE, 0x9e37_79b9, 2_500.);
    let samples = (0..RATE)
        .map(|i| {
            let t = i as f32 / RATE as f32;
            let tone: f32 = motors
                .iter()
                .map(|f| (1..=12).map(|h| (TAU * f * h as f32 * t).sin() / h as f32).sum::<f32>())
                .sum();
            tone * 0.2 + wash[i] * 0.9
        })
        .collect();
    normalize(samples, 0.8)
}

/// Rushing air: darker noise, louder with speed.
pub fn wind_loop() -> Vec<f32> {
    normalize(noise_loop(RATE, 0x85eb_ca6b, 600.), 0.8)
}

/// A short plastic-and-carbon thump for a crash.
pub fn crash_clip() -> Vec<f32> {
    let len = RATE * 35 / 100;
    let mut noise = Noise(0xc2b2_ae35);
    let mut phase = 0f32;
    let samples = (0..len)
        .map(|i| {
            let t = i as f32 / RATE as f32;
            phase += TAU * (140. - 260. * t).max(45.) / RATE as f32;
            phase.sin() * (-t * 14.).exp() + noise.next() * 0.6 * (-t * 30.).exp()
        })
        .collect();
    normalize(samples, 0.9)
}

/// Pitch and volume of the motor loop for a motor output of 0..1. Motors stopped are silent.
pub fn motor_voice(power: f32, armed: bool) -> (f32, f32) {
    if !armed {
        return (0.4, 0.);
    }
    let p = power.clamp(0., 1.);
    (0.7 + 1.5 * p.sqrt(), 0.35 + 0.65 * p)
}

/// Pitch and volume of the wind loop at a speed in m/s.
pub fn wind_voice(speed: f32) -> (f32, f32) {
    let s = (speed / 40.).clamp(0., 1.);
    (0.8 + 0.6 * s, s.powf(1.5) * 0.8)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn loops_are_seamless_and_in_range() {
        for (name, clip) in [("motor", motor_loop()), ("wind", wind_loop())] {
            assert_eq!(clip.len(), RATE, "{name}");
            assert!(clip.iter().all(|x| x.is_finite() && x.abs() <= 1.), "{name}");
            // The jump from the last sample back to the first is no bigger than a normal step.
            let wrap = (clip[0] - clip[RATE - 1]).abs();
            let typical = clip.windows(2).map(|w| (w[1] - w[0]).abs()).fold(0f32, f32::max);
            assert!(wrap <= typical, "{name}: wrap {wrap} vs max step {typical}");
        }
    }
    #[test]
    fn crash_dies_away() {
        let c = crash_clip();
        assert!(c.iter().all(|x| x.abs() <= 1.));
        let tail = c[c.len() - 500..].iter().fold(0f32, |m, x| m.max(x.abs()));
        assert!(tail < 0.05);
    }
    #[test]
    fn motors_rise_with_power_and_stop_when_disarmed() {
        let (p0, v0) = motor_voice(0.1, true);
        let (p1, v1) = motor_voice(0.9, true);
        assert!(p1 > p0 && v1 > v0);
        assert_eq!(motor_voice(1., false).1, 0.);
        assert_eq!(wind_voice(0.).1, 0.);
    }
}
