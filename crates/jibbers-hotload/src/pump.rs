//! Runs queued jobs on Unity's main thread.
//!
//! Unity APIs (TerrainData.SetHeights, scene rebuilds, ...) must be called from the main
//! thread, and touching IL2CPP metadata from another thread during start-up crashes the game.
//! We detour a few managed per-frame methods (`Update`/`LateUpdate`); each call drains the job
//! queue before running the original.

use crate::abi::JobFn;
use crate::il2cpp;
use crate::log;
use retour::RawDetour;
use std::collections::VecDeque;
use std::ffi::c_void;
use std::sync::atomic::{AtomicU8, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

const PENDING: u8 = 0;
const STARTED: u8 = 1;
const CANCELLED: u8 = 2;

struct Job {
    f: JobFn,
    ctx: usize,
    state: Arc<(AtomicU8, Mutex<bool>, Condvar)>,
}

static QUEUE: Mutex<VecDeque<Job>> = Mutex::new(VecDeque::new());

/// Frame hooks: (image, namespace, class, method). Any one that runs each frame is enough;
/// several are hooked so the pump keeps running in menus, loading screens and in game.
const TARGETS: &[(&str, &str, &str, &str)] = &[
    ("UnityEngine.UI.dll", "UnityEngine.EventSystems", "EventSystem", "Update"),
    ("Mujoco.Runtime.dll", "Mujoco", "MjScene", "LateUpdate"),
    ("Assembly-CSharp.dll", "", "TerrainCuller", "Update"),
];

struct Hook(#[allow(dead_code)] RawDetour);
// Installed once on the worker thread and never touched again.
unsafe impl Send for Hook {}

static HOOKS: Mutex<Vec<Hook>> = Mutex::new(Vec::new());
static ORIGINALS: [AtomicUsize; 3] = [AtomicUsize::new(0), AtomicUsize::new(0), AtomicUsize::new(0)];

macro_rules! trampolines {
    ($($i:literal => $name:ident),*) => {
        $(
            unsafe extern "C" fn $name(this: *mut c_void, method: *const c_void) {
                drain();
                let orig = ORIGINALS[$i].load(Ordering::Acquire);
                let f: unsafe extern "C" fn(*mut c_void, *const c_void) = std::mem::transmute(orig);
                f(this, method)
            }
        )*
        const TRAMPOLINES: &[unsafe extern "C" fn(*mut c_void, *const c_void)] = &[$($name),*];
    };
}
trampolines!(0 => hook0, 1 => hook1, 2 => hook2);

fn drain() {
    // One at a time, so a job can queue more work without deadlocking.
    loop {
        let job = QUEUE.lock().unwrap().pop_front();
        let Some(job) = job else { break };
        let (state, done, cv) = &*job.state;
        if state.compare_exchange(PENDING, STARTED, Ordering::AcqRel, Ordering::Acquire).is_err() {
            continue; // cancelled by a timed-out caller
        }
        let (f, ctx) = (job.f, job.ctx);
        if std::panic::catch_unwind(|| unsafe { f(ctx as *mut c_void) }).is_err() {
            log::info("main-thread job panicked");
        }
        *done.lock().unwrap() = true;
        cv.notify_all();
    }
}

/// Installs the frame hooks. Returns how many were installed.
pub unsafe fn install() -> usize {
    let mut hooks = HOOKS.lock().unwrap();
    // Attach retries call this again; hooking a second time would stack detours.
    if !hooks.is_empty() {
        return hooks.len();
    }
    for (i, (img, ns, class, method)) in TARGETS.iter().enumerate() {
        let Some(k) = il2cpp::class(img, ns, class) else {
            log::info(format!("pump: {class} not found"));
            continue;
        };
        let Some(m) = il2cpp::method(k, method, 0) else {
            log::info(format!("pump: {class}.{method} not found"));
            continue;
        };
        // MethodInfo starts with the compiled method pointer.
        let target = *(m as *const *const ());
        match RawDetour::new(target, TRAMPOLINES[i] as *const ()) {
            Ok(d) => {
                ORIGINALS[i].store(d.trampoline() as *const () as usize, Ordering::Release);
                match d.enable() {
                    Ok(()) => {
                        hooks.push(Hook(d));
                        log::info(format!("pump: hooked {class}.{method}"));
                    }
                    Err(e) => log::info(format!("pump: enabling {class}.{method} failed: {e}")),
                }
            }
            Err(e) => log::info(format!("pump: detour {class}.{method} failed: {e}")),
        }
    }
    hooks.len()
}

/// Host callback for the core: runs `f(ctx)` on the main thread and waits for it.
/// Returns 1 (and guarantees the job never runs) if no frame picked it up within 60 s.
pub unsafe extern "C" fn run_main(f: JobFn, ctx: *mut c_void) -> i32 {
    let state = Arc::new((AtomicU8::new(PENDING), Mutex::new(false), Condvar::new()));
    QUEUE.lock().unwrap().push_back(Job { f, ctx: ctx as usize, state: state.clone() });
    let (st, done, cv) = &*state;
    let guard = done.lock().unwrap();
    let (guard, timeout) = cv.wait_timeout_while(guard, Duration::from_secs(60), |d| !*d).unwrap();
    if !timeout.timed_out() {
        return 0;
    }
    drop(guard);
    if st.compare_exchange(PENDING, CANCELLED, Ordering::AcqRel, Ordering::Acquire).is_ok() {
        return 1;
    }
    // It started just as we timed out: wait for it to finish.
    let guard = done.lock().unwrap();
    let _g = cv.wait_while(guard, |d| !*d).unwrap();
    0
}

/// Closure adapter for the shared menu's worker callbacks. Call from workers, not the main thread.
#[cfg(feature = "release")]
pub fn run<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> Result<T, String> {
    run_with(f, run_main)
}

#[cfg(feature = "release")]
fn run_with<T: Send + 'static>(
    f: impl FnOnce() -> T + Send + 'static,
    execute: unsafe extern "C" fn(JobFn, *mut c_void) -> i32,
) -> Result<T, String> {
    type Closure = Box<dyn FnOnce() + Send>;
    unsafe extern "C" fn invoke(ctx: *mut c_void) {
        let job = Box::from_raw(ctx as *mut Closure);
        job();
    }
    let slot = Arc::new(Mutex::new(None));
    let out = slot.clone();
    let job: Closure = Box::new(move || {
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(f));
        *out.lock().unwrap() = Some(result);
    });
    let ctx = Box::into_raw(Box::new(job));
    if unsafe { execute(invoke, ctx as *mut c_void) } != 0 {
        // run_main guarantees a cancelled job can never start: reclaim its closure once.
        unsafe { drop(Box::from_raw(ctx)) };
        return Err("timed out waiting for the game's main thread".into());
    }
    let result = slot.lock().unwrap().take();
    match result {
        Some(Ok(value)) => Ok(value),
        Some(Err(_)) => Err("panic while running on the main thread".into()),
        None => Err("main-thread job did not run".into()),
    }
}

#[cfg(all(test, feature = "release"))]
mod tests {
    use super::*;

    unsafe extern "C" fn execute(job: JobFn, ctx: *mut c_void) -> i32 {
        job(ctx);
        0
    }

    #[test]
    fn closure_returns_its_result() {
        assert_eq!(run_with(|| 42, execute), Ok(42));
    }

    #[test]
    fn closure_panic_is_reported_without_unwinding_across_abi() {
        assert!(run_with(|| panic!("test panic"), execute).unwrap_err().contains("panic"));
    }

    #[test]
    fn cancelled_closure_is_dropped_without_running() {
        unsafe extern "C" fn cancel(_job: JobFn, _ctx: *mut c_void) -> i32 {
            1
        }
        struct Capture(Arc<AtomicUsize>);
        impl Drop for Capture {
            fn drop(&mut self) {
                self.0.fetch_add(1, Ordering::SeqCst);
            }
        }
        let drops = Arc::new(AtomicUsize::new(0));
        let capture = Capture(drops.clone());
        let result = run_with(
            move || {
                drop(capture);
                panic!("cancelled job ran");
            },
            cancel,
        );
        assert!(result.unwrap_err().contains("timed out"));
        assert_eq!(drops.load(Ordering::SeqCst), 1);
    }
}
