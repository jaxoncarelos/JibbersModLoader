//! Atmosphere: height fog, horizon haze, sun glow and falling snow, so maps feel less empty.
//!
//! The game's build strips Unity's fog shader variants (RenderSettings.fog does nothing), so the
//! effects come from our own shaders in `jibbers_atmosphere.bundle` (built from
//! mods/JibbersAtmosphere/unity). The bundle holds one prefab whose meshes the shaders place around
//! whichever camera renders them; we spawn it once (DontDestroyOnLoad) and drive everything through
//! shader globals from a pre-hook on `EnvironmentManager.Update`, taking colours from the game's
//! own day/night sky so the fog always matches the horizon.
//!
//! Settings live in `jibbers_atmosphere.json` next to the game exe (written with defaults on first
//! run) and can be changed live with `{"cmd":"atmo", ...fields, "save": true}`.
//!
//! Hotkeys (`hotkeys`, on by default) are polled once per frame on the main thread: `toggleKey`
//! (F9) turns the atmosphere on or off, `presetKey` (F10) goes to the next preset.

use crate::il2cpp::{self, api, Object};
use crate::unity::{self, Vec3, R};
use crate::{game, grading, log, pump};
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use std::ffi::c_void;
use std::path::PathBuf;
use std::ptr::null_mut;
use std::sync::Mutex;
use windows_sys::Win32::System::Threading::GetCurrentProcessId;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
use windows_sys::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};

pub const BUNDLE_FILE: &str = "jibbers_atmosphere.bundle";
const SETTINGS_FILE: &str = "jibbers_atmosphere.json";
const PREFAB: &str = "JibbersAtmosphere";

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Settings {
    pub enabled: bool,
    /// Height fog density (1/m) at the base height; thins out exponentially above it.
    pub fog_density: f32,
    /// How fast the height fog thins with altitude (1/m); 1/falloff is its scale height.
    pub fog_falloff: f32,
    /// Base height of the fog relative to the lowest point of the map's terrain (m).
    pub fog_base_offset: f32,
    /// Extra fog that only depends on distance (1/m): aerial perspective at any altitude.
    pub haze_density: f32,
    /// No fog closer than this (m), so the rider and nearby features stay crisp.
    pub fog_start: f32,
    /// Opacity cap, so far terrain never disappears completely.
    pub fog_max: f32,
    /// How much the haze covers the sky near the horizon (0..1).
    pub sky_haze: f32,
    /// Ray length used for sky pixels (m); larger = thicker haze over the sky.
    pub sky_distance: f32,
    /// How quickly the sky haze fades above the horizon (higher = thinner band).
    pub sky_band: f32,
    /// Multiplies the game's sky-synced fog colour.
    pub fog_tint: [f32; 3],
    /// Colour multiplier when looking up through the fog.
    pub top_tint: [f32; 3],
    /// Brightness of the glow around the sun in the fog.
    pub sun_glow: f32,
    /// Exponent of the sun glow lobe (higher = tighter).
    pub sun_glow_sharpness: f32,
    /// Fraction of snowflakes shown (0 = no snowfall, 1 = blizzard).
    pub snow: f32,
    /// Size of the box of flakes around the camera (m).
    pub snow_box: f32,
    /// Fall speed (m/s).
    pub snow_fall: f32,
    /// Flake radius (m).
    pub snow_size: f32,
    pub snow_opacity: f32,
    /// Wind (m/s, world x/y/z), moves flakes sideways.
    pub wind: [f32; 3],
    /// Side-to-side flutter of the flakes (m).
    pub flutter: f32,
    /// How much drifting cloud shadows darken the ground (0 = off).
    pub cloud_shadows: f32,
    /// Typical cloud size (m).
    pub cloud_size: f32,
    /// Fraction of the sky covered by clouds (0..1).
    pub cloud_coverage: f32,
    /// Cloud shadow height above the player's camera (m); sets how shadows shift with the sun angle.
    pub cloud_height: f32,
    /// Cloud drift (m/s, world x/z).
    pub cloud_speed: [f32; 2],
    /// Width of the soft cloud shadow edge (noise units, ~0.05 sharp .. 0.2 very soft).
    pub cloud_softness: f32,
    /// Colour of snow fully in a cloud's shadow (multiplies the scene).
    pub cloud_tint: [f32; 3],
    /// Strength of contact shadows where things meet (0 = off).
    pub ao: f32,
    /// Reach of the contact shadows (m).
    pub ao_radius: f32,
    /// Contact shadows fade out by this distance (m).
    pub ao_distance: f32,
    /// Only occluders rising more steeply than this above the surface count (degrees).
    pub ao_min_angle: f32,
    /// Colour of a fully occluded corner (multiplies the scene).
    pub ao_tint: [f32; 3],
    /// Brightness of ice-crystal glints on the snow (0 = off).
    pub sparkle: f32,
    /// Fraction of tiny snow cells that hold a crystal (0..1).
    pub sparkle_density: f32,
    /// Glints fade out by this distance (m).
    pub sparkle_distance: f32,
    /// Crystal cell size (m).
    pub sparkle_size: f32,
    /// Brightness of the light shafts around the sun (0 = off).
    pub sun_shafts: f32,
    /// How far towards the sun (screen fraction) each pixel looks for open sky.
    pub sun_shaft_reach: f32,
    /// Width of the area around the sun with shafts (higher = tighter).
    pub sun_shaft_sharpness: f32,
    /// How much falling snow streaks with your speed (0 = round flakes always).
    pub speed_streaks: f32,
    /// Strength of the faint cold frost at the screen edges at high speed (0 = off).
    pub speed_frost: f32,
    /// Speed where the frost starts fading in, and where it is full (km/h).
    pub speed_frost_kmh: [f32; 2],
    /// Low cloud sea in the valleys below the map (0 = off, 1 = thick).
    pub cloud_bank: f32,
    /// Legacy terrain-relative top, used while the player's camera is unavailable.
    pub cloud_bank_top: f32,
    /// Distance below the player's camera to the top of the cloud bank (m).
    pub cloud_bank_distance: f32,
    /// Distance past the map edge over which the cloud bank fades in (m).
    pub cloud_bank_margin: f32,
    /// Thickness of the cloud bank (m).
    pub cloud_bank_thickness: f32,
    /// Typical size of the cloud lumps (m).
    pub cloud_bank_size: f32,
    /// How much of the valley floor is covered (0..1).
    pub cloud_bank_coverage: f32,
    /// Drift of the cloud bank (m/s, world x/z).
    pub cloud_bank_speed: [f32; 2],
    /// Replace the map terrain's snow textures with the mod's wind-packed snow (CC0, ambientCG):
    /// bright albedo, a fine granular crust and large soft wind dunes instead of the game's
    /// repeating corduroy.
    pub snow_surface: bool,
    /// World size of one repeat of the fine snow grain (m).
    pub snow_surface_scale: f32,
    /// Strength of the fine grain.
    pub snow_surface_normal: f32,
    /// World size of one repeat of the wind dunes (m).
    pub snow_surface_dune_scale: f32,
    /// Strength of the wind dunes.
    pub snow_surface_dune_normal: f32,
    /// Anisotropic texture filtering forced on every texture (1..16; 0 = leave the game's setting).
    /// Keeps textures sharp at shallow angles now that map textures have mipmaps.
    pub anisotropic: i32,
    /// Level-of-detail bias (higher = detailed models from further away; 0 = leave the game's).
    pub lod_bias: f32,
    /// Debug: extra camera velocity (m/s, world) added for previewing the speed effects standing still.
    pub debug_cam_vel: [f32; 3],
    /// Debug: draw the shading (cloud + contact shadows) on the right half of the screen only.
    pub debug_split: bool,
    /// Colour grading: apply `beautify` and `environment` below.
    pub grading: bool,
    /// Beautify post-processing parameters by name (e.g. "saturate": 1.1, "tintColor": [r,g,b,a]).
    pub beautify: Map<String, Value>,
    /// EnvironmentManager lighting fields by name (e.g. "ambientSaturation": 0.5).
    pub environment: Map<String, Value>,
    /// Name of the preset the settings were last set from (a key of `presets`).
    pub preset: String,
    /// Named presets: partial settings (camelCase fields) applied over the defaults when chosen.
    pub presets: Map<String, Value>,
    /// Keyboard hotkeys while the game window has focus (see the module docs).
    pub hotkeys: bool,
    /// Virtual-key code of the on/off hotkey (0x78 = F9).
    pub toggle_key: u32,
    /// Virtual-key code of the next-preset hotkey (0x79 = F10).
    pub preset_key: u32,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            enabled: true,
            fog_density: 0.004,
            fog_falloff: 0.012,
            fog_base_offset: 0.0,
            haze_density: 0.004,
            fog_start: 5.0,
            fog_max: 1.0,
            sky_haze: 1.0,
            sky_distance: 3000.0,
            sky_band: 0.0,
            fog_tint: [1.0, 0.95, 0.86],
            top_tint: [1.0, 0.96, 0.9],
            sun_glow: 0.25,
            sun_glow_sharpness: 10.0,
            snow: 0.7,
            snow_box: 40.0,
            snow_fall: 1.1,
            snow_size: 0.035,
            snow_opacity: 0.85,
            wind: [1.2, 0.0, 0.5],
            flutter: 0.35,
            cloud_shadows: 0.0,
            cloud_size: 500.0,
            cloud_coverage: 0.4,
            cloud_height: 1500.0,
            cloud_speed: [6.0, 2.5],
            cloud_softness: 0.12,
            cloud_tint: [0.62, 0.7, 0.86],
            ao: 0.0,
            ao_radius: 0.6,
            ao_distance: 80.0,
            ao_min_angle: 22.0,
            ao_tint: [0.5, 0.58, 0.72],
            sparkle: 1.0,
            sparkle_density: 0.04,
            sparkle_distance: 40.0,
            sparkle_size: 0.015,
            speed_streaks: 1.0,
            speed_frost: 0.0,
            speed_frost_kmh: [60.0, 120.0],
            cloud_bank: 1.0,
            cloud_bank_top: 0.55,
            cloud_bank_distance: 180.0,
            cloud_bank_margin: 120.0,
            cloud_bank_thickness: 250.0,
            cloud_bank_size: 450.0,
            cloud_bank_coverage: 0.6,
            cloud_bank_speed: [1.5, 0.6],
            sun_shafts: 0.0,
            sun_shaft_reach: 0.8,
            sun_shaft_sharpness: 6.0,
            snow_surface: false,
            snow_surface_scale: 2.5,
            snow_surface_normal: 2.5,
            snow_surface_dune_scale: 60.0,
            snow_surface_dune_normal: 0.6,
            anisotropic: 16,
            lod_bias: 2.0,
            debug_cam_vel: [0.0; 3],
            debug_split: false,
            grading: true,
            // Filmic tonemapping, cooler white balance, a touch of vibrance/contrast and a light
            // bloom; bluer sky light in the shadows.
            beautify: json!({
                "tonemap": 1, "tonemapExposurePre": 1.1, "contrast": 1.1, "saturate": 0.15,
                "colorTemp": 8000.0, "colorTempBlend": 0.6, "bloomIntensity": 0.12, "bloomThreshold": 0.9,
            }).as_object().cloned().unwrap_or_default(),
            environment: json!({"ambientSaturation": 0.55}).as_object().cloned().unwrap_or_default(),
            hotkeys: true,
            toggle_key: 0x78,
            preset_key: 0x79,
            preset: "Foggy day".to_string(),
            presets: json!({
                "Foggy day": {},
                "Clear": {
                    "hazeDensity": 0.00045, "fogDensity": 0.0022, "fogMax": 0.9, "skyHaze": 0.8, "skyBand": 9.0,
                    "fogTint": [1.0, 1.0, 1.0], "topTint": [0.9, 0.95, 1.05], "snow": 0.3, "sunShafts": 0.8,
                    "sunGlow": 0.6, "fogStart": 25.0,
                },
                "Snowstorm": {
                    "hazeDensity": 0.012, "fogDensity": 0.008, "fogStart": 2.0, "snow": 1.0,
                    "wind": [6.0, 0.0, 2.0], "flutter": 0.8,
                },
            }).as_object().cloned().unwrap_or_default(),
        }
    }
}

