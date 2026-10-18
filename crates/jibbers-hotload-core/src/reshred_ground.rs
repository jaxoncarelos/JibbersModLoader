//! Static MuJoCo ground rays, using the official 3.3.6 double-precision ABI.
//!
//! No native mjModel layout is assumed: offsets come from the IL2CPP value-type
//! mirror. No model/data pointers or terrain IDs are cached across scene changes.
//! This module does not load DLLs, invoke managed wrappers, or change mjState.

use crate::il2cpp::{self, api, Class};
use crate::unity::{self, R};
use std::ffi::c_void;
use std::mem::{align_of, size_of};
use std::ptr;
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress};

const LIFT: f64 = 0.15;
const STEP: f64 = 0.005;
const MAX_SKIPS: usize = 32;
const MAX_ITEMS: usize = 1_000_000;
const HFIELD: i32 = 1;
const DOWN: [f64; 3] = [0.0, 0.0, -1.0];
// IL2CPP x64 boxed value fields include the two-pointer object header.
const BOX_HEADER: usize = 16;

type Version = unsafe extern "C" fn() -> i32;
type Ray = unsafe extern "C" fn(
    *const c_void, *const c_void, *const f64, *const f64,
    *const u8, u8, i32, *mut i32,
) -> f64;
type RayHfield = unsafe extern "C" fn(
    *const c_void, *const c_void, i32, *const f64, *const f64,
) -> f64;

struct Offsets {
    ngeom: usize,
    nbody: usize,
    geom_bodyid: usize,
    geom_type: usize,
    body_weldid: usize,
}

/// Resolved metadata offsets and native function pointers, not scene pointers.
pub struct Ground {
    offsets: Offsets,
    ray: Ray,
    ray_hfield: RayHfield,
}

impl Ground {
    /// Resolve the already-loaded pcore exports and the managed mjModel_ mirror.
    ///
    /// # Safety
    /// The IL2CPP API must be initialized, and the calling thread attached to its
    /// domain. The IL2CPP domain and pcore module must remain loaded while this
    /// object is used. Only the x64, double-precision MuJoCo 3.3.6 build is supported.
    pub unsafe fn resolve() -> R<Self> {
        if size_of::<usize>() != BOX_HEADER / 2 {
            return Err("ground: requires the x64 IL2CPP value-type ABI".into());
        }
        let module = GetModuleHandleA(c"pcore.dll".as_ptr() as _);
        if module.is_null() {
            return Err("ground: pcore.dll is not loaded".into());
        }
        let symbol = |name: &[u8]| {
            GetProcAddress(module, name.as_ptr()).ok_or_else(|| {
                format!("ground: pcore.dll missing {}", String::from_utf8_lossy(&name[..name.len() - 1]))
            })
        };
        let version: Version = std::mem::transmute(symbol(b"mj_version\0")?);
        let actual = version();
        if actual != 336 {
            return Err(format!("ground: requires MuJoCo 336, found {actual}"));
        }
        let ray: Ray = std::mem::transmute(symbol(b"mj_ray\0")?);
        let ray_hfield: RayHfield = std::mem::transmute(symbol(b"mj_rayHfield\0")?);
        let model = model_class()?;
        if !(api().class_is_valuetype)(model) {
            return Err("ground: MujocoLib/mjModel_ is not a value type".into());
        }
        let mut alignment = 0;
        let size = (api().class_value_size)(model, &mut alignment);
        if size <= 0 || size as usize > 16 * 1024 * 1024 {
            return Err(format!("ground: invalid mjModel_ value size {size}"));
        }
        let field = |name, pointer| field_offset(model, size as usize, name, pointer);
        let offsets = Offsets {
            ngeom: field("ngeom", false)?,
            nbody: field("nbody", false)?,
            geom_bodyid: field("geom_bodyid", true)?,
            geom_type: field("geom_type", true)?,
            body_weldid: field("body_weldid", true)?,
        };
        // Distinct native fields must not overlap, even with plausible types.
        let spans = [
            (offsets.ngeom, size_of::<i32>()),
            (offsets.nbody, size_of::<i32>()),
            (offsets.geom_bodyid, size_of::<*const i32>()),
            (offsets.geom_type, size_of::<*const i32>()),
            (offsets.body_weldid, size_of::<*const i32>()),
        ];
        for (i, &(start, width)) in spans.iter().enumerate() {
            if spans[..i].iter().any(|&(s, w)| start < s + w && s < start + width) {
                return Err("ground: overlapping mjModel_ fields".into());
            }
        }
        Ok(Self { offsets, ray, ray_hfield })
    }

