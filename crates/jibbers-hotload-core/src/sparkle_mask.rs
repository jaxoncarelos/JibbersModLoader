//! Keeps the additive snow glitter off characters: world-space boxes around every rider, refreshed
//! every frame, that the glitter shader rejects (`_JA_CharBox{i}C` centre / `_JA_CharBox{i}E`
//! extents, `_JA_CharBoxCount`). Rendering-only: no material, layer, lighting or physics changes.
//!
//! The game renders with a scriptable pipeline, so Camera command buffers (a stencil silhouette)
//! are ignored, and Shader.SetGlobalVectorArray is stripped from its build: hence boxes, sent as
//! individual vectors.
use crate::{il2cpp::{self, api}, log, unity::{self, R}};
use std::{collections::{HashMap, HashSet}, ffi::c_void, ptr::null_mut, sync::Mutex};

/// Characters the glitter shader can exclude (the nearest ones to the camera).
const MAX_BOXES: usize = 8;
/// Margin around the renderer bounds (m): loose sleeves, skis and poles.
const BOX_MARGIN: f32 = 0.15;
/// Frames between re-collecting the characters' renderers.
const REFRESH: u32 = 30;
/// Characters further than this from the camera (m) are not re-measured every frame; glitter only
/// reaches 40 m by default, so they can't receive any.
const NEAR: f32 = 120.;
/// Largest half-size (m) of anything treated as a character; bigger groups are scenery that
/// happens to share a root, and boxing them would blank glitter over a whole area.
const MAX_EXTENT: f32 = 3.0;

#[derive(Default)]
struct Mask {
    ticks: u32,
    count: usize,
    error: Option<String>,
    /// Renderers (GC handles) of each character, boxed together, with its last box.
    groups: Vec<Vec<isize>>,
    last: Vec<Option<([f32; 4], [f32; 4])>>,
    ids: Option<Ids>,
}
struct Ids {
    centre: [i32; MAX_BOXES],
    extents: [i32; MAX_BOXES],
    count: i32,
    set_vector: usize,
    set_float: usize,
}
static MASK: Mutex<Option<Mask>> = Mutex::new(None);

unsafe fn ids() -> R<Ids> {
    let shader = unity::class(unity::CORE, "UnityEngine", "Shader")?;
    let id = |name: String| -> R<i32> {
        unity::call(shader, null_mut(), "PropertyToID", &mut [il2cpp::string(&name)]).map(|o| il2cpp::unbox::<i32>(o))
    };
    let mut centre = [0; MAX_BOXES];
    let mut extents = [0; MAX_BOXES];
    for i in 0..MAX_BOXES {
        centre[i] = id(format!("_JA_CharBox{i}C"))?;
        extents[i] = id(format!("_JA_CharBox{i}E"))?;
    }
    Ok(Ids {
        centre,
        extents,
        count: id("_JA_CharBoxCount".into())?,
        set_vector: il2cpp::method_sig(shader, "SetGlobalVector", &["System.Int32", "UnityEngine.Vector4"])
            .ok_or("Shader.SetGlobalVector missing")? as usize,
        set_float: il2cpp::method_sig(shader, "SetGlobalFloat", &["System.Int32", "System.Single"])
            .ok_or("Shader.SetGlobalFloat missing")? as usize,
    })
}

unsafe fn free_groups(mask: &mut Mask) {
    for h in mask.groups.drain(..).flatten() { let _ = unity::gc_free(h); }
    mask.last.clear();
}

/// The scene root a renderer belongs to: one player's body parts share it.
unsafe fn root_id(renderer: *mut crate::il2cpp::Object) -> Option<i32> {
    let tr = unity::callv(renderer, "get_transform", &mut []).ok()?;
    let root = unity::callv(tr, "get_root", &mut []).ok()?;
    unity::call_unbox::<i32>(root, "GetInstanceID").ok()
}

/// Re-collects the characters: one set per rider (its ragdoll's renderers), then every other
/// skinned mesh on its own. Renderer identity deduplication keeps each renderer in one set.
unsafe fn collect(mask: &mut Mask) -> R<()> {
    let skinned = unity::class(unity::CORE, "UnityEngine", "SkinnedMeshRenderer")?;
    let rigid = unity::class(unity::CORE, "UnityEngine", "MeshRenderer")?;
    let mut sets = Vec::new();
    if let Ok(ragdoll) = unity::class("Assembly-CSharp.dll", "", "RagdollController") {
        let kind = unity::class(unity::CORE, "UnityEngine", "Renderer")?;
        for rider in unity::find_objects(ragdoll)? {
            let mut include_inactive = false;
            if let Ok(array) = unity::callv(rider, "GetComponentsInChildren", &mut [unity::type_object(kind), &mut include_inactive as *mut bool as _]) {
                sets.push(unity::array_items(array));
            }
        }
    }
    // Other skinned meshes (remote riders' bodies and clothing) grouped by their scene root.
    let mut by_root: HashMap<i32, Vec<_>> = HashMap::new();
    for r in unity::find_objects(skinned)? {
        if unity::is_null(r) { continue; }
        by_root.entry(root_id(r).unwrap_or(r as usize as i32)).or_default().push(r);
    }
    sets.extend(by_root.into_values());
    let mut seen = HashSet::new();
    let mut count = 0;
    free_groups(mask);
    for set in sets {
        let mut group = Vec::new();
        for renderer in set {
            if unity::is_null(renderer) || !seen.insert(renderer as usize) { continue; }
            let kind = (api().object_get_class)(renderer);
            if !(api().class_is_assignable_from)(skinned, kind) && !(api().class_is_assignable_from)(rigid, kind) { continue; }
            if let Ok(h) = unity::gc_handle(renderer) { group.push(h); }
            count += 1;
        }
        if !group.is_empty() { mask.groups.push(group); }
    }
    mask.last = vec![None; mask.groups.len()];
    if mask.count != count {
        log::info(format!("atmosphere: snow glitter character mask: {count} renderers, {} characters", mask.groups.len()));
    }
    mask.count = count;
    Ok(())
}

