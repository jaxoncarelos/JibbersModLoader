//! Faster physics rebuilds for big scenes (respawning after placing an object rebuilds the whole
//! scene). Both parts leave the compiled model byte-identical:
//!
//! 1. Tree lists: MuJoCo's `mjCBody::Add*` (geoms, sites, joints...) rebuilds the model-wide tree
//!    lists after every single element (`ResetTreeLists` + `MakeTreeLists`, a walk over everything
//!    added so far), so parsing N geoms costs O(N^2). We remove those per-element calls and rebuild
//!    the lists once when parsing ends: the lists end up exactly as the last per-element rebuild left
//!    them. (Real 97k-collider scene: parse 18.1 -> 5.8 s.)
//! 2. Mesh cache: MuJoCo caches processed meshes (convex hull, inertia, BVH) only for meshes loaded
//!    from a file. The game writes every collider mesh inline (`vertex="..."`), so every rebuild
//!    reprocessed all of them. We move each inline mesh into an in-memory file named by a hash of its
//!    vertices (MuJoCo's own MSH format, float32 vertices as MuJoCo would parse them) so its cache
//!    applies: a rebuild only processes meshes it hasn't seen. (Compile 12.5 -> 3.0 s.)

use std::collections::HashSet;
use std::ffi::{c_char, c_int, c_void};
use std::sync::{Mutex, OnceLock};
use windows_sys::Win32::Foundation::HMODULE;

// ---- tree lists ---------------------------------------------------------------------------------

type ResetTreeLists = unsafe extern "C" fn(*mut c_void);
type MakeTreeLists = unsafe extern "C" fn(*mut c_void, *mut c_void);
static TREE: OnceLock<(ResetTreeLists, MakeTreeLists)> = OnceLock::new();
/// The call sites (address, original 5 bytes): NOPed only while an XML parse runs.
static SITES: OnceLock<Vec<(usize, [u8; 5])>> = OnceLock::new();
/// mjSpec's first field is its element, the mjCModel itself; the model keeps its spec at +0x818
/// (from mjCBody::AddGeom: `mov rcx,[model+0x818]; mov [rcx+8],rax` sets spec.element->signature).
const MODEL_SPEC: usize = 0x818;

const NOP5: [u8; 5] = [0x0F, 0x1F, 0x44, 0x00, 0x00];

/// Finds `mov rcx,[reg+0xC8]; call R; mov rcx,[reg+0xC8]; xor edx,edx; call M` (the end of every
/// mjCBody::Add*) and checks every site calls the same two functions. Nothing is changed yet: see
/// `parse_with_fast_tree_lists`.
pub fn find_tree_lists(pcore: HMODULE) -> Result<usize, String> {
    let code = super::pcore::code(pcore)?;
    let mut sites = Vec::new();
    let mut i = 0;
    while i + 26 <= code.len() {
        let w = &code[i..i + 26];
        let mov = |o: usize| w[o] == 0x48 && w[o + 1] == 0x8B && (0x88..=0x8F).contains(&w[o + 2]) && w[o + 3..o + 7] == [0xC8, 0, 0, 0];
        if mov(0) && w[7] == 0xE8 && mov(12) && (w[19] == 0x31 || w[19] == 0x33) && w[20] == 0xD2 && w[21] == 0xE8 {
            let base = code.as_ptr() as usize + i;
            let rel = |o: usize| i32::from_le_bytes(w[o..o + 4].try_into().unwrap()) as isize;
            let r = (base + 12).wrapping_add_signed(rel(8));
            let m = (base + 26).wrapping_add_signed(rel(22));
            sites.push((base + 7, base + 21, r, m));
            i += 26;
        } else {
            i += 1;
        }
    }
    let Some(&(_, _, r, m)) = sites.first() else { return Err("unknown version of pcore.dll (no tree-list calls)".into()) };
    if sites.iter().any(|s| s.2 != r || s.3 != m) || sites.len() > 32 {
        return Err("unknown version of pcore.dll (tree-list calls differ)".into());
    }
    let mut saved = Vec::new();
    for &(a, b, _, _) in &sites {
        for at in [a, b] {
            let mut orig = [0u8; 5];
            unsafe { std::ptr::copy_nonoverlapping(at as *const u8, orig.as_mut_ptr(), 5) };
            saved.push((at, orig));
        }
    }
    unsafe {
        let _ = TREE.set((std::mem::transmute(r), std::mem::transmute(m)));
    }
    let _ = SITES.set(saved);
    Ok(sites.len())
}

