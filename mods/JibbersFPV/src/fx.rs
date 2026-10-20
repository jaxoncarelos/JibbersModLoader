//! The pilot's OSD (a screen-space TextMeshPro canvas) and the drone's sound. Everything lives under
//! one GameObject that is destroyed on landing. The game's build strips AudioClip.Create and most
//! AudioSource setters, so the sounds (from `synth`) ship as configured AudioSources in
//! jibbers_fpv.bundle; only pitch, volume and Play are used at runtime.
use crate::math::blend;
use crate::synth;
use jibbers_mod_sdk::il2cpp::{self, api, Class, Method, Object};
use jibbers_mod_sdk::unity::{self, R};
use std::ffi::c_void;
use std::ptr::null_mut;

const TMP: &str = "Unity.TextMeshPro.dll";
const UI: &str = "UnityEngine.UI.dll";
const UI_MODULE: &str = "UnityEngine.UIModule.dll";
const AUDIO: &str = "UnityEngine.AudioModule.dll";
const BUNDLE: &str = "jibbers_fpv.bundle";
const AUDIO_PREFAB: &str = "JibbersFPVAudio";
/// Text refreshes this often; sound every frame.
const HUD_INTERVAL: f32 = 1. / 15.;
/// Seconds the control hint stays up after take-off.
const HINT_TIME: f32 = 8.;

/// What the OSD shows this frame.
#[derive(Clone, Debug, Default)]
pub struct Osd {
    pub mode: &'static str,
    pub power: f32,
    /// m/s.
    pub speed: f32,
    /// Height above the ground under the drone, if it is in range.
    pub altitude: Option<f32>,
    pub flight_time: f32,
    pub locked: Option<String>,
    pub disarmed: bool,
    pub paused: bool,
    pub crosshair: bool,
    pub gamepad: bool,
}

/// The four OSD blocks: bottom-left, bottom-right, top-centre, centre. Rich text for TMP.
pub fn osd_text(o: &Osd) -> [String; 4] {
    const BOX: &str = "<mark=#00000080 padding=\"14,14,6,6\">";
    let bars = (o.power.clamp(0., 1.) * 10.).round() as usize;
    let mut status = o.mode.to_uppercase();
    if o.disarmed {
        status.push_str("  <color=#ff5050>DISARMED</color>");
    }
    let left = format!(
        "{BOX}{status}\nTHR [{}{}] {:>3.0}%</mark>",
        "|".repeat(bars),
        ".".repeat(10 - bars),
        o.power.clamp(0., 1.) * 100.
    );
    let altitude = match o.altitude {
        Some(a) => format!("{a:5.1} m"),
        None => "  --- m".into(),
    };
    let secs = o.flight_time.max(0.) as u32;
    let right = format!(
        "{BOX}{:>4.0} km/h\nALT {altitude}\n{:02}:{:02}</mark>",
        o.speed * 3.6,
        secs / 60,
        secs % 60
    );
    let mut top = Vec::new();
    if o.paused {
        top.push("PAUSED".to_string());
    }
    if let Some(name) = &o.locked {
        top.push(format!("<color=#7fe0ff>LOCK  {name}</color>"));
    }
    if o.flight_time < HINT_TIME {
        top.push(if o.gamepad {
            "L3+R3 land   LB mode   RB lock (hold: release)   Y return".into()
        } else {
            "F9 land   F10 mode   Middle mouse lock   Home return".into()
        });
    }
    let top = if top.is_empty() { String::new() } else { format!("{BOX}{}</mark>", top.join("\n")) };
    let centre = if o.crosshair { "<alpha=#B0>+".to_string() } else { String::new() };
    [left, right, top, centre]
}

fn class(img: &str, ns: &str, name: &str) -> R<*mut Class> {
    unsafe { unity::class(img, ns, name) }
}
unsafe fn sig(k: *mut Class, name: &str, params: &[&str]) -> R<*const Method> {
    il2cpp::method_sig(k, name, params).ok_or_else(|| format!("{name}({}) missing", params.join(", ")))
}
unsafe fn type_of(k: *mut Class) -> *mut c_void {
    unity::type_object(k)
}
unsafe fn f32_arg(v: &mut f32) -> *mut c_void {
    v as *mut f32 as *mut c_void
}
unsafe fn set_f32(o: *mut Object, name: &str, mut v: f32) -> R<()> {
    unity::callv(o, name, &mut [f32_arg(&mut v)]).map(|_| ())
}
unsafe fn set_i32(o: *mut Object, name: &str, mut v: i32) -> R<()> {
    unity::callv(o, name, &mut [&mut v as *mut i32 as _]).map(|_| ())
}
unsafe fn set_bool(o: *mut Object, name: &str, mut v: bool) -> R<()> {
    unity::callv(o, name, &mut [&mut v as *mut bool as _]).map(|_| ())
}
unsafe fn set_v2(o: *mut Object, name: &str, mut v: [f32; 2]) -> R<()> {
    unity::callv(o, name, &mut [v.as_mut_ptr() as _]).map(|_| ())
}

