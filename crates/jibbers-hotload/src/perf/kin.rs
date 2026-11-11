//! Static-geom kinematics: MuJoCo's mj_kinematics recomputes the world pose of EVERY geom each
//! step, including geoms on bodies welded to the world, whose poses never change. On a map with
//! ~100k static colliders that is most of the step. We compute everything once, then run the
//! original mj_kinematics with the geom arrays narrowed to the span that holds the movable geoms
//! (pointer offsets + a smaller ngeom), so static geom poses are simply left as they are.
//! Exactly the same numbers as the unpatched engine: the same code computes the movable geoms.
use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::Mutex;

// MuJoCo 3.3.6 layout (from the game's C# bindings, Mujoco.MujocoLib.mjModel_/mjData_).
pub const M_NBODY: usize = 16;
pub const M_NGEOM: usize = 60;
pub const M_BODY_WELDID: usize = 1384;
pub const M_BODY_MOCAPID: usize = 1392;
pub const M_BODY_PARENTID: usize = 1368;
pub const M_GEOM_BODYID: usize = 1936;
pub const M_GEOM_SAMEFRAME: usize = 1984;
pub const M_GEOM_POS: usize = 2040;
pub const M_GEOM_QUAT: usize = 2048;
pub const M_BODY_POS: usize = 1472;
pub const M_BODY_QUAT: usize = 1480;
pub const D_GEOM_XPOS: usize = 161848;
pub const D_GEOM_XMAT: usize = 161856;

pub type Kin = unsafe extern "C" fn(*const c_void, *mut c_void);

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Plan {
    pub ngeom: i32,
    pub lo: i32, // movable geoms all lie in [lo, hi)
    pub hi: i32,
    pub stat: i32, // static geoms
    pub probe: i32, // a static geom outside [lo,hi) used to detect a reset (-1: none)
}

/// Per (model, data). A static geom's pose depends only on its body's pose (body_pos/quat down the
/// welded chain) and its own geom_pos/quat. The game may change those after the first pass (it
/// places, moves and streams colliders in place on build 26596007+) or recompile the model in
/// place (same struct addresses, new arrays), so every step compares them with the values the last
/// full pass used: a memcmp of a few arrays, far cheaper than recomputing every geom pose.
struct State {
    plan: Plan,
    primed: bool,
    sig: [usize; 6],
    snap: Vec<u8>,
    steps: u64,
    changes: u64,
    off: bool,
}
static STATES: Mutex<Option<HashMap<(usize, usize), State>>> = Mutex::new(None);

/// Array identities: an in-place recompile reallocates them even if the counts stay the same.
unsafe fn signature(m: *const c_void, d: *mut c_void) -> [usize; 6] {
    [rd::<usize>(m, M_GEOM_POS), rd::<usize>(m, M_GEOM_QUAT), rd::<usize>(m, M_BODY_POS), rd::<usize>(m, M_BODY_QUAT),
     rd::<usize>(d, D_GEOM_XPOS), rd::<usize>(d, D_GEOM_XMAT)]
}

/// The model inputs static geom poses depend on, as (what, first index, byte slice, values per item):
/// every body's pos/quat (bodies are few) and the static geoms' pos/quat outside [lo, hi).
unsafe fn inputs<'a>(m: *const c_void, p: &Plan) -> [(&'static str, usize, &'a [u8], usize); 6] {
    let nbody = rd::<i32>(m, M_NBODY).max(0) as usize;
    let (lo, hi, n) = (p.lo as usize, p.hi as usize, p.ngeom as usize);
    let bytes = |off: usize, first: usize, count: usize, per: usize| -> &'a [u8] {
        let base = ptr::<f64>(m, off);
        if base.is_null() || count == 0 { &[] } else { std::slice::from_raw_parts(base.add(first * per) as *const u8, count * per * 8) }
    };
    [("body_pos", 0, bytes(M_BODY_POS, 0, nbody, 3), 3),
     ("body_quat", 0, bytes(M_BODY_QUAT, 0, nbody, 4), 4),
     ("geom_pos", 0, bytes(M_GEOM_POS, 0, lo, 3), 3),
     ("geom_quat", 0, bytes(M_GEOM_QUAT, 0, lo, 4), 4),
     ("geom_pos", hi, bytes(M_GEOM_POS, hi, n - hi, 3), 3),
     ("geom_quat", hi, bytes(M_GEOM_QUAT, hi, n - hi, 4), 4)]
}

unsafe fn snapshot(m: *const c_void, p: &Plan, into: &mut Vec<u8>) {
    into.clear();
    for (_, _, b, _) in inputs(m, p) { into.extend_from_slice(b); }
}

