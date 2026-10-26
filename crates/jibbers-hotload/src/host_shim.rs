//! The part of the SDK's `host` module that the SDK's `symbols` needs, for crates that compile the
//! SDK sources by path (the release loader and the macOS UI). Both define `crate::host()`.

use crate::abi::PreHookFn;
use std::ffi::c_void;

/// Pre-hooks a resolved method; `label` names it in errors and the log.
pub unsafe fn pre_hook(m: *const crate::il2cpp::Method, label: &str, cb: PreHookFn) -> Result<(), String> {
    match (crate::host().pre_hook)(m as *const c_void, cb) {
        0 => Ok(()),
        rc => {
            crate::log::info(format!("hook {label} failed ({rc})"));
            Err(format!("hook {label} failed ({rc})"))
        }
    }
}