/// Built-in presets, in the order F10 cycles through them. Other presets follow, alphabetically.
const BUILT_IN_PRESETS: [&str; 3] = ["Foggy day", "Clear", "Snowstorm"];

/// Preset names in cycle order.
pub fn preset_order(presets: &Map<String, Value>) -> Vec<String> {
    let mut names: Vec<String> = BUILT_IN_PRESETS.iter().filter(|n| presets.contains_key(**n)).map(|n| n.to_string()).collect();
    let mut others: Vec<String> = presets.keys().filter(|k| !BUILT_IN_PRESETS.contains(&k.as_str())).cloned().collect();
    others.sort();
    names.extend(others);
    names
}

/// The preset after the current one in the cycle (the first one if the current name is unknown).
fn next_preset_name(cur: &Value) -> R<String> {
    let presets = cur.get("presets").and_then(Value::as_object).ok_or("no presets defined")?;
    let order = preset_order(presets);
    if order.is_empty() {
        return Err("no presets defined".into());
    }
    let now = cur.get("preset").and_then(Value::as_str).unwrap_or("");
    let i = order.iter().position(|n| n.as_str() == now).map_or(0, |i| (i + 1) % order.len());
    Ok(order[i].clone())
}

/// Settings with the named preset applied: starts from the defaults, keeps the user's own presets,
/// the name of the preset and the master switch, then layers the preset's fields on top.
fn with_preset(cur: &Value, name: &str) -> R<Value> {
    let partial = cur
        .get("presets")
        .and_then(Value::as_object)
        .and_then(|p| p.get(name))
        .and_then(Value::as_object)
        .ok_or_else(|| format!("no preset named {name:?}"))?
        .clone();
    let mut next = serde_json::to_value(Settings::default()).map_err(|e| e.to_string())?;
    for keep in ["enabled", "presets"] {
        next[keep] = cur[keep].clone();
    }
    next["preset"] = json!(name);
    if let Some(obj) = next.as_object_mut() {
        merge_fields(obj, &partial);
    }
    Ok(next)
}