/// The first input that differs from the snapshot, as "array[index]", or None if all match.
unsafe fn changed(m: *const c_void, p: &Plan, snap: &[u8]) -> Option<String> {
    let mut at = 0;
    for (what, first, b, per) in inputs(m, p) {
        let Some(old) = snap.get(at..at + b.len()) else { return Some("layout".into()) };
        if old != b {
            let i = old.iter().zip(b).position(|(x, y)| x != y).unwrap_or(0) / (8 * per);
            return Some(format!("{what}[{}]", first + i));
        }
        at += b.len();
    }
    (at != snap.len()).then(|| "layout".into())
}
/// Where to report each new model's plan (set by the plugin; tests leave it unset).
pub static LOG: std::sync::OnceLock<fn(String)> = std::sync::OnceLock::new();
pub static ENABLED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(true);

unsafe fn rd<T: Copy>(base: *const c_void, off: usize) -> T { *((base as *const u8).add(off) as *const T) }
unsafe fn ptr<T>(base: *const c_void, off: usize) -> *mut T { rd::<*mut T>(base, off) }
unsafe fn slot<T>(base: *const c_void, off: usize) -> *mut *mut T { (base as *mut u8).add(off) as *mut *mut T }

/// Which geoms can move: those on a body that isn't welded to the world, or on a mocap body
/// (or a descendant of one).
pub unsafe fn plan(m: *const c_void) -> Plan {
    let nbody: i32 = rd(m, M_NBODY);
    let ngeom: i32 = rd(m, M_NGEOM);
    let weld = ptr::<i32>(m, M_BODY_WELDID);
    let mocap = ptr::<i32>(m, M_BODY_MOCAPID);
    let parent = ptr::<i32>(m, M_BODY_PARENTID);
    let gbody = ptr::<i32>(m, M_GEOM_BODYID);
    let mut moving = vec![false; nbody.max(0) as usize];
    for b in 1..nbody as usize { // bodies are in tree order: parents come first
        let p = *parent.add(b) as usize;
        moving[b] = *weld.add(b) != 0 || *mocap.add(b) >= 0 || (p < b && moving[p]);
    }
    let (mut lo, mut hi, mut stat) = (ngeom, 0, 0);
    for g in 0..ngeom {
        let b = *gbody.add(g as usize);
        if b >= 0 && b < nbody && moving[b as usize] { lo = lo.min(g); hi = hi.max(g + 1); } else { stat += 1; }
    }
    if lo >= hi { lo = 0; hi = 0; }
    let probe = if lo > 0 { 0 } else if hi < ngeom { ngeom - 1 } else { -1 };
    Plan { ngeom, lo, hi, stat, probe }
}

/// A reset (mj_resetData) zeroes geom_xmat; a valid rotation matrix never has a zero first row.
unsafe fn poses_valid(d: *mut c_void, probe: i32) -> bool {
    if probe < 0 { return true; }
    let xm = ptr::<f64>(d, D_GEOM_XMAT).add(9 * probe as usize);
    let r = *xm * *xm + *xm.add(1) * *xm.add(1) + *xm.add(2) * *xm.add(2);
    r > 0.5
}