/// World bounds of each character from its renderers' current bounds (skinned bounds follow the
/// ragdoll); the nearest `MAX_BOXES` to the camera are uploaded.
unsafe fn push_boxes(mask: &mut Mask, camera: Option<[f32; 3]>) -> R<()> {
    if mask.ids.is_none() {
        mask.ids = Some(ids()?);
    }
    let mut boxes: Vec<([f32; 4], [f32; 4])> = Vec::new();
    let far = |b: &([f32; 4], [f32; 4])| match camera {
        Some(p) => (0..3).map(|i| (b.0[i] - p[i]).powi(2)).sum::<f32>() > NEAR * NEAR,
        None => false,
    };
    for (gi, group) in mask.groups.iter().enumerate() {
        // Far characters keep last frame's box until the next collection.
        if let Some(b) = mask.last[gi].filter(|b| far(b)) {
            boxes.push(b);
            continue;
        }
        let mut lo = [f32::MAX; 3];
        let mut hi = [f32::MIN; 3];
        for &h in group {
            let renderer = unity::gc_target(h).unwrap_or(null_mut());
            if unity::is_null(renderer) || !unity::call_unbox::<bool>(renderer, "get_enabled").unwrap_or(false) { continue; }
            // UnityEngine.Bounds: centre then extents.
            let Ok(b) = unity::call_unbox::<[f32; 6]>(renderer, "get_bounds") else { continue };
            if !b.iter().all(|v| v.is_finite()) { continue; }
            for i in 0..3 {
                lo[i] = lo[i].min(b[i] - b[i + 3]);
                hi[i] = hi[i].max(b[i] + b[i + 3]);
            }
        }
        if lo[0] > hi[0] || (0..3).any(|i| hi[i] - lo[i] > 2. * MAX_EXTENT) {
            mask.last[gi] = None;
            continue;
        }
        let c = [(lo[0] + hi[0]) * 0.5, (lo[1] + hi[1]) * 0.5, (lo[2] + hi[2]) * 0.5, 0.0];
        let e = [(hi[0] - lo[0]) * 0.5 + BOX_MARGIN, (hi[1] - lo[1]) * 0.5 + BOX_MARGIN, (hi[2] - lo[2]) * 0.5 + BOX_MARGIN, 0.0];
        mask.last[gi] = Some((c, e));
        boxes.push((c, e));
    }
    if let Some(p) = camera {
        let d = |c: &[f32; 4]| (c[0] - p[0]).powi(2) + (c[1] - p[1]).powi(2) + (c[2] - p[2]).powi(2);
        boxes.sort_by(|a, b| d(&a.0).total_cmp(&d(&b.0)));
    }
    boxes.truncate(MAX_BOXES);
    let ids = mask.ids.as_ref().ok_or("shader ids missing")?;
    let set_vector = |id: i32, mut v: [f32; 4]| -> R<()> {
        let mut id = id;
        il2cpp::invoke(ids.set_vector as *const _, null_mut(), &mut [&mut id as *mut i32 as *mut c_void, v.as_mut_ptr() as *mut c_void]).map(|_| ())
    };
    for (i, (c, e)) in boxes.iter().enumerate() {
        set_vector(ids.centre[i], *c)?;
        set_vector(ids.extents[i], *e)?;
    }
    let mut id = ids.count;
    let mut n = boxes.len() as f32;
    il2cpp::invoke(ids.set_float as *const _, null_mut(), &mut [&mut id as *mut i32 as *mut c_void, &mut n as *mut f32 as *mut c_void])?;
    Ok(())
}

/// Refreshes the boxes for this frame. Returns whether glitter may be drawn (the boxes are live).
pub unsafe fn update(enabled: bool, camera: Option<[f32; 3]>) -> bool {
    let mut state = MASK.lock().unwrap_or_else(|e| e.into_inner());
    let mask = state.get_or_insert_with(Mask::default);
    if !enabled {
        free_groups(mask);
        let _ = push_boxes(mask, camera);
        mask.ticks = 0;
        return false;
    }
    let result = (|| {
        if mask.ticks == 0 {
            collect(mask)?;
            mask.ticks = REFRESH;
        } else {
            mask.ticks -= 1;
        }
        push_boxes(mask, camera)
    })();
    match result {
        Ok(()) => { mask.error = None; true }
        Err(error) => {
            if mask.error.as_ref() != Some(&error) { log::info(format!("atmosphere: character mask: {error}")); }
            mask.error = Some(error);
            mask.ticks = REFRESH; // retry the collection later, not every frame
            false
        }
    }
}

pub unsafe fn teardown() {
    if let Some(mut mask) = MASK.lock().unwrap_or_else(|e| e.into_inner()).take() {
        free_groups(&mut mask);
        let _ = push_boxes(&mut mask, None);
    }
}
