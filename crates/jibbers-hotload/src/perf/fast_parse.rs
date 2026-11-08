// From Fast Map Load by xhail39 (GPL-3.0-only), built into the loader; see perf/mod.rs.
//! Why big maps load slowly:
//!
//! The game describes the whole map's physics as one big XML file and MuJoCo reads it. While it
//! reads, MuJoCo does two things over and over that only make sense once:
//!
//! 1. Every time it gives something a name (every collider has one), `mjs_setName` calls
//!    `CheckRepeat`, which sorts *all* the names so far to look for duplicates. With 50,000
//!    colliders that's 50,000 sorts of up to 50,000 names. MuJoCo checks every name for duplicates
//!    again when it compiles the model, so we skip this one.
//!
//! 2. Every time it adds a collider, mesh, body etc., it writes out the *whole* scene so far as text
//!    and hashes it (`Signature`), to keep a fingerprint of the scene up to date. Compiling starts by
//!    working out that fingerprint again from scratch, so the ones made while reading are never used.
//!    We skip those too and leave the fingerprint at 0 until the compile sets it.
//!
//! Both skips overwrite a single `call` instruction. The finished physics model is byte-for-byte
//! the same, just built faster (Village's physics in game: about 4 minutes -> 4.7 s).

use super::pcore;
use std::ffi::c_void;
use windows_sys::Win32::Foundation::HMODULE;
use windows_sys::Win32::System::Diagnostics::Debug::FlushInstructionCache;
use windows_sys::Win32::System::Memory::{VirtualProtect, PAGE_EXECUTE_READWRITE};
use windows_sys::Win32::System::Threading::GetCurrentProcess;

/// The last few instructions of mjs_setName, ending in the call we remove:
///     mov rcx, [r15+0xC0]      ; the model
///     mov edx, [r15]           ; the element type
///     mov dword [rbp-0xC], 0
///     mov [rbp-0x38], r15
///     vzeroupper
///     call CheckRepeat
/// If the game ever ships a different pcore.dll this won't match and we change nothing.
#[rustfmt::skip]
const BEFORE_CALL: [u8; 24] = [
    0x49, 0x8B, 0x8F, 0xC0, 0x00, 0x00, 0x00,
    0x41, 0x8B, 0x17,
    0xC7, 0x45, 0xF4, 0x00, 0x00, 0x00, 0x00,
    0x4C, 0x89, 0x7D, 0xC8,
    0xC5, 0xF8, 0x77,
];
const CALL: u8 = 0xE8;
const NOP_5_BYTES: [u8; 5] = [0x0F, 0x1F, 0x44, 0x00, 0x00];

/// Where the fingerprint gets updated after adding something to a body (geom, body, site, joint...):
///     mov rcx, [rsi+0xC8]      ; the model
///     call Signature
///     mov rcx, [rsi+0xC8]
///     mov rcx, [rcx+0x818]     ; the model's spec
///     mov [rcx+8], rax         ; spec.signature = the result
/// `None` is the call's target address, which we don't need to know in advance.
#[rustfmt::skip]
const SIGNATURE_AFTER_BODY_ADD: [Option<u8>; 30] = bytes_with_gap(&[
    0x48, 0x8B, 0x8E, 0xC8, 0x00, 0x00, 0x00,
    CALL, 0, 0, 0, 0,
    0x48, 0x8B, 0x8E, 0xC8, 0x00, 0x00, 0x00,
    0x48, 0x8B, 0x89, 0x18, 0x08, 0x00, 0x00,
    0x48, 0x89, 0x41, 0x08,
], 7);

/// The same after adding something to the model itself (mesh, material, texture...):
///     mov rcx, rsi             ; the model
///     call Signature
///     mov rcx, [rsi+0x818]     ; the model's spec
///     mov [rcx+8], rax         ; spec.signature = the result
#[rustfmt::skip]
const SIGNATURE_AFTER_MODEL_ADD: [Option<u8>; 19] = bytes_with_gap(&[
    0x48, 0x89, 0xF1,
    CALL, 0, 0, 0, 0,
    0x48, 0x8B, 0x8E, 0x18, 0x08, 0x00, 0x00,
    0x48, 0x89, 0x41, 0x08,
], 3);

