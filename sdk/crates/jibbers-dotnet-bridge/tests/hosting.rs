//! Starts .NET through the bridge's hostfxr code, loads the built C# SDK and calls its entry point,
//! outside the game. With a null API table `Bootstrap.Init` must answer 2 ("no table"), which proves
//! runtime discovery, `runtimeconfig.json`, assembly loading and the `[UnmanagedCallersOnly]` call.
//! Skipped (passes) when the SDK has not been built (`dotnet build sdk/dotnet/Jibbers.Sdk -c Release`).

use jibbers_dotnet::hostfxr::Runtime;
use std::path::PathBuf;

#[test]
fn hosts_the_catalog_runtime_layout() {
    let Some(root) = std::env::var_os("JIBBERS_TEST_CHANNEL_ROOT") else { return };
    let layout = jibbers_dotnet::layout(&PathBuf::from(root).join("JibbersMods"));
    assert!(layout.sdk.to_string_lossy().contains("cache"));
    let runtime = unsafe { Runtime::start(&layout.runtime_config, &layout.bundled_runtime) }.expect("start channel runtime");
    let entry = unsafe { runtime.unmanaged_entry(&layout.sdk, "Jibbers.Sdk.Bootstrap, Jibbers.Sdk", "Init") }.expect("channel SDK entry");
    let init: unsafe extern "system" fn(*const std::ffi::c_void) -> i32 = unsafe { std::mem::transmute(entry) };
    assert_eq!(unsafe { init(std::ptr::null()) }, 2);
}

#[test]
fn hosts_the_csharp_sdk_and_calls_its_entry_point() {
    let bin = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../dotnet/Jibbers.Sdk/bin/Release/net8.0");
    let sdk = bin.join("Jibbers.Sdk.dll");
    let config = bin.join("Jibbers.Sdk.runtimeconfig.json");
    if !sdk.exists() || !config.exists() {
        eprintln!("C# SDK not built; skipped");
        return;
    }
    let rt = unsafe { Runtime::start(&config, std::path::Path::new("Z:/no-bundled-runtime")) }.expect("start .NET");
    let init = unsafe { rt.unmanaged_entry(&sdk, "Jibbers.Sdk.Bootstrap, Jibbers.Sdk", "Init") }.expect("find Bootstrap.Init");
    let init: unsafe extern "system" fn(*const std::ffi::c_void) -> i32 = unsafe { std::mem::transmute(init) };
    assert_eq!(unsafe { init(std::ptr::null()) }, 2, "Init(null) must refuse a missing API table");
}

/// The runtime bundled in the player package (`JibbersMods\dotnet\runtime`) is found first.
/// Point JIBBERS_TEST_BUNDLED at a staged runtime folder to run it.
#[test]
fn prefers_the_bundled_runtime() {
    let Some(dir) = std::env::var_os("JIBBERS_TEST_BUNDLED") else { return };
    let bundled = PathBuf::from(dir);
    let found = jibbers_dotnet::hostfxr::candidate_roots(&bundled).into_iter().find_map(|r| jibbers_dotnet::hostfxr::find_hostfxr(&r)).expect("a hostfxr");
    assert!(found.starts_with(&bundled), "picked {} instead of the bundled runtime", found.display());
    let bin = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../dotnet/Jibbers.Sdk/bin/Release/net8.0");
    let rt = unsafe { Runtime::start(&bin.join("Jibbers.Sdk.runtimeconfig.json"), &bundled) }.expect("start bundled .NET");
    let init = unsafe { rt.unmanaged_entry(&bin.join("Jibbers.Sdk.dll"), "Jibbers.Sdk.Bootstrap, Jibbers.Sdk", "Init") }.expect("entry");
    let init: unsafe extern "system" fn(*const std::ffi::c_void) -> i32 = unsafe { std::mem::transmute(init) };
    assert_eq!(unsafe { init(std::ptr::null()) }, 2);
}
