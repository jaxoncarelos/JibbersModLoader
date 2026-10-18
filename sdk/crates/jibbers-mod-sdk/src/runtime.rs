//! Client for the SDK runtime plugin (`JibbersMods\jibbers_sdk.dll`), which the loader runs next
//! to your plugin and which updates itself from the network.
//!
//! The runtime grows new commands over time without you re-downloading anything. Your plugin
//! reaches them through [`call`]; check [`has_feature`] first so your mod still works when the
//! runtime is missing or older than the feature you want. The current list is in
//! `JibbersMods\sdk\docs\RUNTIME.md` (kept up to date by the updater) and in the reply of
//! [`info`].
//!
//! Never call these from `Plugin::init` or `Plugin::unload` (see [`crate::host`]).

use crate::host;
use serde_json::{json, Value};

/// DLL file name (without extension) of the runtime plugin.
pub const RUNTIME_FILE: &str = "jibbers_sdk";

/// Sends `{"cmd": cmd, ...args}` to the runtime and returns its reply. Errors if the runtime isn't
/// loaded, or if it replied `"ok": false` (then with its error message).
pub fn call(cmd: &str, args: Value) -> Result<Value, String> {
    let mut req = match args {
        Value::Object(m) => m,
        Value::Null => Default::default(),
        _ => return Err("runtime::call args must be a JSON object".into()),
    };
    req.insert("cmd".into(), json!(cmd));
    let reply = host::call_plugin_by_file(RUNTIME_FILE, &Value::Object(req)).ok_or("the SDK runtime (JibbersMods\\jibbers_sdk.dll) is not loaded")?;
    if reply.get("ok").and_then(Value::as_bool) == Some(true) {
        Ok(reply)
    } else {
        Err(reply.get("error").and_then(Value::as_str).unwrap_or("runtime call failed").to_string())
    }
}

/// `sdk.info`: runtime version, feature list and update status.
pub fn info() -> Result<Value, String> {
    call("sdk.info", Value::Null)
}

/// True if the loaded runtime lists `feature` (for example `"sdk.plugins"`).
pub fn has_feature(feature: &str) -> bool {
    info().ok().and_then(|i| i["features"].as_array().map(|f| f.iter().any(|x| x == feature))).unwrap_or(false)
}