/// Runs one XML parse with the per-element tree-list rebuilds removed, puts them back, and
/// rebuilds the lists once for the parsed spec. Parses run on one thread (the game's), so nothing
/// else adds elements while the calls are out.
pub unsafe fn parse_with_fast_tree_lists(parse: impl FnOnce() -> *mut c_void) -> *mut c_void {
    let Some(sites) = SITES.get() else { return parse() };
    static LOCK: Mutex<()> = Mutex::new(());
    let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
    for &(at, _) in sites {
        let _ = super::fast_parse::overwrite(at as *mut u8, &NOP5);
    }
    let spec = std::panic::catch_unwind(std::panic::AssertUnwindSafe(parse));
    for &(at, orig) in sites {
        let _ = super::fast_parse::overwrite(at as *mut u8, &orig);
    }
    let spec = match spec {
        Ok(s) => s,
        Err(e) => std::panic::resume_unwind(e),
    };
    finish_parse(spec);
    spec
}

/// Rebuilds the tree lists once (what the last per-element rebuild would have left).
unsafe fn finish_parse(spec: *mut c_void) {
    let Some(&(reset, make)) = TREE.get() else { return };
    if spec.is_null() {
        return;
    }
    let model = *(spec as *const *mut c_void);
    if model.is_null() || *((model as *const u8).add(MODEL_SPEC) as *const *mut c_void) != model {
        return; // layout check failed: leave it (only an unpatched build gets here)
    }
    reset(model);
    make(model, std::ptr::null_mut());
}

// ---- mesh cache -----------------------------------------------------------------------------------

type DefaultVfs = unsafe extern "C" fn(*mut c_void);
type AddBufferVfs = unsafe extern "C" fn(*mut c_void, *const c_char, *const c_void, c_int) -> c_int;
type GetCache = unsafe extern "C" fn() -> *mut c_void;
type SetCacheCapacity = unsafe extern "C" fn(*mut c_void, usize) -> usize;

struct Vfs {
    vfs: Box<[u64; 8]>, // mjVFS is one pointer (impl_) in 3.3.6; room to spare
    files: HashSet<[u8; 20]>,
    add: AddBufferVfs,
}
unsafe impl Send for Vfs {}
static VFS: Mutex<Option<Vfs>> = Mutex::new(None);

/// Sets up the persistent in-memory file system and MuJoCo's mesh cache size.
pub fn enable_mesh_cache(pcore: HMODULE, cache_bytes: usize) -> Result<(), String> {
    unsafe {
        let default_vfs: DefaultVfs = std::mem::transmute(super::pcore::function(pcore, "mj_defaultVFS")?);
        let add: AddBufferVfs = std::mem::transmute(super::pcore::function(pcore, "mj_addBufferVFS")?);
        let get_cache: GetCache = std::mem::transmute(super::pcore::function(pcore, "mj_getCache")?);
        let set_cap: SetCacheCapacity = std::mem::transmute(super::pcore::function(pcore, "mj_setCacheCapacity")?);
        let mut vfs = Box::new([0u64; 8]);
        default_vfs(vfs.as_mut_ptr() as *mut c_void);
        let cache = get_cache();
        if cache.is_null() {
            return Err("MuJoCo has no asset cache".into());
        }
        set_cap(cache, cache_bytes);
        *VFS.lock().unwrap_or_else(|e| e.into_inner()) = Some(Vfs { vfs, files: HashSet::new(), add });
    }
    Ok(())
}

/// The VFS meshes live in (pass it to parse and compile of a rewritten scene).
pub fn vfs_ptr() -> Option<*mut c_void> {
    VFS.lock().ok()?.as_mut().map(|v| v.vfs.as_mut_ptr() as *mut c_void)
}