/// A TMP alignment by its enum name (values differ between TMP versions).
unsafe fn alignment(name: &str) -> R<i32> {
    let k = class(TMP, "TMPro", "TextAlignmentOptions")?;
    let f = il2cpp::field(k, name).ok_or_else(|| format!("TextAlignmentOptions.{name} missing"))?;
    let mut v = 0i32;
    (api().field_static_get_value)(f, &mut v as *mut i32 as _);
    Ok(v)
}

/// A new rooted GameObject.
unsafe fn game_object(name: &str) -> R<(*mut Object, isize)> {
    let k = class(unity::CORE, "UnityEngine", "GameObject")?;
    let o = (api().object_new)(k);
    if o.is_null() {
        return Err("cannot allocate a GameObject".into());
    }
    let handle = unity::gc_handle(o)?;
    il2cpp::invoke(sig(k, ".ctor", &["System.String"])?, o, &mut [il2cpp::string(name) as _])?;
    Ok((o, handle))
}
unsafe fn add(go: *mut Object, k: *mut Class) -> R<*mut Object> {
    let gk = class(unity::CORE, "UnityEngine", "GameObject")?;
    let c = il2cpp::invoke(sig(gk, "AddComponent", &["System.Type"])?, go, &mut [type_of(k)])?;
    if unity::is_null(c) {
        return Err("AddComponent returned null".into());
    }
    Ok(c)
}
unsafe fn destroy(o: *mut Object) {
    if unity::is_null(o) {
        return;
    }
    if let Ok(k) = class(unity::CORE, "UnityEngine", "Object") {
        if let Some(m) = il2cpp::method_sig(k, "Destroy", &["UnityEngine.Object"]) {
            let _ = il2cpp::invoke(m, null_mut(), &mut [o]);
        }
    }
}

struct Sound {
    motor: isize,
    wind: isize,
    crash: isize,
    motor_voice: (f32, f32),
}

pub struct Fx {
    root: isize,
    texts: Vec<isize>,
    shown: Vec<String>,
    sound: Option<Sound>,
    hud_wait: f32,
}

impl Fx {
    /// Builds the OSD and/or sound under a new GameObject. Either part failing only disables that
    /// part; the error says why.
    pub unsafe fn new(hud: bool, sound: bool) -> (Self, Vec<String>) {
        let mut errors = Vec::new();
        let mut fx = Fx { root: 0, texts: Vec::new(), shown: Vec::new(), sound: None, hud_wait: 0. };
        let root = match game_object("JibbersFPV") {
            Ok((o, h)) => {
                fx.root = h;
                o
            }
            Err(e) => {
                errors.push(e);
                return (fx, errors);
            }
        };
        if hud {
            if let Err(e) = fx.build_hud(root) {
                errors.push(format!("no OSD: {e}"));
            }
        }
        if sound {
            match Sound::new(root) {
                Ok(s) => fx.sound = Some(s),
                Err(e) => errors.push(format!("no sound: {e}")),
            }
        }
        (fx, errors)
    }