    /// Nearest static ground distance below a Unity-space position, in metres.
    /// Returns a signed distance in [-0.15, reach], or None on invalid input,
    /// invalid model indices, no ground, or exhaustion of the dynamic-hit budget.
    /// An independently found static heightfield remains usable if that budget
    /// is exhausted. Invisible heightfields are always tested separately.
    ///
    /// # Safety
    /// `model` and `data` must be live matching mjModel/mjData pointers from the
    /// resolved double-precision 3.3.6 engine, with readable model storage and
    /// arrays matching their counts. mjData must have current geom transforms.
    /// Call on the physics thread with no concurrent mutation, scene destruction,
    /// or module unload. Bounds checks cannot make dangling native pointers safe.
    pub unsafe fn distance(
        &self,
        model: *const c_void,
        data: *const c_void,
        position: unity::Vec3,
        reach: f32,
    ) -> Option<f64> {
        let (origin, limit) = ray_input(position, reach)?;
        if model.is_null() || data.is_null() { return None; }
        let view = self.offsets.view(model)?;

        // Recompute every call: a scene can reuse an old mjModel address.
        // Never pass a dynamic heightfield (including rider geometry) to this
        // supplementary ray path, even if it is the closest geometry.
        let mut nearest: Option<f64> = None;
        for id in 0..view.geom_type.len() {
            if view.geom_type[id] != HFIELD { continue; }
            if !view.is_static(id)? { continue; }
            let hit = (self.ray_hfield)(model, data, id as i32, origin.as_ptr(), DOWN.as_ptr());
            if accepted_distance(hit, f64::from(reach)).is_some() {
                nearest = Some(nearest.map_or(hit, |old| old.min(hit)));
            }
        }

        let mut travelled = 0.0;
        let mut skips = 0;
        loop {
            // There is no need to search beyond a proven static terrain hit.
            let end = nearest.unwrap_or(limit).min(limit);
            if travelled > end { break; }
            let point = [origin[0], origin[1], origin[2] - travelled];
            let mut geomid = -1;
            // Null geomgroup includes every group. flg_static=1 includes static
            // AND dynamic geometry; it is NOT a static-only filter. bodyexclude
            // cannot exclude a whole articulated rider, so inspect every hit.
            let hit = (self.ray)(
                model, data, point.as_ptr(), DOWN.as_ptr(), ptr::null(),
                1, -1, &mut geomid,
            );
            if !hit.is_finite() || hit < 0.0 { break; }
            // Validate every returned ID before indexing, even beyond reach.
            let id = usize::try_from(geomid).ok()?;
            let is_static = view.is_static(id)?;
            let total = travelled + hit;
            if !total.is_finite() || total > end { break; }
            if is_static {
                nearest = Some(nearest.map_or(total, |old| old.min(total)));
                break;
            }
            // Dynamic hits are never accepted, including when hit == 0.
            if skips == MAX_SKIPS { break; }
            skips += 1;
            travelled = total + STEP;
        }
        accepted_distance(nearest?, f64::from(reach))
    }
}

unsafe fn model_class() -> R<*mut Class> {
    let parent = unity::class("Mujoco.Runtime.dll", "Mujoco", "MujocoLib")?;
    let mut iter = ptr::null_mut();
    // Resolve the nested class explicitly; class_from_name need not understand '/'.
    for _ in 0..4096 {
        let nested = (api().class_get_nested_types)(parent, &mut iter);
        if nested.is_null() { break; }
        if il2cpp::cstr((api().class_get_name)(nested)) == "mjModel_" {
            return Ok(nested);
        }
    }
    Err("ground: Mujoco.MujocoLib/mjModel_ missing".into())
}