/// Rewrites every inline `<mesh ... vertex="..." .../>` (no faces) into `file="<sha1>.msh"`, adding
/// the files to the VFS. None when nothing was rewritten or the cache is off.
pub fn rewrite_meshes(xml: &[u8]) -> Option<(Vec<u8>, usize)> {
    let mut guard = VFS.lock().ok()?;
    let v = guard.as_mut()?;
    let mut out = Vec::with_capacity(xml.len() / 2);
    let (mut last, mut n, mut pos) = (0usize, 0usize, 0usize);
    while let Some(off) = find(&xml[pos..], b"<mesh ") {
        let start = pos + off;
        let Some(end_off) = find(&xml[start..], b">") else { break };
        let end = start + end_off;
        let el = &xml[start..end];
        pos = end;
        if find(el, b" face=").is_some() || find(el, b" file=").is_some() {
            continue; // faces or a real file: leave as is
        }
        let Some(va) = find(el, b" vertex=\"") else { continue };
        let vs = start + va + 9;
        let Some(ve_off) = find(&xml[vs..end], b"\"") else { continue };
        let ve = vs + ve_off;
        let Ok(text) = std::str::from_utf8(&xml[vs..ve]) else { continue };
        let mut verts: Vec<f32> = Vec::new();
        let mut ok = true;
        for t in text.split_ascii_whitespace() {
            match t.parse::<f64>() {
                Ok(x) => verts.push(x as f32),
                Err(_) => {
                    ok = false;
                    break;
                }
            }
        }
        if !ok || verts.is_empty() || verts.len() % 3 != 0 {
            continue;
        }
        // MSH: int32 nvertex, nnormal, ntexcoord, nface, then float32 vertices
        let mut body = Vec::with_capacity(16 + verts.len() * 4);
        for x in [(verts.len() / 3) as i32, 0, 0, 0] {
            body.extend_from_slice(&x.to_le_bytes());
        }
        for x in &verts {
            body.extend_from_slice(&x.to_le_bytes());
        }
        let h = sha1(&body);
        let name: String = h.iter().map(|b| format!("{b:02x}")).collect::<String>() + ".msh";
        if v.files.insert(h) {
            let cname = std::ffi::CString::new(name.clone()).unwrap();
            unsafe { (v.add)(v.vfs.as_mut_ptr() as *mut c_void, cname.as_ptr(), body.as_ptr() as *const c_void, body.len() as c_int) };
        }
        out.extend_from_slice(&xml[last..start + va]);
        out.extend_from_slice(b" file=\"");
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(b"\"");
        last = ve + 1;
        n += 1;
    }
    if n == 0 {
        return None;
    }
    out.extend_from_slice(&xml[last..]);
    Some((out, n))
}

fn find(hay: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || hay.len() < needle.len() {
        return None;
    }
    let first = needle[0];
    let mut i = 0;
    while i + needle.len() <= hay.len() {
        match hay[i..hay.len() - needle.len() + 1].iter().position(|&b| b == first) {
            None => return None,
            Some(p) => {
                i += p;
                if &hay[i..i + needle.len()] == needle {
                    return Some(i);
                }
                i += 1;
            }
        }
    }
    None
}

/// SHA-1 (file names only: content identity, not security).
fn sha1(data: &[u8]) -> [u8; 20] {
    let mut h: [u32; 5] = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0];
    let mut msg = data.to_vec();
    let bits = (data.len() as u64) * 8;
    msg.push(0x80);
    while msg.len() % 64 != 56 {
        msg.push(0);
    }
    msg.extend_from_slice(&bits.to_be_bytes());
    for chunk in msg.chunks(64) {
        let mut w = [0u32; 80];
        for i in 0..16 {
            w[i] = u32::from_be_bytes(chunk[4 * i..4 * i + 4].try_into().unwrap());
        }
        for i in 16..80 {
            w[i] = (w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16]).rotate_left(1);
        }
        let (mut a, mut b, mut c, mut d, mut e) = (h[0], h[1], h[2], h[3], h[4]);
        for (i, wi) in w.iter().enumerate() {
            let (f, k) = match i {
                0..=19 => ((b & c) | (!b & d), 0x5A827999),
                20..=39 => (b ^ c ^ d, 0x6ED9EBA1),
                40..=59 => ((b & c) | (b & d) | (c & d), 0x8F1BBCDC),
                _ => (b ^ c ^ d, 0xCA62C1D6),
            };
            let t = a.rotate_left(5).wrapping_add(f).wrapping_add(e).wrapping_add(k).wrapping_add(*wi);
            e = d;
            d = c;
            c = b.rotate_left(30);
            b = a;
            a = t;
        }
        for (x, y) in h.iter_mut().zip([a, b, c, d, e]) {
            *x = x.wrapping_add(y);
        }
    }
    let mut out = [0u8; 20];
    for (i, x) in h.iter().enumerate() {
        out[4 * i..4 * i + 4].copy_from_slice(&x.to_be_bytes());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha1_known_answers() {
        let hex = |d: &[u8]| sha1(d).iter().map(|b| format!("{b:02x}")).collect::<String>();
        assert_eq!(hex(b""), "da39a3ee5e6b4b0d3255bfef95601890afd80709");
        assert_eq!(hex(b"abc"), "a9993e364706816aba3e25717850c26c9cd0d89d");
    }

    #[test]
    fn find_works() {
        assert_eq!(find(b"abc<mesh x", b"<mesh "), Some(3));
        assert_eq!(find(b"abc", b"<mesh "), None);
    }
}
