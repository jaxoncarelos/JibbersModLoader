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

struct State { plan: Plan, primed: bool }
static STATES: Mutex<Option<HashMap<(usize, usize), State>>> = Mutex::new(None);
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
    let fresh = !map.contains_key(&key) || map[&key].plan.ngeom != ngeom;
    if fresh {
        map.insert(key, State { plan: plan(m), primed: false });
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
    if p.stat < 64 || (p.hi - p.lo) as f64 > 0.8 * p.ngeom as f64 {
        drop(g);
        return original(m, d);
    }
    if !st.primed || !poses_valid(d, p.probe) {
        st.primed = true;
        drop(g);
        return original(m, d); // full pass: fills every static pose
    }
    drop(g);
    narrowed(original, m, d, p.lo, p.hi);
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

    #[test]
    fn all_static_has_no_movable_span() {
        let m = model(&[0], &[-1], &[0], &[0; 10]);
        let p = unsafe { plan(m.mem.as_ptr() as *const c_void) };
        assert_eq!((p.lo, p.hi, p.stat), (0, 0, 10));
    }
}