unsafe fn field_offset(model: *mut Class, size: usize, name: &str, pointer: bool) -> R<usize> {
    let fail = || format!("ground: invalid mjModel_.{name} metadata");
    let field = il2cpp::field(model, name).ok_or_else(fail)?;
    if (api().field_get_flags)(field) & (0x10 | 0x40) != 0 { return Err(fail()); }
    let ty = (api().field_get_type)(field);
    if ty.is_null() { return Err(fail()); }
    // IL2CPP_TYPE_I4 = 0x08, IL2CPP_TYPE_PTR = 0x0f. An IntPtr,
    // array, reference, byref, or pointer to a different element type is rejected.
    let (kind, expected, width) = if pointer {
        (0x0f, "System.Int32*", size_of::<*const i32>())
    } else {
        (0x08, "System.Int32", size_of::<i32>())
    };
    if (api().type_get_type)(ty) != kind || il2cpp::type_name(ty) != expected {
        return Err(fail());
    }
    checked_offset((api().field_get_offset)(field), width, size).ok_or_else(fail)
}

fn checked_offset(boxed: usize, width: usize, size: usize) -> Option<usize> {
    let offset = boxed.checked_sub(BOX_HEADER)?;
    (offset.checked_add(width)? <= size).then_some(offset)
}

impl Offsets {
    unsafe fn view<'a>(&self, model: *const c_void) -> Option<ModelView<'a>> {
        let ngeom = count(read_at::<i32>(model, self.ngeom)?)?;
        let nbody = count(read_at::<i32>(model, self.nbody)?)?;
        Some(ModelView {
            geom_bodyid: int_array(read_at(model, self.geom_bodyid)?, ngeom)?,
            geom_type: int_array(read_at(model, self.geom_type)?, ngeom)?,
            body_weldid: int_array(read_at(model, self.body_weldid)?, nbody)?,
        })
    }
}

unsafe fn read_at<T: Copy>(model: *const c_void, offset: usize) -> Option<T> {
    let address = (model as usize).checked_add(offset)?;
    address.checked_add(size_of::<T>())?;
    Some(ptr::read_unaligned(model.cast::<u8>().add(offset).cast::<T>()))
}

unsafe fn int_array<'a>(pointer: *const i32, len: usize) -> Option<&'a [i32]> {
    if pointer.is_null() || (pointer as usize) % align_of::<i32>() != 0 { return None; }
    let bytes = len.checked_mul(size_of::<i32>())?;
    if bytes > isize::MAX as usize { return None; }
    (pointer as usize).checked_add(bytes)?;
    Some(std::slice::from_raw_parts(pointer, len))
}

fn count(value: i32) -> Option<usize> {
    let value = usize::try_from(value).ok()?;
    (1..=MAX_ITEMS).contains(&value).then_some(value)
}

struct ModelView<'a> {
    geom_bodyid: &'a [i32],
    geom_type: &'a [i32],
    body_weldid: &'a [i32],
}

impl ModelView<'_> {
    fn is_static(&self, geomid: usize) -> Option<bool> {
        // No geomid/bodyid/weldid is used without checking its own bounds.
        self.geom_type.get(geomid)?;
        let body = usize::try_from(*self.geom_bodyid.get(geomid)?).ok()?;
        let weld = usize::try_from(*self.body_weldid.get(body)?).ok()?;
        if weld >= self.body_weldid.len() { return None; }
        Some(body == 0 || weld == 0)
    }
}

fn ray_input(position: unity::Vec3, reach: f32) -> Option<([f64; 3], f64)> {
    if !position.x.is_finite() || !position.y.is_finite() || !position.z.is_finite()
        || !reach.is_finite() || reach < 0.0 {
        return None;
    }
    // Unity (x,y,z) -> MuJoCo (x,z,y), with lift along MuJoCo +Z.
    Some(([
        f64::from(position.x), f64::from(position.z), f64::from(position.y) + LIFT,
    ], f64::from(reach) + LIFT))
}