/// Drop-in replacement for mj_kinematics; `original` is the real one.
pub unsafe fn kinematics(original: Kin, m: *const c_void, d: *mut c_void) {
    if !ENABLED.load(std::sync::atomic::Ordering::Relaxed) || m.is_null() || d.is_null() {
        return original(m, d);
    }
    let key = (m as usize, d as usize);
    let ngeom: i32 = rd(m, M_NGEOM);
    let mut g = STATES.lock().unwrap_or_else(|e| e.into_inner());
    let map = g.get_or_insert_with(HashMap::new);
    let sig = signature(m, d);
    let fresh = map.get(&key).is_none_or(|s| s.plan.ngeom != ngeom || s.sig != sig);
    if fresh {
        map.insert(key, State { plan: plan(m), primed: false, sig, snap: Vec::new(), steps: 0, changes: 0, off: false });
    }
    let st = map.get_mut(&key).unwrap();
    let p = st.plan;
    if fresh {
        if let Some(log) = LOG.get() {
            let used = p.stat >= 64 && ((p.hi - p.lo) as f64) <= 0.8 * p.ngeom as f64;
            log(format!("{} geoms, {} static; movable ones in [{}, {}): {}", p.ngeom, p.stat, p.lo, p.hi,
                if used { format!("skipping {} static geoms per step", p.ngeom - (p.hi - p.lo)) } else { "not worth it, plain kinematics".into() }));
        }
    }
    // Not worth it (or nothing static): plain call.
    if st.off || p.stat < 64 || (p.hi - p.lo) as f64 > 0.8 * p.ngeom as f64 {
        drop(g);
        return original(m, d);
    }
    st.steps += 1;
    let moved = if st.primed { changed(m, &p, &st.snap) } else { None };
    if !st.primed || moved.is_some() || !poses_valid(d, p.probe) {
        if let Some(what) = moved {
            st.changes += 1;
            if st.changes <= 5 {
                if let Some(log) = LOG.get() {
                    log(format!("static colliders changed in place ({what}, change {} after {} steps): recomputing every pose", st.changes, st.steps));
                }
            }
            // Static colliders that keep moving gain nothing from skipping: stop for this model.
            if st.changes >= 32 && st.changes * 4 >= st.steps {
                st.off = true;
                if let Some(log) = LOG.get() {
                    log(format!("static colliders change on {} of {} steps on this map: skipping off for it", st.changes, st.steps));
                }
            }
        }
        st.primed = true;
        snapshot(m, &p, &mut st.snap); // the inputs this full pass uses
        drop(g);
        return original(m, d); // full pass: fills every static pose
    }
    let verify = VERIFY.load(std::sync::atomic::Ordering::Relaxed) && st.steps % 60 == 0;
    drop(g);
    narrowed(original, m, d, p.lo, p.hi);
    if verify {
        verify_parity(original, m, d, &p);
    }
}

/// Opt-in self-check (JIBBERS_PERF_VERIFY=1): after a narrowed step, recompute every pose with
/// the original engine and compare the static geoms bit for bit with what skipping left in place.
pub static VERIFY: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static CHECKS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
unsafe fn verify_parity(original: Kin, m: *const c_void, d: *mut c_void, p: &Plan) {
    let n = p.ngeom as usize;
    let (xpos, xmat) = (ptr::<f64>(d, D_GEOM_XPOS), ptr::<f64>(d, D_GEOM_XMAT));
    let before: (Vec<f64>, Vec<f64>) = (std::slice::from_raw_parts(xpos, 3 * n).to_vec(), std::slice::from_raw_parts(xmat, 9 * n).to_vec());
    original(m, d); // the unpatched result for this exact state
    let mut bad = 0usize;
    let mut first = None;
    for g in (0..n).filter(|g| (*g as i32) < p.lo || (*g as i32) >= p.hi) {
        let same = (0..3).all(|k| before.0[3 * g + k].to_bits() == (*xpos.add(3 * g + k)).to_bits())
            && (0..9).all(|k| before.1[9 * g + k].to_bits() == (*xmat.add(9 * g + k)).to_bits());
        if !same { bad += 1; first.get_or_insert(g); }
    }
    let k = CHECKS.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1;
    if let Some(log) = LOG.get() {
        if bad > 0 {
            log(format!("parity check {k}: {bad} of {} static geom poses differ from the unpatched engine (first: geom {})", p.stat, first.unwrap_or(0)));
        } else if k % 50 == 1 {
            log(format!("parity check {k}: all {} static geom poses bit-identical to the unpatched engine", p.stat));
        }
    }
}

unsafe fn narrowed(original: Kin, m: *const c_void, d: *mut c_void, lo: i32, hi: i32) {
    let lo_u = lo as usize;
    let mm = m as *mut c_void;
    let s_ngeom = (m as *mut u8).add(M_NGEOM) as *mut i32;
    let saved = (*s_ngeom, *slot::<i32>(m, M_GEOM_BODYID), *slot::<u8>(m, M_GEOM_SAMEFRAME),
        *slot::<f64>(m, M_GEOM_POS), *slot::<f64>(m, M_GEOM_QUAT), *slot::<f64>(d, D_GEOM_XPOS), *slot::<f64>(d, D_GEOM_XMAT));
    *s_ngeom = hi - lo;
    *slot::<i32>(mm, M_GEOM_BODYID) = saved.1.add(lo_u);
    *slot::<u8>(mm, M_GEOM_SAMEFRAME) = saved.2.add(lo_u);
    *slot::<f64>(mm, M_GEOM_POS) = saved.3.add(3 * lo_u);
    *slot::<f64>(mm, M_GEOM_QUAT) = saved.4.add(4 * lo_u);
    *slot::<f64>(d, D_GEOM_XPOS) = saved.5.add(3 * lo_u);
    *slot::<f64>(d, D_GEOM_XMAT) = saved.6.add(9 * lo_u);
    let r = std::panic::catch_unwind(|| original(m, d));
    *s_ngeom = saved.0;
    *slot::<i32>(mm, M_GEOM_BODYID) = saved.1;
    *slot::<u8>(mm, M_GEOM_SAMEFRAME) = saved.2;
    *slot::<f64>(mm, M_GEOM_POS) = saved.3;
    *slot::<f64>(mm, M_GEOM_QUAT) = saved.4;
    *slot::<f64>(d, D_GEOM_XPOS) = saved.5;
    *slot::<f64>(d, D_GEOM_XMAT) = saved.6;
    if let Err(e) = r { std::panic::resume_unwind(e); }
}