    unsafe fn build_hud(&mut self, root: *mut Object) -> R<()> {
        let canvas = add(root, class(UI_MODULE, "UnityEngine", "Canvas")?)?;
        set_i32(canvas, "set_renderMode", 0)?; // ScreenSpaceOverlay
        set_i32(canvas, "set_sortingOrder", 900)?;
        let scaler = add(root, class(UI, "UnityEngine.UI", "CanvasScaler")?)?;
        set_i32(scaler, "set_uiScaleMode", 1)?; // ScaleWithScreenSize
        set_v2(scaler, "set_referenceResolution", [1920., 1080.])?;
        set_f32(scaler, "set_matchWidthOrHeight", 0.5)?;
        let tk = class(TMP, "TMPro", "TextMeshProUGUI")?;
        let font = unity::find_objects(class(TMP, "TMPro", "TMP_Text")?)?
            .into_iter()
            .filter(|t| !unity::is_null(*t))
            .find_map(|t| unity::callv(t, "get_font", &mut []).ok().filter(|f| !unity::is_null(*f)));
        let tr = unity::callv(root, "get_transform", &mut [])?;
        let tk_transform = class(unity::CORE, "UnityEngine", "Transform")?;
        let set_parent = sig(tk_transform, "SetParent", &["UnityEngine.Transform", "System.Boolean"])?;
        // anchor/pivot, offset from it (reference pixels), alignment, size
        let blocks = [
            ([0., 0.], [40., 40.], "BottomLeft", 30.),
            ([1., 0.], [-40., 40.], "BottomRight", 30.),
            ([0.5, 1.], [0., -40.], "Top", 26.),
            ([0.5, 0.5], [0., 0.], "Center", 44.),
        ];
        for (i, (anchor, offset, align, size)) in blocks.into_iter().enumerate() {
            let (go, go_handle) = game_object(&format!("OSD {i}"))?;
            let child = unity::callv(go, "get_transform", &mut [])?;
            let mut keep = false;
            il2cpp::invoke(set_parent, child, &mut [tr, &mut keep as *mut bool as _])?;
            let text = add(go, tk);
            // Parented: the scene now owns it.
            let _ = unity::gc_free(go_handle);
            let text = text?;
            if let Some(f) = font {
                unity::callv(text, "set_font", &mut [f])?;
            }
            set_f32(text, "set_fontSize", size)?;
            set_bool(text, "set_richText", true)?;
            set_bool(text, "set_raycastTarget", false)?;
            set_i32(text, "set_alignment", alignment(align)?)?;
            // Newer TMP: textWrappingMode (0 = NoWrap); older: enableWordWrapping.
            if set_i32(text, "set_textWrappingMode", 0).is_err() {
                let _ = set_bool(text, "set_enableWordWrapping", false);
            }
            let mut white = [1f32, 1., 1., 1.];
            unity::callv(text, "set_color", &mut [white.as_mut_ptr() as _])?;
            let rt = unity::callv(text, "get_rectTransform", &mut [])?;
            set_v2(rt, "set_anchorMin", anchor)?;
            set_v2(rt, "set_anchorMax", anchor)?;
            set_v2(rt, "set_pivot", anchor)?;
            set_v2(rt, "set_anchoredPosition", offset)?;
            set_v2(rt, "set_sizeDelta", [900., 200.])?;
            unity::callv(text, "set_text", &mut [il2cpp::string("") as _])?;
            self.texts.push(unity::gc_handle(text)?);
            self.shown.push(String::new());
        }
        Ok(())
    }

    /// Per frame: sound follows the motors; the OSD text is refreshed at 15 Hz.
    pub unsafe fn update(&mut self, real_dt: f32, osd: &Osd, volume: f32, crashed: bool) -> R<()> {
        if let Some(s) = self.sound.as_mut() {
            s.update(real_dt, osd, volume, crashed)?;
        }
        self.hud_wait -= real_dt;
        if self.texts.is_empty() || self.hud_wait > 0. {
            return Ok(());
        }
        self.hud_wait = HUD_INTERVAL;
        for (i, text) in osd_text(osd).into_iter().enumerate() {
            if self.shown[i] != text {
                let t = unity::gc_target(self.texts[i])?;
                if !unity::is_null(t) {
                    unity::callv(t, "set_text", &mut [il2cpp::string(&text) as _])?;
                }
                self.shown[i] = text;
            }
        }
        Ok(())
    }

    pub unsafe fn destroy(&mut self) {
        if let Some(s) = self.sound.take() {
            s.destroy();
        }
        for h in self.texts.drain(..) {
            let _ = unity::gc_free(h);
        }
        if self.root != 0 {
            destroy(unity::gc_target(self.root).unwrap_or(null_mut()));
            let _ = unity::gc_free(self.root);
            self.root = 0;
        }
    }
}

#[repr(C)]
struct SpanWrapper {
    begin: *const u16,
    length: i32,
}

/// Turns the GC handle a Unity 6 `_Injected` binding returns into the managed object (as
/// Atmosphere does: the AssetBundle API is reached through its injected internal calls).
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

/// Loads the audio prefab from jibbers_fpv.bundle and instantiates it under `parent`; the bundle is
/// unloaded again straight away (the instance keeps its clips).
unsafe fn spawn_audio(parent: *mut Object) -> R<*mut Object> {
    let path = jibbers_mod_sdk::mods_dir().join(BUNDLE);
    if !path.exists() {
        return Err(format!("{BUNDLE} is missing from JibbersMods"));
    }
    let load: unsafe extern "C" fn(*mut SpanWrapper, u32, u64) -> isize = std::mem::transmute(
        il2cpp::icall("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected").ok_or("AssetBundle loading isn't available")?,
    );
    let wide: Vec<u16> = path.to_string_lossy().encode_utf16().collect();
    let mut span = SpanWrapper { begin: wide.as_ptr(), length: wide.len() as i32 };
    let bundle = unmarshal(load(&mut span, 0, 0))?;
    if unity::is_null(bundle) {
        return Err(format!("could not load {} (built for another Unity version?)", path.display()));
    }
    let go_k = class(unity::CORE, "UnityEngine", "GameObject")?;
    let prefab = unity::callv(bundle, "LoadAsset", &mut [il2cpp::string(AUDIO_PREFAB) as _, type_of(go_k)]);
    let instance = match prefab {
        Ok(p) if !unity::is_null(p) => {
            let obj_k = class(unity::CORE, "UnityEngine", "Object")?;
            let inst = sig(obj_k, "Instantiate", &["UnityEngine.Object", "UnityEngine.Transform"])?;
            let tr = unity::callv(parent, "get_transform", &mut [])?;
            il2cpp::invoke(inst, null_mut(), &mut [p, tr])
        }
        Ok(_) => Err(format!("{AUDIO_PREFAB} is not in {BUNDLE}")),
        Err(e) => Err(e),
    };
    if let Some(f) = il2cpp::icall("UnityEngine.AssetBundle::Unload_Injected") {
        let f: unsafe extern "C" fn(*mut c_void, bool) = std::mem::transmute(f);
        let native: *mut c_void = unity::field_val(bundle, "m_CachedPtr").unwrap_or(null_mut());
        if !native.is_null() {
            f(native, false);
        }
    }
    instance
}