/// How many of those calls pcore.dll 3.3.6 has (9 body adds, 18 model adds).
const SIGNATURE_CALLS: usize = 27;

/// `xor eax, eax` (the fingerprint stays 0) and a 3-byte NOP, in place of `call Signature`.
const ZERO_5_BYTES: [u8; 5] = [0x31, 0xC0, 0x0F, 0x1F, 0x00];

pub fn enable(pcore: HMODULE) -> Result<(), String> {
    let version = pcore::version(pcore)?;
    if version != "3.3.6" {
        return Err(format!("made for MuJoCo 3.3.6, the game has {version}"));
    }
    // Find both before changing anything, so an unknown pcore.dll is left exactly as it was.
    let check_repeat = find_check_repeat(pcore)?;
    let signatures = find_signature_calls(pcore)?;

    overwrite_code(check_repeat, &NOP_5_BYTES)?;
    for call in signatures {
        overwrite_code(call, &ZERO_5_BYTES)?;
    }
    Ok(())
}

/// The `call CheckRepeat` inside mjs_setName.
fn find_check_repeat(pcore: HMODULE) -> Result<*mut u8, String> {
    let set_name = pcore::function(pcore, "mjs_setName")?;
    let code = unsafe { std::slice::from_raw_parts(set_name, 1024) };
    let mut pattern = BEFORE_CALL.to_vec();
    pattern.push(CALL);
    let found: Vec<usize> =
        code.windows(pattern.len()).enumerate().filter(|(_, w)| *w == pattern).map(|(i, _)| i).collect();
    let [start] = found[..] else {
        return Err("unknown version of pcore.dll".into());
    };

    Ok(unsafe { set_name.add(start + BEFORE_CALL.len()) } as *mut u8)
}

/// Every `call Signature` that just updates the fingerprint after something was added.
fn find_signature_calls(pcore: HMODULE) -> Result<Vec<*mut u8>, String> {
    let code = pcore::code(pcore)?;
    let mut calls = Vec::new();
    for (pattern, call_at) in [(&SIGNATURE_AFTER_BODY_ADD[..], 7), (&SIGNATURE_AFTER_MODEL_ADD[..], 3)] {
        for (i, window) in code.windows(pattern.len()).enumerate() {
            if window.iter().zip(pattern).all(|(byte, want)| want.is_none_or(|w| w == *byte)) {
                calls.push(unsafe { code.as_ptr().add(i + call_at) } as *mut u8);
            }
        }
    }
    // They must all be calls to the one same function (Signature).
    let targets: Vec<usize> = calls.iter().map(|&call| unsafe { call_target(call) }).collect();
    if calls.len() != SIGNATURE_CALLS || targets.iter().any(|&t| t != targets[0]) {
        return Err("unknown version of pcore.dll".into());
    }
    Ok(calls)
}

/// Where a 5-byte `call rel32` instruction jumps to.
unsafe fn call_target(call: *const u8) -> usize {
    let offset = i32::from_le_bytes(*(call.add(1) as *const [u8; 4]));
    (call as usize + 5).wrapping_add_signed(offset as isize)
}

/// Turns a byte pattern into one where the 4 bytes after `call_at` (a call's target) match anything.
const fn bytes_with_gap<const N: usize>(bytes: &[u8; N], call_at: usize) -> [Option<u8>; N] {
    let mut out = [None; N];
    let mut i = 0;
    while i < N {
        if i <= call_at || i > call_at + 4 {
            out[i] = Some(bytes[i]);
        }
        i += 1;
    }
    out
}

fn overwrite_code(at: *mut u8, bytes: &[u8]) -> Result<(), String> {
    unsafe {
        let mut old = 0;
        if VirtualProtect(at as *const c_void, bytes.len(), PAGE_EXECUTE_READWRITE, &mut old) == 0 {
            return Err("couldn't make the code writable".into());
        }
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), at, bytes.len());
        VirtualProtect(at as *const c_void, bytes.len(), old, &mut old);
        FlushInstructionCache(GetCurrentProcess(), at as *const c_void, bytes.len());
    }
    Ok(())
}