// --- install: inline detour on pcore.dll's mj_kinematics ------------------------------------------

static KIN: std::sync::OnceLock<retour::GenericDetour<Kin>> = std::sync::OnceLock::new();

unsafe extern "C" fn original(m: *const c_void, d: *mut c_void) {
    KIN.get().unwrap().call(m, d)
}

unsafe extern "C" fn detour(m: *const c_void, d: *mut c_void) {
    kinematics(original, m, d)
}

/// Hooks mj_kinematics. Refuses any MuJoCo but 3.3.6 (the offsets above are for that layout).
pub unsafe fn install(module: windows_sys::Win32::Foundation::HMODULE) -> Result<(), String> {
    let v = super::pcore::version(module)?;
    if v != "3.3.6" {
        return Err(format!("made for MuJoCo 3.3.6, the game has {v}"));
    }
    let _ = LOG.set(|s| super::say(s));
    if std::env::var_os("JIBBERS_PERF_VERIFY").is_some_and(|v| !v.is_empty() && v != "0") {
        VERIFY.store(true, std::sync::atomic::Ordering::Relaxed);
        super::say("static collider parity self-check on (JIBBERS_PERF_VERIFY)");
    }
    let target: Kin = std::mem::transmute(super::pcore::function(module, "mj_kinematics")?);
    let det = retour::GenericDetour::new(target, detour as Kin).map_err(|e| e.to_string())?;
    det.enable().map_err(|e| e.to_string())?;
    let _ = KIN.set(det);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A fake mjModel: only the fields `plan` reads, at MuJoCo 3.3.6 offsets.
    struct FakeModel { mem: Vec<u8>, _arrays: Vec<Vec<i32>> }
    fn model(weld: &[i32], mocap: &[i32], parent: &[i32], geom_body: &[i32]) -> FakeModel {
        let mut mem = vec![0u8; 4096];
        let arrays = vec![weld.to_vec(), mocap.to_vec(), parent.to_vec(), geom_body.to_vec()];
        let put_i32 = |m: &mut Vec<u8>, off: usize, v: i32| m[off..off + 4].copy_from_slice(&v.to_le_bytes());
        let put_ptr = |m: &mut Vec<u8>, off: usize, p: *const i32| m[off..off + 8].copy_from_slice(&(p as usize).to_le_bytes());
        put_i32(&mut mem, M_NBODY, weld.len() as i32);
        put_i32(&mut mem, M_NGEOM, geom_body.len() as i32);
        put_ptr(&mut mem, M_BODY_WELDID, arrays[0].as_ptr());
        put_ptr(&mut mem, M_BODY_MOCAPID, arrays[1].as_ptr());
        put_ptr(&mut mem, M_BODY_PARENTID, arrays[2].as_ptr());
        put_ptr(&mut mem, M_GEOM_BODYID, arrays[3].as_ptr());
        FakeModel { mem, _arrays: arrays }
    }

    #[test]
    fn static_world_geoms_first_movable_last() {
        // bodies: 0 world, 1 welded to world, 2 free (weld 2), 3 child of 2 (weld 2)
        let geoms: Vec<i32> = std::iter::repeat(0).take(1000).chain([1, 1, 2, 3, 3]).collect();
        let m = model(&[0, 0, 2, 2], &[-1, -1, -1, -1], &[0, 0, 0, 2], &geoms);
        let p = unsafe { plan(m.mem.as_ptr() as *const c_void) };
        assert_eq!((p.ngeom, p.lo, p.hi, p.stat, p.probe), (1005, 1002, 1005, 1002, 0));
    }

    #[test]
    fn mocap_bodies_and_their_children_count_as_moving() {
        // body 1 is mocap (weld 0 but moves), body 2 its child
        let geoms = [0, 0, 1, 2, 0];
        let m = model(&[0, 0, 0], &[-1, 0, -1], &[0, 0, 1], &geoms);
        let p = unsafe { plan(m.mem.as_ptr() as *const c_void) };
        assert_eq!((p.lo, p.hi, p.stat), (2, 4, 3));
        assert_eq!(p.probe, 0);
    }

    /// A model + data that `kinematics` can run on: 100 static geoms on body 1 (welded to the
    /// world), then 2 movable geoms on body 2 (free). The fake engine records ngeom per call.
    struct Scene { m: FakeModel, d: Vec<u8>, gpos: Vec<f64>, gquat: Vec<f64>, bpos: Vec<f64>, bquat: Vec<f64>, xpos: Vec<f64>, xmat: Vec<f64> }
    fn scene() -> Scene {
        let geoms: Vec<i32> = std::iter::repeat(1).take(100).chain([2, 2]).collect();
        let m = model(&[0, 0, 2], &[-1, -1, -1], &[0, 0, 0], &geoms);
        let mut s = Scene { m, d: vec![0u8; D_GEOM_XMAT + 8], gpos: vec![0.5; 102 * 3], gquat: vec![1.0; 102 * 4], bpos: vec![0.0; 9], bquat: vec![1.0; 12], xpos: vec![0.0; 102 * 3], xmat: vec![1.0; 102 * 9] };
        s.wire();
        s
    }
    impl Scene {
        fn wire(&mut self) {
            let put = |mem: &mut Vec<u8>, off: usize, p: usize| mem[off..off + 8].copy_from_slice(&p.to_le_bytes());
            put(&mut self.m.mem, M_GEOM_POS, self.gpos.as_ptr() as usize);
            put(&mut self.m.mem, M_GEOM_QUAT, self.gquat.as_ptr() as usize);
            put(&mut self.m.mem, M_BODY_POS, self.bpos.as_ptr() as usize);
            put(&mut self.m.mem, M_BODY_QUAT, self.bquat.as_ptr() as usize);
            put(&mut self.d, D_GEOM_XPOS, self.xpos.as_ptr() as usize);
            put(&mut self.d, D_GEOM_XMAT, self.xmat.as_ptr() as usize);
        }
        /// Runs one step; returns the ngeom the engine saw (102 = full pass, 2 = narrowed).
        fn step(&mut self) -> i32 {
            thread_local!(static SEEN: std::cell::Cell<i32> = const { std::cell::Cell::new(-1) });
            unsafe extern "C" fn engine(m: *const c_void, _d: *mut c_void) { SEEN.with(|s| s.set(rd::<i32>(m, M_NGEOM))); }
            unsafe { kinematics(engine, self.m.mem.as_ptr() as *const c_void, self.d.as_mut_ptr() as *mut c_void) };
            SEEN.with(|s| s.get())
        }
    }

    #[test]
    fn unchanged_static_colliders_are_skipped_after_one_full_pass() {
        let mut s = scene();
        assert_eq!(s.step(), 102, "first step fills every pose");
        assert_eq!(s.step(), 2);
        assert_eq!(s.step(), 2);
    }

    #[test]
    fn a_static_collider_moved_in_place_gets_a_full_pass() {
        let mut s = scene();
        s.step();
        assert_eq!(s.step(), 2);
        s.gpos[3 * 40 + 1] = 9.0; // the game moves static geom 40
        assert_eq!(s.step(), 102, "moved geom pose must be recomputed");
        assert_eq!(s.step(), 2, "and skipping resumes once it is up to date");
        s.bpos[3 + 2] = -4.0; // the game moves static body 1
        assert_eq!(s.step(), 102, "geoms of a moved static body must be recomputed");
        s.gquat[4 * 99] = 0.5;
        assert_eq!(s.step(), 102);
    }

    #[test]
    fn an_in_place_recompile_replans() {
        let mut s = scene();
        s.step();
        assert_eq!(s.step(), 2);
        s.gpos = vec![0.5; 102 * 3]; // same values, new array: the model was rebuilt in place
        s.wire();
        assert_eq!(s.step(), 102);
    }

    #[test]
    fn colliders_that_keep_moving_turn_skipping_off_for_that_model() {
        let mut s = scene();
        for i in 0..64 {
            s.gpos[0] = i as f64;
            assert_eq!(s.step(), 102);
        }
        s.gpos[0] = -1.0;
        assert_eq!(s.step(), 102);
        assert_eq!(s.step(), 102, "skipping stays off for a model whose static colliders keep moving");
    }

    #[test]
    fn all_static_has_no_movable_span() {
        let m = model(&[0], &[-1], &[0], &[0; 10]);
        let p = unsafe { plan(m.mem.as_ptr() as *const c_void) };
        assert_eq!((p.lo, p.hi, p.stat), (0, 0, 10));
    }
}