impl Sound {
    unsafe fn new(root: *mut Object) -> R<Self> {
        let audio = spawn_audio(root)?;
        let source_k = class(AUDIO, "UnityEngine", "AudioSource")?;
        let component_k = class(unity::CORE, "UnityEngine", "Component")?;
        let get = sig(component_k, "GetComponent", &["System.Type"])?;
        let tr = unity::callv(audio, "get_transform", &mut [])?;
        let source = |name: &str| -> R<isize> {
            let child = unity::callv(tr, "Find", &mut [il2cpp::string(name) as _])?;
            if unity::is_null(child) {
                return Err(format!("{AUDIO_PREFAB}/{name} missing"));
            }
            let s = il2cpp::invoke(get, child, &mut [type_of(source_k)])?;
            if unity::is_null(s) {
                return Err(format!("{AUDIO_PREFAB}/{name} has no AudioSource"));
            }
            unity::gc_handle(s)
        };
        let sound = Sound { motor: source("Motor")?, wind: source("Wind")?, crash: source("Crash")?, motor_voice: (0.4, 0.) };
        for h in [sound.motor, sound.wind] {
            unity::callv(unity::gc_target(h)?, "Play", &mut [])?;
        }
        Ok(sound)
    }

    unsafe fn update(&mut self, dt: f32, osd: &Osd, volume: f32, crashed: bool) -> R<()> {
        let (pitch, level) = synth::motor_voice(osd.power, !osd.disarmed);
        // Motors spin up and down; they don't jump.
        let k = blend(dt, if osd.disarmed { 0.35 } else { 0.06 });
        self.motor_voice.0 += (pitch - self.motor_voice.0) * k;
        self.motor_voice.1 += (level - self.motor_voice.1) * k;
        let mute = if osd.paused { 0. } else { volume.clamp(0., 1.) };
        let motor = unity::gc_target(self.motor)?;
        set_f32(motor, "set_pitch", self.motor_voice.0)?;
        set_f32(motor, "set_volume", self.motor_voice.1 * mute * 0.6)?;
        let (wind_pitch, wind_level) = synth::wind_voice(osd.speed);
        let wind = unity::gc_target(self.wind)?;
        set_f32(wind, "set_pitch", wind_pitch)?;
        set_f32(wind, "set_volume", wind_level * mute * 0.5)?;
        if crashed && mute > 0. {
            let crash = unity::gc_target(self.crash)?;
            set_f32(crash, "set_volume", mute)?;
            unity::callv(crash, "Play", &mut [])?;
        }
        Ok(())
    }

    unsafe fn destroy(self) {
        // The sources go with the root GameObject.
        for h in [self.motor, self.wind, self.crash] {
            let _ = unity::gc_free(h);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn osd_shows_mode_throttle_speed_and_lock() {
        let o = Osd {
            mode: "Acro",
            power: 0.62,
            speed: 15.,
            altitude: Some(3.25),
            flight_time: 75.,
            locked: Some("you".into()),
            crosshair: true,
            ..Default::default()
        };
        let [left, right, top, centre] = osd_text(&o);
        assert!(left.contains("ACRO") && left.contains("[||||||....]") && left.contains("62%"));
        assert!(right.contains("54 km/h") && right.contains("3.2 m") && right.contains("01:15"));
        assert!(top.contains("LOCK  you") && !top.contains("F9"), "hint gone after a few seconds");
        assert!(centre.contains('+'));
    }
    #[test]
    fn osd_flags_and_hint() {
        let o = Osd { mode: "Angle", disarmed: true, paused: true, gamepad: true, ..Default::default() };
        let [left, right, top, centre] = osd_text(&o);
        assert!(left.contains("DISARMED") && left.contains("[..........]"));
        assert!(right.contains("--- m"));
        assert!(top.contains("PAUSED") && top.contains("L3+R3"));
        assert!(centre.is_empty());
    }
}