/// Writes the fields of `req` into the settings object `cur`. Maps merge key by key (null removes a
/// key); keys that are not settings are ignored.
fn merge_fields(cur: &mut Map<String, Value>, req: &Map<String, Value>) {
    for (k, v) in req {
        match (cur.get_mut(k), v) {
            // Maps (beautify, environment) merge key by key; null removes a key.
            (Some(Value::Object(c)), Value::Object(n)) => {
                for (nk, nv) in n {
                    if nv.is_null() {
                        c.remove(nk);
                    } else {
                        c.insert(nk.clone(), nv.clone());
                    }
                }
            }
            (Some(c), _) => *c = v.clone(),
            (None, _) => {}
        }
    }
}

struct State {
    settings: Settings,
    spawned: bool,
    /// Destroy the instance on the next frame so it is spawned again from the bundle file.
    respawn: bool,
    /// Frames until the next check that the prefab instance still exists.
    check_in: u32,
    /// Lowest terrain height of the current map, and the map it belongs to.
    floor: Option<(Option<String>, f32)>,
    floor_retry: u32,
    load_error: Option<String>,
    ids: Option<Ids>,
    hooked: bool,
    originals: grading::Originals,
    /// Settings changed: restore the game's values once before re-applying ours.
    grading_dirty: bool,
    /// Frames until texture filtering / LOD bias are re-applied.
    quality_in: u32,
    /// The game's own LOD bias, restored when ours is turned off.
    game_lod_bias: Option<f32>,
    /// GC handle (managed GCHandle as IntPtr) of the WorldCamera transform; 0 = not found yet.
    cam: isize,
    cam_check_in: u32,
    last_cam: Option<Vec3>,
    /// Smoothed camera velocity (m/s, world).
    cam_vel: [f32; 3],
    /// The map terrain's xz rectangle [min x, min z, max x, max z].
    rect: [f32; 4],
    /// Highest terrain point of the map.
    top: f32,
    /// The terrain material our snow textures are on, with its original values.
    surface: Option<Surface>,
    surface_check_in: u32,
    /// Hotkeys held down last frame: [toggle, preset]. A press is a key going down.
    keys_down: [bool; 2],
}