fn accepted_distance(ray_distance: f64, reach: f64) -> Option<f64> {
    if !ray_distance.is_finite() || !reach.is_finite() || reach < 0.0
        || ray_distance < 0.0 || ray_distance > reach + LIFT {
        return None;
    }
    let distance = ray_distance - LIFT;
    (distance >= -LIFT && distance <= reach).then_some(distance)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn converts_coordinates_and_lifts_in_double_precision() {
        let (p, limit) = ray_input(unity::Vec3 { x: 2.0, y: 3.0, z: 7.0 }, 4.0).unwrap();
        assert_eq!(p, [2.0, 7.0, 3.15]);
        assert_eq!(limit, 4.15);
        assert_eq!(DOWN, [0.0, 0.0, -1.0]);
        assert!(ray_input(unity::Vec3 { x: f32::NAN, y: 0.0, z: 0.0 }, 1.0).is_none());
        for reach in [-1.0, f32::INFINITY, f32::NAN] {
            assert!(ray_input(unity::Vec3::default(), reach).is_none());
        }
    }

    #[test]
    fn distance_acceptance_is_signed_finite_and_bounded() {
        assert_eq!(accepted_distance(0.0, 2.0), Some(-LIFT));
        assert_eq!(accepted_distance(LIFT, 0.0), Some(0.0));
        assert!((accepted_distance(1.0 + LIFT, 1.0).unwrap() - 1.0).abs() < 1e-12);
        assert!(accepted_distance(2.0 + LIFT + STEP, 2.0).is_none());
        for hit in [-1.0, -0.001, f64::NAN, f64::INFINITY] {
            assert!(accepted_distance(hit, 2.0).is_none());
        }
        assert!(accepted_distance(0.0, -1.0).is_none());
    }

    #[test]
    fn world_and_world_welded_bodies_only() {
        let view = ModelView {
            geom_bodyid: &[0, 1, 2, 3], geom_type: &[0, 1, 1, 2],
            body_weldid: &[0, 0, 2, 2],
        };
        assert_eq!(view.is_static(0), Some(true));
        assert_eq!(view.is_static(1), Some(true));
        assert_eq!(view.is_static(2), Some(false));
        // A fixed child of a moving rider remains dynamic.
        assert_eq!(view.is_static(3), Some(false));
        assert_eq!(view.is_static(4), None);
        assert_eq!(view.is_static(usize::MAX), None);
    }

    #[test]
    fn invalid_body_and_weld_indices_fail_closed() {
        for body in [-1, 2, i32::MAX] {
            let view = ModelView { geom_bodyid: &[body], geom_type: &[1], body_weldid: &[0, 1] };
            assert_eq!(view.is_static(0), None);
        }
        for weld in [-1, 2, i32::MAX] {
            let view = ModelView { geom_bodyid: &[1], geom_type: &[1], body_weldid: &[0, weld] };
            assert_eq!(view.is_static(0), None);
        }
    }

    #[test]
    fn counts_and_reflected_offsets_are_bounded() {
        assert_eq!(count(1), Some(1));
        assert_eq!(count(1_000_000), Some(MAX_ITEMS));
        for n in [-1, 0, 1_000_001, i32::MAX] { assert_eq!(count(n), None); }
        assert_eq!(checked_offset(16, 4, 4), Some(0));
        assert_eq!(checked_offset(24, 8, 16), Some(8));
        assert_eq!(checked_offset(15, 4, 16), None);
        assert_eq!(checked_offset(25, 8, 16), None);
        assert_eq!(checked_offset(usize::MAX, 32, 16), None);
    }

    // Synthetic storage only: no IL2CPP resolution, DLL calls, or live process.
    #[repr(C)]
    struct TestModel {
        ngeom: i32,
        nbody: i32,
        geom_bodyid: *const i32,
        geom_type: *const i32,
        body_weldid: *const i32,
    }

    struct TestData {
        ray_calls: std::cell::Cell<usize>,
        hfield_calls: std::cell::Cell<usize>,
        // (distance from the original lifted origin, geomid)
        hits: Vec<(f64, i32)>,
        terrain: f64,
    }

    unsafe extern "C" fn test_ray(
        _: *const c_void, data: *const c_void, point: *const f64,
        direction: *const f64, groups: *const u8, static_flag: u8,
        exclude: i32, geomid: *mut i32,
    ) -> f64 {
        assert!(groups.is_null());
        assert_eq!(static_flag, 1);
        assert_eq!(exclude, -1);
        assert_eq!(std::slice::from_raw_parts(direction, 3), &DOWN);
        let data = &*data.cast::<TestData>();
        data.ray_calls.set(data.ray_calls.get() + 1);
        let travelled = LIFT - *point.add(2);
        for &(distance, id) in &data.hits {
            if distance >= travelled {
                *geomid = id;
                return distance - travelled;
            }
        }
        -1.0
    }

    unsafe extern "C" fn test_hfield(
        _: *const c_void, data: *const c_void, id: i32,
        point: *const f64, direction: *const f64,
    ) -> f64 {
        let data = &*data.cast::<TestData>();
        data.hfield_calls.set(data.hfield_calls.get() + 1);
        assert_eq!(id, 1); // The dynamic heightfield at index zero must be ignored.
        assert_eq!(*point.add(2), LIFT);
        assert_eq!(std::slice::from_raw_parts(direction, 3), &DOWN);
        data.terrain
    }

    fn test_ground() -> Ground {
        Ground {
            // Test fixture offsets only; production offsets always use metadata.
            offsets: Offsets {
                ngeom: std::mem::offset_of!(TestModel, ngeom),
                nbody: std::mem::offset_of!(TestModel, nbody),
                geom_bodyid: std::mem::offset_of!(TestModel, geom_bodyid),
                geom_type: std::mem::offset_of!(TestModel, geom_type),
                body_weldid: std::mem::offset_of!(TestModel, body_weldid),
            },
            ray: test_ray,
            ray_hfield: test_hfield,
        }
    }

    fn test_data(hits: Vec<(f64, i32)>, terrain: f64) -> TestData {
        TestData {
            ray_calls: std::cell::Cell::new(0), hfield_calls: std::cell::Cell::new(0),
            hits, terrain,
        }
    }

    #[test]
    fn skips_rider_and_uses_nearest_static_geometry_or_invisible_terrain() {
        let bodies = [1, 0, 2];
        let mut types = [HFIELD, HFIELD, 2];
        let welds = [0, 1, 0];
        let mut model = TestModel {
            ngeom: 3, nbody: 3, geom_bodyid: bodies.as_ptr(),
            geom_type: types.as_ptr(), body_weldid: welds.as_ptr(),
        };
        let ground = test_ground();
        let data = test_data(vec![(0.2, 0), (0.8, 2)], 1.0);
        unsafe {
            let result = ground.distance(
                (&model as *const TestModel).cast(), (&data as *const TestData).cast(),
                unity::Vec3::default(), 2.0,
            ).unwrap();
            assert!((result - (0.8 - LIFT)).abs() < 1e-12);
        }
        assert_eq!(data.ray_calls.get(), 2);
        assert_eq!(data.hfield_calls.get(), 1);

        // Same model address, changed scene geometry: no cached heightfield IDs.
        types[1] = 2;
        model.geom_type = types.as_ptr();
        let data = test_data(vec![], 1.0);
        unsafe {
            assert!(ground.distance(
                (&model as *const TestModel).cast(), (&data as *const TestData).cast(),
                unity::Vec3::default(), 2.0,
            ).is_none());
        }
        assert_eq!(data.hfield_calls.get(), 0);
    }

    #[test]
    fn dynamic_budget_is_bounded_and_never_accepts_rider() {
        let bodies = [1, 0];
        let types = [HFIELD, HFIELD];
        let welds = [0, 1];
        let model = TestModel {
            ngeom: 2, nbody: 2, geom_bodyid: bodies.as_ptr(),
            geom_type: types.as_ptr(), body_weldid: welds.as_ptr(),
        };
        for terrain in [-1.0, 1.0] {
            let data = test_data((1..=100).map(|i| (f64::from(i) * 0.01, 0)).collect(), terrain);
            let result = unsafe {
                test_ground().distance(
                    (&model as *const TestModel).cast(), (&data as *const TestData).cast(),
                    unity::Vec3::default(), 2.0,
                )
            };
            assert_eq!(result, if terrain < 0.0 { None } else { Some(terrain - LIFT) });
            assert_eq!(data.ray_calls.get(), MAX_SKIPS + 1);
            assert_eq!(data.hfield_calls.get(), 1);
        }
    }
}