/// Terrain snow textures we replaced: GC handles of the material and its original textures.
struct Surface {
    mat: isize,
    textures: Vec<(&'static str, isize)>,
    floats: Vec<(&'static str, f32)>,
}

// The game's own roughness map stays: its shader reads that slot the other way round from ours, which
// turned the snow into a mirror for its strong sun glint.
const SURFACE_TEXTURES: [&str; 3] = ["_SurfaceTex", "_SnowNormal", "_SnowDetailNormal"];
const SURFACE_FLOATS: [&str; 4] = ["_SnowTexTiling", "_SnowNormalStrength", "_SnowDetailTiling", "_SnowDetailNormalStrength"];

static STATE: Mutex<Option<State>> = Mutex::new(None);

fn settings_path() -> PathBuf {
    crate::game_dir().join(SETTINGS_FILE)
}

fn load_settings() -> Settings {
    let path = settings_path();
    match std::fs::read_to_string(&path) {
        Ok(s) => serde_json::from_str(&s).unwrap_or_else(|e| {
            log::info(format!("atmosphere: {} is invalid ({e}); using defaults", path.display()));
            Settings::default()
        }),
        Err(_) => {
            let s = Settings::default();
            let _ = std::fs::write(&path, serde_json::to_string_pretty(&s).unwrap_or_default());
            s
        }
    }
}

/// Shader property ids and the setter, resolved once.
#[derive(Clone, Copy)]
struct Ids {
    set_vector: *const il2cpp::Method,
    fog_color: i32,
    fog_top: i32,
    fog: i32,
    fog2: i32,
    sun_dir: i32,
    sun_color: i32,
    snow: i32,
    wind: i32,
    snow_color: i32,
    cloud: i32,
    cloud2: i32,
    cloud_tint: i32,
    ao: i32,
    ao_tint: i32,
    sparkle: i32,
    shafts: i32,
    cam_vel: i32,
    speed: i32,
    bank: i32,
    bank2: i32,
    map_rect: i32,
}
unsafe impl Send for Ids {}

unsafe fn ids() -> R<Ids> {
    let sk = unity::class(unity::CORE, "UnityEngine", "Shader")?;
    let id = |n: &str| -> R<i32> {
        let r = unity::call(sk, null_mut(), "PropertyToID", &mut [il2cpp::string(n) as *mut c_void])?;
        Ok(il2cpp::unbox::<i32>(r))
    };
    Ok(Ids {
        set_vector: il2cpp::method_sig(sk, "SetGlobalVector", &["System.Int32", "UnityEngine.Vector4"]).ok_or("Shader.SetGlobalVector missing")?,
        fog_color: id("_JA_FogColor")?,
        fog_top: id("_JA_FogTop")?,
        fog: id("_JA_Fog")?,
        fog2: id("_JA_Fog2")?,
        sun_dir: id("_JA_SunDir")?,
        sun_color: id("_JA_SunColor")?,
        snow: id("_JA_Snow")?,
        wind: id("_JA_Wind")?,
        snow_color: id("_JA_SnowColor")?,
        cloud: id("_JA_Cloud")?,
        cloud2: id("_JA_Cloud2")?,
        cloud_tint: id("_JA_CloudTint")?,
        ao: id("_JA_AO")?,
        ao_tint: id("_JA_AOTint")?,
        sparkle: id("_JA_Sparkle")?,
        shafts: id("_JA_Shafts")?,
        cam_vel: id("_JA_CamVel")?,
        speed: id("_JA_Speed")?,
        bank: id("_JA_Bank")?,
        bank2: id("_JA_Bank2")?,
        map_rect: id("_JA_MapRect")?,
    })
}

unsafe fn set_vec(ids: &Ids, id: i32, v: [f32; 4]) {
    let mut id = id;
    let mut v = v;
    let _ = il2cpp::invoke(ids.set_vector, null_mut(), &mut [&mut id as *mut i32 as _, &mut v as *mut [f32; 4] as _]);
}

#[repr(C)]
struct SpanWrapper {
    begin: *const u16,
    length: i32,
}

/// Turns the GC handle a Unity 6 `_Injected` binding returns into the managed object, the way the
/// game's own `Unmarshal.UnmarshalUnityObject<T>` does. (The handle is pointer-sized; IL2CPP's
/// 32-bit `il2cpp_gchandle_*` exports would truncate it.)
unsafe fn unmarshal(handle: isize) -> R<*mut Object> {
    if handle == 0 {
        return Ok(null_mut());
    }
    let unm = unity::class(unity::CORE, "UnityEngine.Bindings", "Unmarshal")?;
    let mut h = handle;
    let boxed = unity::call(unm, null_mut(), "FromIntPtrUnsafe", &mut [&mut h as *mut isize as _])?;
    let gk = (api().object_get_class)(boxed);
    let p = (api().object_unbox)(boxed);
    let target = unity::call(gk, p, "get_Target", &mut [])?;
    let _ = unity::call(gk, p, "Free", &mut []);
    Ok(target)
}

/// Loads the bundle, spawns the prefab (kept across scene loads) and unloads the bundle again,
/// keeping the spawned object's assets.
unsafe fn spawn() -> R<*mut Object> {
    let path = crate::game_dir().join(BUNDLE_FILE);
    if !path.exists() {
        return Err(format!("{} is missing from the game folder", BUNDLE_FILE));
    }
    let load: unsafe extern "C" fn(*mut SpanWrapper, u32, u64) -> isize =
        std::mem::transmute(il2cpp::icall("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected").ok_or("AssetBundle loading isn't available")?);
    let wide: Vec<u16> = path.to_string_lossy().encode_utf16().collect();
    let mut span = SpanWrapper { begin: wide.as_ptr(), length: wide.len() as i32 };
    let bundle = unmarshal(load(&mut span, 0, 0))?;
    if unity::is_null(bundle) {
        return Err(format!("could not load {} (built for another Unity version?)", path.display()));
    }
    let unload = |b: *mut Object| {
        if let Some(f) = il2cpp::icall("UnityEngine.AssetBundle::Unload_Injected") {
            let f: unsafe extern "C" fn(*mut c_void, bool) = std::mem::transmute(f);
            let native: *mut c_void = unity::field_val(b, "m_CachedPtr").unwrap_or(null_mut());
            if !native.is_null() {
                f(native, false);
            }
        }
    };
    let go_k = unity::class(unity::CORE, "UnityEngine", "GameObject")?;
    let prefab = unity::callv(bundle, "LoadAsset", &mut [il2cpp::string(PREFAB) as *mut c_void, unity::type_object(go_k) as *mut c_void]);
    let prefab = match prefab {
        Ok(p) if !unity::is_null(p) => p,
        other => {
            unload(bundle);
            return Err(format!("prefab {PREFAB} not in the bundle: {:?}", other.err()));
        }
    };
    let obj_k = unity::class(unity::CORE, "UnityEngine", "Object")?;
    let inst = il2cpp::method_sig(obj_k, "Instantiate", &["UnityEngine.Object"]).ok_or("Object.Instantiate missing")?;
    let go = il2cpp::invoke(inst, null_mut(), &mut [prefab as *mut c_void])?;
    unity::call(obj_k, null_mut(), "DontDestroyOnLoad", &mut [go as *mut c_void])?;
    unload(bundle);
    log::info("atmosphere: spawned");
    Ok(go)
}

/// Makes sure the prefab instance exists (it can be left by an earlier core, whose statics are
/// gone after a reload). Checked every 120 frames; nothing is held between frames.
unsafe fn ensure_spawned(st: &mut State) -> R<()> {
    let go_k = unity::class(unity::CORE, "UnityEngine", "GameObject")?;
    let name = format!("{PREFAB}(Clone)");
    if st.respawn {
        st.respawn = false;
        let o = unity::call(go_k, null_mut(), "Find", &mut [il2cpp::string(&name) as *mut c_void]).unwrap_or(null_mut());
        if !unity::is_null(o) {
            let obj_k = unity::class(unity::CORE, "UnityEngine", "Object")?;
            unity::call(obj_k, null_mut(), "DestroyImmediate", &mut [o as *mut c_void])?;
        }
        st.check_in = 0;
    }
    if st.check_in > 0 {
        st.check_in -= 1;
        return Ok(());
    }
    st.check_in = 120;
    let o = unity::call(go_k, null_mut(), "Find", &mut [il2cpp::string(&name) as *mut c_void]).unwrap_or(null_mut());
    if unity::is_null(o) {
        spawn()?;
    }
    st.spawned = true;
    Ok(())
}

unsafe fn color(o: *mut Object) -> [f32; 4] {
    if o.is_null() {
        [0.0; 4]
    } else {
        il2cpp::unbox::<[f32; 4]>(o)
    }
}

/// Sky colour the game syncs into RenderSettings.fogColor (horizon colour for the time of day).
unsafe fn game_fog_color() -> [f32; 4] {
    let mut c = [0.8f32, 0.85, 0.95, 1.0];
    if let Some(f) = il2cpp::icall("UnityEngine.RenderSettings::get_fogColor_Injected") {
        let f: unsafe extern "C" fn(*mut [f32; 4]) = std::mem::transmute(f);
        f(&mut c);
    }
    c
}

/// (direction towards the sun, sun colour × intensity).
unsafe fn sun() -> ([f32; 3], [f32; 3]) {
    let fallback = ([0.3, 0.8, 0.5], [1.0, 0.95, 0.85]);
    let Ok(rs) = unity::class(unity::CORE, "UnityEngine", "RenderSettings") else { return fallback };
    let Ok(light) = unity::call(rs, null_mut(), "get_sun", &mut []) else { return fallback };
    if unity::is_null(light) {
        return fallback;
    }
    let Ok(tr) = unity::callv(light, "get_transform", &mut []) else { return fallback };
    let f = unity::call_unbox::<Vec3>(tr, "get_forward").unwrap_or(Vec3 { x: 0.0, y: -1.0, z: 0.0 });
    let c = unity::callv(light, "get_color", &mut []).map(|o| color(o)).unwrap_or([1.0; 4]);
    let i = unity::call_unbox::<f32>(light, "get_intensity").unwrap_or(1.0);
    ([-f.x, -f.y, -f.z], [c[0] * i, c[1] * i, c[2] * i])
}

unsafe fn ambient() -> [f32; 4] {
    unity::class(unity::CORE, "UnityEngine", "RenderSettings")
        .and_then(|rs| unity::call(rs, null_mut(), "get_ambientSkyColor", &mut []))
        .map(|o| color(o))
        .unwrap_or([0.5, 0.52, 0.55, 1.0])
}

/// Lowest terrain height of the loaded map (from a coarse grid of samples), its highest, and its xz
/// rectangle [min x, min z, max x, max z].
unsafe fn terrain_floor() -> Option<(f32, f32, [f32; 4])> {
    let mut lo = f32::MAX;
    let mut hi = f32::MIN;
    let mut rect = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
    for t in game::map_terrains().ok()? {
        rect = [rect[0].min(t.position.x), rect[1].min(t.position.z), rect[2].max(t.position.x + t.size.x), rect[3].max(t.position.z + t.size.z)];
        for i in 0..=24 {
            for j in 0..=24 {
                let mut p = Vec3 { x: t.position.x + t.size.x * i as f32 / 24.0, y: 0.0, z: t.position.z + t.size.z * j as f32 / 24.0 };
                if let Ok(h) = unity::callv(t.terrain, "SampleHeight", &mut [&mut p as *mut Vec3 as _]) {
                    let y = il2cpp::unbox::<f32>(h) + t.position.y;
                    lo = lo.min(y);
                    hi = hi.max(y);
                }
            }
        }
    }
    (lo < f32::MAX).then_some((lo, hi, rect))
}

fn clamp01(v: f32) -> f32 {
    v.clamp(0.0, 1.0)
}

/// Follow the rider's view rather than the map's lowest terrain, which can be thousands of metres
/// below the imported riding surface. Keep the old terrain anchor until a camera is available.
fn cloud_bank_bounds(s: &Settings, camera_height: Option<f32>, floor: f32, terrain_top: f32) -> (f32, f32) {
    let top = match camera_height.filter(|y| y.is_finite()) {
        Some(y) => y - s.cloud_bank_distance.max(0.0),
        None => floor + s.cloud_bank_top * (terrain_top - floor).max(50.0),
    };
    (top - s.cloud_bank_thickness.max(1.0), top)
}

/// Pushes the current settings and the game's sky state into the shader globals.
unsafe fn update(st: &mut State, env: *mut Object) {
    let ids = match st.ids {
        Some(i) => i,
        None => match ids() {
            Ok(i) => {
                st.ids = Some(i);
                i
            }
            Err(e) => {
                st.load_error = Some(e);
                return;
            }
        },
    };
    let s = st.settings.clone();
    if st.grading_dirty || !(s.enabled && s.grading) {
        grading::apply(env, false, &s.beautify, &s.environment, &mut st.originals);
        st.grading_dirty = false;
    }
    snow_surface(st, &s);
    if !s.enabled {
        crate::sparkle_mask::update(false, None);
        set_vec(&ids, ids.fog_color, [0.0; 4]);
        set_vec(&ids, ids.snow, [0.0; 4]);
        set_vec(&ids, ids.cloud, [0.0; 4]);
        set_vec(&ids, ids.ao, [0.0; 4]);
        set_vec(&ids, ids.sparkle, [0.0; 4]);
        set_vec(&ids, ids.shafts, [0.0; 4]);
        set_vec(&ids, ids.speed, [0.0; 4]);
        set_vec(&ids, ids.bank, [0.0; 4]);
        return;
    }
    if s.grading {
        grading::apply(env, true, &s.beautify, &s.environment, &mut st.originals);
    }
    if st.quality_in == 0 {
        quality(st, &s);
        st.quality_in = 300;
    } else {
        st.quality_in -= 1;
    }
    if st.load_error.is_none() {
        if let Err(e) = ensure_spawned(st) {
            log::info(format!("atmosphere: {e}"));
            st.load_error = Some(e);
        }
    }

    let map = game::loaded_map_id();
    let stale = match &st.floor {
        Some((id, _)) => *id != map,
        None => true,
    };
    if stale {
        if st.floor_retry == 0 {
            match terrain_floor() {
                Some((f, hi, rect)) => {
                    st.floor = Some((map, f));
                    st.top = hi;
                    st.rect = rect;
                }
                None => st.floor_retry = 300,
            }
        } else {
            st.floor_retry -= 1;
        }
    }
    let floor = st.floor.as_ref().map(|f| f.1).unwrap_or(0.0);
    let camera = camera_position(st);
    let sparkle_mask_ready = crate::sparkle_mask::update(s.sparkle > 0.0, camera.map(|p| [p.x, p.y, p.z]));

    let fc = game_fog_color();
    let (sun_dir, sun_col) = sun();
    let amb = ambient();
    let tint = |c: [f32; 4], t: [f32; 3]| [c[0] * t[0], c[1] * t[1], c[2] * t[2]];
    let f = tint(fc, s.fog_tint);
    let top = tint([f[0], f[1], f[2], 1.0], s.top_tint);
    let glow = [sun_col[0].min(4.0), sun_col[1].min(4.0), sun_col[2].min(4.0)];
    set_vec(&ids, ids.fog_color, [f[0], f[1], f[2], 1.0]);
    set_vec(&ids, ids.fog_top, [top[0], top[1], top[2], s.sky_band]);
    set_vec(&ids, ids.fog, [s.fog_density, s.fog_falloff, floor + s.fog_base_offset, clamp01(s.fog_max)]);
    set_vec(&ids, ids.fog2, [s.fog_start, clamp01(s.sky_haze), s.sky_distance, s.haze_density]);
    set_vec(&ids, ids.sun_dir, [sun_dir[0], sun_dir[1], sun_dir[2], s.sun_glow]);
    set_vec(&ids, ids.sun_color, [glow[0] * 0.5, glow[1] * 0.5, glow[2] * 0.5, s.sun_glow_sharpness]);
    set_vec(&ids, ids.snow, [clamp01(s.snow), s.snow_box, s.snow_fall, s.snow_size]);
    set_vec(&ids, ids.wind, [s.wind[0], s.wind[1], s.wind[2], s.flutter]);
    // Flakes are lit by the sky plus a little sun, never brighter than white.
    let lit = |i: usize| (amb[i] * 1.25 + glow[i] * 0.25).min(1.0);
    set_vec(&ids, ids.snow_color, [lit(0), lit(1), lit(2), clamp01(s.snow_opacity)]);
    set_vec(&ids, ids.cloud, [s.cloud_shadows.max(0.0), 1.0 / s.cloud_size.max(1.0), clamp01(s.cloud_coverage), camera.map(|p| p.y).unwrap_or(floor) + s.cloud_height]);
    set_vec(&ids, ids.cloud2, [s.cloud_speed[0], s.cloud_speed[1], s.cloud_softness, if s.debug_split { 1.0 } else { 0.0 }]);
    set_vec(&ids, ids.cloud_tint, [s.cloud_tint[0], s.cloud_tint[1], s.cloud_tint[2], 1.0]);
    set_vec(&ids, ids.ao, [s.ao.max(0.0), s.ao_radius.max(0.05), s.ao_distance, s.ao_min_angle.to_radians().sin()]);
    set_vec(&ids, ids.ao_tint, [s.ao_tint[0], s.ao_tint[1], s.ao_tint[2], 1.0]);
    set_vec(&ids, ids.sparkle, [if sparkle_mask_ready { s.sparkle.max(0.0) } else { 0.0 }, clamp01(s.sparkle_density), s.sparkle_distance, s.sparkle_size.max(0.002)]);
    set_vec(&ids, ids.shafts, [s.sun_shafts.max(0.0), s.sun_shaft_reach.clamp(0.05, 1.0), 0.96, s.sun_shaft_sharpness]);

    let mut v = camera_velocity(st, camera);
    for i in 0..3 {
        v[i] += s.debug_cam_vel[i];
    }
    let kmh = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt() * 3.6;
    let [k0, k1] = s.speed_frost_kmh;
    let t = clamp01((kmh - k0) / (k1 - k0).max(1.0));
    set_vec(&ids, ids.cam_vel, [v[0], v[1], v[2], kmh / 3.6]);
    set_vec(&ids, ids.speed, [s.speed_streaks.max(0.0), s.speed_frost.max(0.0) * t * t * (3.0 - 2.0 * t), 0.0, 0.0]);
    let (base, top) = cloud_bank_bounds(&s, camera.map(|p| p.y), floor, st.top);
    set_vec(&ids, ids.bank, [s.cloud_bank.max(0.0), base, top, 1.0 / s.cloud_bank_size.max(1.0)]);
    set_vec(&ids, ids.bank2, [s.cloud_bank_speed[0], s.cloud_bank_speed[1], clamp01(s.cloud_bank_coverage), s.cloud_bank_margin]);
    set_vec(&ids, ids.map_rect, st.rect);
}

unsafe fn mat_method(m: *mut Object, name: &str, sig: &[&str]) -> Option<*const il2cpp::Method> {
    il2cpp::method_sig((api().object_get_class)(m), name, sig)
        .or_else(|| unity::class(unity::CORE, "UnityEngine", "Material").ok().and_then(|k| il2cpp::method_sig(k, name, sig)))
}

unsafe fn get_tex(m: *mut Object, name: &str) -> *mut Object {
    let Some(f) = mat_method(m, "GetTexture", &["System.String"]) else { return null_mut() };
    il2cpp::invoke(f, m as _, &mut [il2cpp::string(name) as _]).unwrap_or(null_mut())
}

unsafe fn set_tex(m: *mut Object, name: &str, t: *mut Object) {
    if let Some(f) = mat_method(m, "SetTexture", &["System.String", "UnityEngine.Texture"]) {
        let _ = il2cpp::invoke(f, m as _, &mut [il2cpp::string(name) as _, t as _]);
    }
}

unsafe fn get_float(m: *mut Object, name: &str) -> Option<f32> {
    let f = mat_method(m, "GetFloat", &["System.String"])?;
    il2cpp::invoke(f, m as _, &mut [il2cpp::string(name) as _]).ok().filter(|r| !r.is_null()).map(|r| il2cpp::unbox::<f32>(r))
}

unsafe fn set_float(m: *mut Object, name: &str, v: f32) {
    if let Some(f) = mat_method(m, "SetFloat", &["System.String", "System.Single"]) {
        let mut v = v;
        let _ = il2cpp::invoke(f, m as _, &mut [il2cpp::string(name) as _, &mut v as *mut f32 as _]);
    }
}

/// The loaded map terrain's material (Custom/PisteSnow); a new instance per map load.
unsafe fn terrain_material() -> Option<*mut Object> {
    let t = game::map_terrains().ok()?.into_iter().next()?;
    let m = unity::callv(t.terrain, "get_materialTemplate", &mut []).ok()?;
    (!unity::is_null(m)).then_some(m)
}

/// The prefab's never-drawn holder material that keeps our snow textures loaded.
unsafe fn holder_material() -> Option<*mut Object> {
    let go_k = unity::class(unity::CORE, "UnityEngine", "GameObject").ok()?;
    let go = unity::call(go_k, null_mut(), "Find", &mut [il2cpp::string(&format!("{PREFAB}(Clone)/SnowSurface")) as _]).ok()?;
    if unity::is_null(go) {
        return None;
    }
    let rk = unity::class(unity::CORE, "UnityEngine", "Renderer").ok()?;
    let r = unity::callv(go, "GetComponent", &mut [unity::type_object(rk) as _]).ok()?;
    let m = unity::callv(r, "get_sharedMaterial", &mut []).ok()?;
    (!unity::is_null(m)).then_some(m)
}

/// Puts our snow textures on the map terrain (re-applied when a new map brings a new material) or
/// restores the game's when the feature is off. Checked every 120 frames.
unsafe fn snow_surface(st: &mut State, s: &Settings) {
    if st.surface_check_in > 0 {
        st.surface_check_in -= 1;
        return;
    }
    st.surface_check_in = 120;
    let want = s.enabled && s.snow_surface;
    let current = st.surface.as_ref().map(|x| gc_target(x.mat)).filter(|m| !unity::is_null(*m));
    let target = if want { terrain_material() } else { None };
    // Off, or the material we changed is gone / no longer the map's: restore what we can and forget it.
    if let Some(old) = st.surface.take() {
        let keep = matches!((current, target), (Some(a), Some(b)) if a == b);
        if keep {
            st.surface = Some(old);
        } else {
            if let Some(m) = current {
                for (n, h) in &old.textures {
                    set_tex(m, n, gc_target(*h));
                }
                for (n, v) in &old.floats {
                    set_float(m, n, *v);
                }
            }
            for (_, h) in &old.textures {
                gc_free(*h);
            }
            gc_free(old.mat);
        }
    }
    let (Some(m), Some(src)) = (target, if want { holder_material() } else { None }) else { return };
    if st.surface.is_none() {
        let Ok(mh) = gc_handle(m) else { return };
        let textures = SURFACE_TEXTURES.iter().map(|n| (*n, gc_handle(get_tex(m, n)).unwrap_or(0))).collect();
        let floats = SURFACE_FLOATS.iter().filter_map(|n| get_float(m, n).map(|v| (*n, v))).collect();
        st.surface = Some(Surface { mat: mh, textures, floats });
        for n in SURFACE_TEXTURES {
            let t = get_tex(src, n);
            if !unity::is_null(t) {
                set_tex(m, n, t);
            }
        }
        log::info("atmosphere: snow surface applied");
    }
    // uv = world x _WorldScale x tiling, so one repeat is 1 / (scale x tiling) metres.
    let ws = get_float(m, "_WorldScale").filter(|w| *w > 0.0).unwrap_or(0.01);
    set_float(m, "_SnowTexTiling", 1.0 / (ws * s.snow_surface_scale.max(0.1)));
    set_float(m, "_SnowNormalStrength", s.snow_surface_normal);
    set_float(m, "_SnowDetailTiling", 1.0 / (ws * s.snow_surface_dune_scale.max(0.1)));
    set_float(m, "_SnowDetailNormalStrength", s.snow_surface_dune_normal);
}

/// Static GCHandle helpers (pointer-sized handles through the managed API; see `unmarshal`).
unsafe fn gc_handle(o: *mut Object) -> R<isize> {
    let gk = unity::class("mscorlib.dll", "System.Runtime.InteropServices", "GCHandle")?;
    let mut zero: isize = 0;
    let mut normal: i32 = 2; // GCHandleType.Normal
    let r = unity::call(gk, null_mut(), "GetTargetHandle", &mut [o as *mut c_void, &mut zero as *mut isize as _, &mut normal as *mut i32 as _])?;
    Ok(il2cpp::unbox::<isize>(r))
}

unsafe fn gc_target(h: isize) -> *mut Object {
    let Ok(gk) = unity::class("mscorlib.dll", "System.Runtime.InteropServices", "GCHandle") else { return null_mut() };
    let mut h = h;
    unity::call(gk, null_mut(), "GetTarget", &mut [&mut h as *mut isize as _]).unwrap_or(null_mut())
}

unsafe fn gc_free(h: isize) {
    if let Ok(gk) = unity::class("mscorlib.dll", "System.Runtime.InteropServices", "GCHandle") {
        let mut h = h;
        let _ = unity::call(gk, null_mut(), "FreeHandle", &mut [&mut h as *mut isize as _]);
    }
}

/// Current rider-view position, shared by the cloud anchors and camera velocity calculation.
unsafe fn camera_position(st: &mut State) -> Option<Vec3> {
    if st.cam_check_in == 0 || st.cam == 0 {
        st.cam_check_in = 300;
        if st.cam != 0 {
            gc_free(st.cam);
            st.cam = 0;
        }
        if let Ok(go_k) = unity::class(unity::CORE, "UnityEngine", "GameObject") {
            let go = unity::call(go_k, null_mut(), "Find", &mut [il2cpp::string("WorldCamera") as *mut c_void]).unwrap_or(null_mut());
            if !unity::is_null(go) {
                if let Ok(tr) = unity::callv(go, "get_transform", &mut []) {
                    st.cam = gc_handle(tr).unwrap_or(0);
                }
            }
        }
    } else {
        st.cam_check_in -= 1;
    }
    let tr = if st.cam != 0 { gc_target(st.cam) } else { null_mut() };
    if unity::is_null(tr) {
        st.cam_check_in = 0;
        st.last_cam = None;
        return None;
    }
    unity::call_unbox::<Vec3>(tr, "get_position").ok().filter(|p| p.x.is_finite() && p.y.is_finite() && p.z.is_finite())
}

/// Smoothed world velocity of the game camera (m/s). Teleports, respawns and paused frames don't
/// count as movement.
unsafe fn camera_velocity(st: &mut State, position: Option<Vec3>) -> [f32; 3] {
    let Some(p) = position else { return st.cam_vel };
    let dt = unity::class(unity::CORE, "UnityEngine", "Time")
        .and_then(|k| unity::call(k, null_mut(), "get_deltaTime", &mut []))
        .map(|o| il2cpp::unbox::<f32>(o))
        .unwrap_or(0.0);
    if let (Some(last), true) = (st.last_cam, dt > 1e-4) {
        let raw = [(p.x - last.x) / dt, (p.y - last.y) / dt, (p.z - last.z) / dt];
        if raw.iter().map(|c| c * c).sum::<f32>() > 90.0 * 90.0 {
            st.cam_vel = [0.0; 3]; // a jump, not motion
        } else {
            let k = 1.0 - (-dt * 6.0).exp();
            for i in 0..3 {
                st.cam_vel[i] += (raw[i] - st.cam_vel[i]) * k;
            }
        }
    }
    if dt > 1e-4 {
        st.last_cam = Some(p);
    }
    st.cam_vel
}

/// Texture filtering and LOD bias (QualitySettings; the setters are reached as engine calls since
/// the build strips some managed wrappers).
unsafe fn quality(st: &mut State, s: &Settings) {
    if st.game_lod_bias.is_none() && st.quality_in == 0 {
        log::info(format!(
            "atmosphere: anisotropic filtering calls {} / {}",
            il2cpp::icall("UnityEngine.QualitySettings::set_anisotropicFiltering").is_some(),
            il2cpp::icall("UnityEngine.Texture::SetGlobalAnisotropicFilteringLimits").is_some()
        ));
    }
    if s.anisotropic > 0 {
        if let Some(f) = il2cpp::icall("UnityEngine.QualitySettings::set_anisotropicFiltering") {
            let f: unsafe extern "C" fn(i32) = std::mem::transmute(f);
            f(2); // ForceEnable
        }
        if let Some(f) = il2cpp::icall("UnityEngine.Texture::SetGlobalAnisotropicFilteringLimits") {
            let f: unsafe extern "C" fn(i32, i32) = std::mem::transmute(f);
            let a = s.anisotropic.clamp(1, 16);
            f(a, a);
        }
    }
    let Ok(qs) = unity::class(unity::CORE, "UnityEngine", "QualitySettings") else { return };
    let cur = unity::call(qs, null_mut(), "get_lodBias", &mut []).map(|o| il2cpp::unbox::<f32>(o)).ok();
    let want = if s.lod_bias > 0.0 && s.enabled { Some(s.lod_bias) } else { st.game_lod_bias };
    if s.lod_bias > 0.0 && st.game_lod_bias.is_none() {
        st.game_lod_bias = cur;
    }
    if let (Some(w), Some(c)) = (want, cur) {
        if (w - c).abs() > 1e-4 {
            let mut v = w;
            let _ = unity::call(qs, null_mut(), "set_lodBias", &mut [&mut v as *mut f32 as _]);
        }
    }
}

/// Runs before every `EnvironmentManager.Update` (main thread).
unsafe extern "C" fn before_env_update(this: *mut c_void, _arg: *mut c_void) {
    let _ = std::panic::catch_unwind(|| {
        // The presses run after the state lock is released, since `command` takes it itself.
        let presses = match STATE.try_lock() {
            Ok(mut g) => match g.as_mut() {
                Some(st) => {
                    update(st, this as *mut Object);
                    hotkey_presses(st)
                }
                None => Vec::new(),
            },
            Err(_) => Vec::new(),
        };
        for req in presses {
            match command(&req) {
                Ok(r) => log::info(format!("hotkey: preset {:?}, enabled {}", r["preset"].as_str().unwrap_or("?"), r["settings"]["enabled"])),
                Err(e) => log::info(format!("hotkey: {e}")),
            }
        }
    });
}

/// The `atmo` requests for the hotkeys pressed this frame. A press is a key going down; presses
/// count only while this process owns the foreground window, so typing in another program never
/// toggles anything. `reload` re-reads the json, so edits made while the game runs are picked up.
unsafe fn hotkey_presses(st: &mut State) -> Vec<Value> {
    let (enabled, toggle_key, preset_key) = (st.settings.hotkeys, st.settings.toggle_key, st.settings.preset_key);
    if !enabled {
        st.keys_down = [false; 2];
        return Vec::new();
    }
    let focused = game_focused();
    let keys = [(toggle_key, json!({"toggle": true, "reload": true})), (preset_key, json!({"nextPreset": true, "reload": true}))];
    let mut out = Vec::new();
    for (i, (vk, req)) in keys.into_iter().enumerate() {
        // The high bit of GetAsyncKeyState is set while the key is held down.
        let down = vk != 0 && (GetAsyncKeyState(vk as i32) as u16 & 0x8000) != 0;
        if down && !st.keys_down[i] && focused {
            out.push(req);
        }
        st.keys_down[i] = down;
    }
    out
}

/// True when the foreground window belongs to this process (the game).
unsafe fn game_focused() -> bool {
    let fg = GetForegroundWindow();
    if fg.is_null() {
        return false;
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(fg, &mut pid);
    pid == GetCurrentProcessId()
}

/// `EnvironmentManager.Update()`.
unsafe fn env_update_method() -> Option<*const il2cpp::Method> {
    let k = unity::class("Assembly-CSharp.dll", "", "EnvironmentManager").ok()?;
    il2cpp::method(k, "Update", 0)
}

/// Called once when the core loads: reads the settings and hooks the per-frame update.
pub unsafe fn install(host: &crate::abi::Host) {
    let mut st = State { settings: load_settings(), spawned: false, respawn: false, check_in: 0, floor: None, floor_retry: 0, load_error: None, ids: None, hooked: false, originals: Default::default(), grading_dirty: false, quality_in: 0, game_lod_bias: None, cam: 0, cam_check_in: 0, last_cam: None, cam_vel: [0.0; 3], rect: [0.0; 4], top: 0.0, surface: None, surface_check_in: 0, keys_down: [false; 2] };
    match env_update_method() {
        Some(m) => {
            let rc = (host.pre_hook)(m as *const c_void, before_env_update);
            st.hooked = rc == 0;
            log::info(format!("hook EnvironmentManager.Update (atmosphere): {}", if rc == 0 { "ok".to_string() } else { format!("failed ({rc})") }));
        }
        None => log::info("atmosphere: EnvironmentManager.Update not found"),
    }
    *STATE.lock().unwrap() = Some(st);
}

/// `{"cmd":"atmo", <any Settings fields>, "save": bool, "reload": bool, "respawn": bool, "toggle": bool,
/// "nextPreset": bool, "preset": "<name>"}`: changes settings live. `reload` re-reads the json file,
/// `respawn` re-creates the effects from the bundle file (e.g. after installing a new build), `toggle`
/// flips `enabled`, and `nextPreset` / `preset` apply a preset first, so fields in the same request
/// override it. Those switches save the json. Replies with the settings in effect.
pub fn command(req: &Value) -> R<Value> {
    if let Some(names) = req.get("inspect") {
        return inspect(names.clone());
    }
    let mut g = STATE.lock().unwrap();
    let st = g.as_mut().ok_or("atmosphere not initialised")?;
    if req.get("reload").and_then(Value::as_bool) == Some(true) {
        st.settings = load_settings();
    }
    let mut cur = serde_json::to_value(&st.settings).map_err(|e| e.to_string())?;
    // "preset" selects a preset rather than setting a value, so it is taken out of the fields.
    let mut fields = req.as_object().cloned().unwrap_or_default();
    let jump = fields.remove("preset");
    let chosen = if req.get("nextPreset").and_then(Value::as_bool) == Some(true) {
        Some(next_preset_name(&cur)?)
    } else {
        match jump {
            Some(Value::String(name)) => Some(name),
            Some(_) => return Err("preset must be a preset name".into()),
            None => None,
        }
    };
    if let Some(name) = &chosen {
        cur = with_preset(&cur, name)?;
    }
    if let Some(obj) = cur.as_object_mut() {
        merge_fields(obj, &fields);
    }
    let toggle = req.get("toggle").and_then(Value::as_bool) == Some(true);
    if toggle {
        let on = cur.get("enabled").and_then(Value::as_bool).unwrap_or(true);
        cur["enabled"] = json!(!on);
    }
    st.settings = serde_json::from_value(cur).map_err(|e| format!("bad atmosphere setting: {e}"))?;
    st.grading_dirty = true;
    st.surface_check_in = 0;
    // A failed spawn is retried after a settings change (e.g. after the bundle was installed).
    st.load_error = None;
    if req.get("respawn").and_then(Value::as_bool) == Some(true) {
        st.respawn = true;
    }
    if req.get("save").and_then(Value::as_bool) == Some(true) || toggle || chosen.is_some() {
        std::fs::write(settings_path(), serde_json::to_string_pretty(&st.settings).unwrap_or_default()).map_err(|e| e.to_string())?;
    }
    Ok(json!({
        "ok": true,
        "preset": st.settings.preset,
        "settings": st.settings,
        "hooked": st.hooked,
        "spawned": st.spawned,
        "floor": st.floor.as_ref().map(|f| f.1),
        "mapHeights": [st.floor.as_ref().map(|f| f.1), Some(st.top)],
        "cameraKmh": (st.cam_vel.iter().map(|c| c * c).sum::<f32>()).sqrt() * 3.6,
        "error": st.load_error,
    }))
}

/// The settings in effect (read by the plugin protocol; `command` changes them).
pub fn settings() -> R<Settings> {
    let g = STATE.lock().unwrap();
    Ok(g.as_ref().ok_or("atmosphere not initialised")?.settings.clone())
}

/// `{"cmd":"atmo","inspect":{"beautify":[names],"environment":[names]}}`: the game's current values.
fn inspect(names: Value) -> R<Value> {
    let list = |k: &str| -> Vec<String> {
        names.get(k).and_then(Value::as_array).map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect()).unwrap_or_default()
    };
    let (b, e) = (list("beautify"), list("environment"));
    pump::run(move || unsafe {
        let k = unity::class("Assembly-CSharp.dll", "", "EnvironmentManager")?;
        let env = *unity::find_objects(k)?.first().ok_or("no EnvironmentManager (load a map first)")?;
        Ok::<_, String>(json!({"ok": true, "values": grading::snapshot(env, &b, &e)}))
    })?
}

/// `{"cmd":"screenshot","path":"…png"}`: saved at the end of the frame.
pub fn screenshot(req: &Value) -> R<Value> {
    unsafe {
        let path = req.get("path").and_then(Value::as_str).ok_or("screenshot needs a path")?;
        let k = unity::class("UnityEngine.ScreenCaptureModule.dll", "UnityEngine", "ScreenCapture")?;
        unity::call(k, null_mut(), "CaptureScreenshot", &mut [il2cpp::string(path) as _])?;
        Ok(json!({"ok": true, "path": path}))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clouds_keep_their_distance_on_elevated_maps_and_after_respawn() {
        let s = Settings::default();
        for elevation in [100.0, 5500.0, -300.0, 12000.0] {
            let (base, top) = cloud_bank_bounds(&s, Some(elevation), 0.0, 150.0);
            assert_eq!(elevation - top, 180.0);
            assert_eq!(top - base, 250.0);
        }
    }

    #[test]
    fn cloud_distance_and_thickness_are_independent_of_terrain() {
        let s = Settings { cloud_bank_distance: 350.0, cloud_bank_thickness: 120.0, ..Settings::default() };
        assert_eq!(cloud_bank_bounds(&s, Some(6000.0), 0.0, 100.0), (5530.0, 5650.0));
        assert_eq!(cloud_bank_bounds(&s, Some(6000.0), 5900.0, 6500.0), (5530.0, 5650.0));
    }

    #[test]
    fn old_settings_gain_player_relative_clouds_and_missing_camera_has_a_fallback() {
        let s: Settings = serde_json::from_value(json!({"cloudBankTop": 0.5, "cloudBankThickness": 100.0})).unwrap();
        assert_eq!(s.cloud_bank_distance, 180.0);
        assert_eq!(cloud_bank_bounds(&s, None, 100.0, 500.0), (200.0, 300.0));
        assert_eq!(cloud_bank_bounds(&s, Some(f32::NAN), 100.0, 500.0), (200.0, 300.0));
    }
}
