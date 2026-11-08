using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Event;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>
/// Three extra cameras for playing (not for the replay), picked with the quick menu (hold D-pad down, then Triangle):
///  - POV: the same first-person view as the replay editor's POV preset, locked on the rider's head.
///  - Follow cam: the replay editor's Follow cam, with more relief (it banks into turns, widens with speed, shakes harder when fast).
///  - Drone: a drone filming the rider from above and behind: it lags, hovers, leads the shot and banks.
/// They run on the replay camera's own engine (<see cref="ReplayCameraMod"/>), with their own settings, listed below in Options > Mods.
/// </summary>
public sealed class PlayCamerasMod : PackPart
{
    protected override string PartId => "play_cameras";
    public override string Name => "Hank Mod Pack: Play Cameras";
    public override string Author => "Hank";
    public override string Description => "Settings of the three play cameras (POV, Follow cam, Drone). Pick the camera in game: hold D-pad down, then the Camera button (Triangle / Y).";

    public static PlayCamerasMod? Instance { get; private set; }

    public static readonly string[] Names = { "Game camera", "POV", "Follow cam", "Drone" };

    // ---------------------------------------------------------------- POV
    [Choice("View based on", "Head", "Head", "Shoulders", "Torso", "Spine", Key = "pov_basedon", Section = "POV",
        Tooltip = "Head: locked on the head like the replay editor's POV (it follows every movement of the body). Shoulders / Torso / Spine (lower back): only the view's movement is based on that part of the body, the camera always stays at head level; in switch it looks over the shoulder towards where you go.")]
    public string PovBasedOn { get; private set; } = "Head";
    [Slider("Follow the slope (%)", 30, 0, 100, 5, Key = "pov_slopefollow", Section = "POV",
        Tooltip = "0 = the view stays flat. Higher = it tilts down / up with the slope (Shoulders / Torso).")]
    public float PovSlopeFollow { get; private set; } = 30;
    [Slider("Follow the body's lean (%)", 10, 0, 100, 5, Key = "pov_leanfollow", Section = "POV",
        Tooltip = "How much the view nods with the body when it leans or tucks (Shoulders / Torso).")]
    public float PovLeanFollow { get; private set; } = 10;
    [Slider("FOV", 100, 40, 130, 1, Key = "pov_fov", Section = "POV")]
    public float PovFov { get; private set; } = 100;
    [Slider("Eye height above the head (m)", 0.3, -0.6, 0.4, 0.01, Key = "pov_up", Section = "POV")]
    public float PovUp { get; private set; } = 0.3f;
    [Slider("Eye forward (m)", 0, -0.3, 0.6, 0.01, Key = "pov_fwd", Section = "POV")]
    public float PovForward { get; private set; }
    [Slider("Look up / down (degrees)", 0, -30, 30, 1, Key = "pov_pitch", Section = "POV")]
    public float PovPitch { get; private set; }
    [Slider("Horizon height in the frame (% from the top)", 45, 15, 70, 1, Key = "pov_horizon", Section = "POV")]
    public float PovHorizon { get; private set; } = 45;
    [Slider("Stabilizer %", 40, 0, 100, 5, Key = "pov_stab", Section = "POV", Tooltip = "Steadies the view like a gimbal: heavy smoothing, level horizon, less head shake. 0 = off.")]
    public float PovStab { get; private set; } = 40;
    [Slider("Smoothing %", 20, 0, 95, 5, Key = "pov_smooth", Section = "POV")]
    public float PovSmooth { get; private set; } = 20;
    [Slider("Head shake %", 10, 0, 100, 5, Key = "pov_shake", Section = "POV")]
    public float PovShake { get; private set; } = 10;
    [Slider("Speed FOV boost (degrees)", 0, 0, 30, 1, Key = "pov_boost", Section = "POV")]
    public float PovBoost { get; private set; }
    [Toggle("Hide the rider's head and helmet", true, Key = "pov_hide", Section = "POV")]
    public bool PovHide { get; private set; } = true;

    // ---------------------------------------------------------------- Follow cam
    [Slider("Angle around the rider (degrees)", 135, 0, 360, 5, Key = "cam_angle", Section = "Follow cam",
        Tooltip = "180 = straight behind, 0 = in front, 90 = right, 270 = left.")]
    public float FollowAngle { get; private set; } = 135;
    [Slider("Distance (m)", 1.8, 0.8, 30, 0.1, Key = "cam_dist", Section = "Follow cam")]
    public float FollowDistance { get; private set; } = 1.8f;
    [Slider("Height (m)", 0.6, -2, 15, 0.1, Key = "cam_height", Section = "Follow cam")]
    public float FollowHeight { get; private set; } = 0.6f;
    [Slider("Aim height on the rider (m)", -0.08, -0.5, 2, 0.05, Key = "cam_aim", Section = "Follow cam")]
    public float FollowAim { get; private set; } = -0.08f;
    [Slider("Keep the rider in frame %", 60, 0, 100, 5, Key = "cam_focus", Section = "Follow cam")]
    public float FollowFocus { get; private set; } = 60;
    [Slider("Reactivity", 9, 0.5, 12, 0.5, Key = "cam_react", Section = "Follow cam")]
    public float FollowReactivity { get; private set; } = 9;
    [Slider("Lead-ahead %", 19, 0, 100, 5, Key = "cam_lead", Section = "Follow cam")]
    public float FollowLead { get; private set; } = 19;
    [Slider("Pull back with speed %", 30, 0, 100, 5, Key = "cam_pull", Section = "Follow cam",
        Tooltip = "The camera backs off as you go faster (full effect at 150 km/h).")]
    public float FollowPull { get; private set; } = 30;
    [Slider("Hand-held shake %", 70, 0, 100, 5, Key = "cam_shake", Section = "Follow cam")]
    public float FollowShake { get; private set; } = 70;
    [Slider("Relief: shake gets stronger with speed %", 60, 0, 100, 5, Key = "relief_shake", Section = "Follow cam")]
    public float ReliefShake { get; private set; } = 60;
    [Slider("Relief: bank into turns (degrees)", 10, 0, 30, 1, Key = "relief_bank", Section = "Follow cam",
        Tooltip = "The picture tilts into the turn, like a camera on a moving platform.")]
    public float ReliefBank { get; private set; } = 10;
    [Slider("Relief: FOV widening with speed (degrees)", 15, 0, 40, 1, Key = "relief_fov", Section = "Follow cam",
        Tooltip = "The field of view opens up as you go faster (full effect at 150 km/h): a stronger sense of speed.")]
    public float ReliefFov { get; private set; } = 15;

    // ---------------------------------------------------------------- Drone
    [Slider("Distance (m)", 11, 3, 40, 0.5, Key = "drone_dist", Section = "Drone")]
    public float DroneDistance { get; private set; } = 11;
    [Slider("Altitude above the rider (m)", 8, 1, 40, 0.5, Key = "drone_height", Section = "Drone")]
    public float DroneHeight { get; private set; } = 8;
    [Slider("Angle around the rider (degrees)", 180, 0, 360, 5, Key = "drone_angle", Section = "Drone",
        Tooltip = "180 = behind the rider, 0 = in front, 90 = right, 270 = left.")]
    public float DroneAngle { get; private set; } = 180;
    [Slider("Aim height on the rider (m)", 0.4, -0.5, 2, 0.05, Key = "drone_aim", Section = "Drone")]
    public float DroneAim { get; private set; } = 0.4f;
    [Slider("Reactivity (low = lazy drone)", 2, 0.5, 8, 0.5, Key = "drone_react", Section = "Drone")]
    public float DroneReactivity { get; private set; } = 2;
    [Slider("Lead-ahead %", 70, 0, 100, 5, Key = "drone_lead", Section = "Drone")]
    public float DroneLead { get; private set; } = 70;
    [Slider("Orbit around the rider (degrees / second)", 0, -60, 60, 1, Key = "drone_sweep", Section = "Drone")]
    public float DroneSweep { get; private set; }
    [Slider("Hover drift %", 40, 0, 100, 5, Key = "drone_hover", Section = "Drone", Tooltip = "Slow floating drift and a faint motor buzz. 0 = a rock steady drone.")]
    public float DroneHover { get; private set; } = 40;
    [Slider("Bank into turns (degrees)", 12, 0, 30, 1, Key = "drone_bank", Section = "Drone")]
    public float DroneBank { get; private set; } = 12;
    [Slider("FOV widening with speed (degrees)", 6, 0, 40, 1, Key = "drone_fov", Section = "Drone")]
    public float DroneFov { get; private set; } = 6;
    [Slider("Shake %", 10, 0, 100, 5, Key = "drone_shake", Section = "Drone")]
    public float DroneShake { get; private set; } = 10;

    // ---------------------------------------------------------------- State

    /// <summary>The camera editor stopped the game on purpose: the play camera keeps working (it is what is being edited).</summary>
    public static bool EditorHold;
    bool wasPaused, seenHidden;

    /// <summary>An interface (pause menu, /place...) is open: the game's own camera must be left alone, and the camera editor closes.</summary>
    public static bool UiOpen;
    /// <summary>The game's spawn/drone view is open (D-pad up): the play camera steps aside.</summary>
    public static bool SpawnView;
    /// <summary>The camera the rider chose ("POV", "Follow", "Drone" or ""), even while the game's camera is held for a menu.</summary>
    public string Chosen => current != "" ? current : stashed;
    public bool Active => current != "" || stashed != "";

    static bool CursorVisible()
    {
        try { return Il2Cpp.Unbox<byte>(Unity.CallStatic(Unity.Class("UnityEngine.Cursor"), "get_visible")) != 0; }
        catch { return false; }
    }
    string current = "";   // "", "POV", "Follow", "Drone"

    /// <summary>The camera shown in the quick menu: "Game camera", "POV", "Follow cam" or "Drone".</summary>
    public string CurrentName => current switch { "POV" => "POV", "Follow" => "Follow cam", "Drone" => "Drone", _ => "Game camera" };
    public bool IsOn => current != "";

    public override void OnLoad()
    {
        ImportPackSettings();
        Instance = this;
        ExitReplay.Subscribe(_ => MainThread.Run(() => { if (current != "") Push(); }));
        Log("Play cameras ready.");
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (current != "" && key != "enabled") MainThread.Run(Push);
    }

    public override void OnUnload()
    {
        try { MainThread.Run(() => ReplayCameraMod.Instance?.SetPlayProfile("", null)); } catch { }
    }

    /// <summary>Picks a camera by its name in <see cref="Names"/>. Main thread.</summary>
    public void Select(string name)
    {
        current = name switch { "POV" => "POV", "Follow cam" => "Follow", "Drone" => "Drone", _ => "" };
        Push();
    }

    /// <summary>Called every frame by Look Around, after the game moved its camera. While the free look is active the game's camera is left alone.</summary>
    string stashed = "";
    readonly HashSet<string> holds = new();
    /// <summary>
    /// The game needs its own camera (spawn selector, pause menu): the chosen camera is switched off completely while at least one hold is on,
    /// and comes back when the last one is released.
    /// </summary>
    public void Hold(string why, bool on)
    {
        if (on)
        {
            if (!holds.Add(why)) return;
            if (holds.Count == 1) { stashed = current; if (current != "") { current = ""; Push(); } }
        }
        else
        {
            if (!holds.Remove(why)) return;
            if (holds.Count == 0) { if (stashed != "") { current = stashed; Push(); } stashed = ""; }
        }
        Log($"play camera: {why} {(on ? "opened, the game's camera takes over" : "closed, camera back")}");
    }
    public bool Holding(string why) => holds.Contains(why);

    public void SpawnSelector(bool open) { SpawnView = open; Hold("spawn selector", open); }

    public void Frame(nint follow, bool freeLook)
    {
        // The pause menu (and /place...) shows the mouse cursor, which the game hides while playing: seen at once, no delay.
        var visible = CursorVisible();
        if (!visible) seenHidden = true;
        UiOpen = seenHidden && visible;
        if (current == "" || ReplayCameraMod.Instance is not { } rc) return;
        // Free look, or the game paused (pause menu, with its own view of the phone): the game's camera is left alone.
        var paused = UiOpen || !EditorHold && Unity.DeltaTime < 1e-4f;
        if (paused != wasPaused) { wasPaused = paused; Log(paused ? "play camera: game paused, the game's camera takes over" : "play camera: back in game"); }
        if (freeLook || paused) { rc.SuspendPlay(); return; }
        rc.ApplyPose(follow);
    }

    // ---------------------------------------------------------------- Live editing (the camera editor)
    // While a value is being changed (many times a second) only the camera is updated; the settings file and the Options menu are touched
    // once, by CommitLive, when the editor closes or changes tab.

    Dictionary<string, PropertyInfo>? byKey;
    readonly HashSet<string> liveDirty = new();

    PropertyInfo? Prop(string key)
    {
        byKey ??= GetType().GetProperties()
            .Select(pi => (pi, a: pi.GetCustomAttributes(typeof(SettingAttribute), true).FirstOrDefault() as SettingAttribute))
            .Where(x => x.a?.Key is { Length: > 0 }).ToDictionary(x => x.a!.Key!, x => x.pi);
        return byKey.TryGetValue(key, out var found) ? found : null;
    }

    public double LiveDouble(string key) => Prop(key) is { } pi ? Convert.ToDouble(pi.GetValue(this)) : GetDouble(key);
    public bool LiveBool(string key) => Prop(key) is { } pi ? Convert.ToBoolean(pi.GetValue(this)) : GetBool(key);
    public string LiveString(string key) => Prop(key) is { } pi ? Convert.ToString(pi.GetValue(this)) ?? "" : GetString(key);

    public void SetLive(string key, object value)
    {
        if (Prop(key) is not { } pi) return;
        pi.SetValue(this, Convert.ChangeType(value, pi.PropertyType));
        liveDirty.Add(key);
        if (current != "") Push();
    }

    /// <summary>Writes the values changed with <see cref="SetLive"/> to the settings (Options > Mods).</summary>
    public void CommitLive()
    {
        // Written straight into the settings store: SetSetting waits for the game's main thread (60 s timeout each = a long freeze).
        if (liveDirty.Count == 0) return;
        try
        {
            if (StoreFld?.GetValue(this) is SettingsStore store)
            {
                foreach (var key in liveDirty.ToList())
                {
                    if (Prop(key) is not { } pi) continue;
                    var v = pi.GetValue(this);
                    store.Set(key, v switch { bool b => JsonValue.Create(b)!, string t => JsonValue.Create(t)!, _ => JsonValue.Create(Convert.ToDouble(v))! });
                }
                store.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(store, null);
            }
        }
        catch (Exception e) { Log($"play cameras: could not save the camera settings: {e.Message}"); }
        liveDirty.Clear();
    }
    static readonly FieldInfo? StoreFld = typeof(JibbersMod).GetField("Store", BindingFlags.Instance | BindingFlags.NonPublic);

    // ---------------------------------------------------------------- Presets (saved sets of values per camera)
    static string PresetFile => Path.Combine(Bootstrap.ModsDir, "Camera Presets.json");
    JsonObject? presetDoc;

    JsonObject PresetDoc()
    {
        if (presetDoc is null)
        {
            try { if (File.Exists(PresetFile)) presetDoc = JsonNode.Parse(File.ReadAllText(PresetFile)) as JsonObject; } catch (Exception e) { Log("camera presets: could not read the file: " + e.Message); }
            presetDoc ??= new JsonObject();
        }
        return presetDoc;
    }

    JsonObject PresetsOf(string cam)
    {
        var d = PresetDoc();
        if (d[cam] is not JsonObject o) { o = new JsonObject(); d[cam] = o; }
        return o;
    }

    void WritePresets()
    {
        try { File.WriteAllText(PresetFile, PresetDoc().ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log("camera presets: could not save the file: " + e.Message); }
    }

    public List<string> PresetNames(string cam) => PresetsOf(cam).Select(kv => kv.Key).ToList();

    public void SavePreset(string cam, string name, IEnumerable<Setting> list)
    {
        var o = new JsonObject();
        foreach (var s in list)
            o[s.Key] = s.Kind switch
            {
                SettingKind.Toggle => JsonValue.Create(LiveBool(s.Key)),
                SettingKind.Choice => JsonValue.Create(LiveString(s.Key)),
                _ => JsonValue.Create(LiveDouble(s.Key)),
            };
        PresetsOf(cam)[name] = o;
        WritePresets();
        Log($"camera presets: saved '{name}' ({cam})");
    }

    public void ApplyPreset(string cam, string name, IEnumerable<Setting> list)
    {
        if (PresetsOf(cam)[name] is not JsonObject o) return;
        foreach (var s in list)
        {
            if (o[s.Key] is not JsonNode n) continue;
            try
            {
                switch (s.Kind)
                {
                    case SettingKind.Toggle: SetLive(s.Key, n.GetValue<bool>()); break;
                    case SettingKind.Choice: SetLive(s.Key, n.GetValue<string>()); break;
                    default: SetLive(s.Key, Math.Clamp(n.GetValue<double>(), s.Min, s.Max)); break;
                }
            }
            catch { }
        }
        Log($"camera presets: loaded '{name}' ({cam})");
    }

    public bool RenamePreset(string cam, string old, string name)
    {
        var o = PresetsOf(cam);
        if (!o.ContainsKey(old) || name != old && o.ContainsKey(name)) return false;
        var items = o.Select(kv => (Key: kv.Key == old ? name : kv.Key, kv.Value)).ToList();
        o.Clear();
        foreach (var (k, v) in items) o[k] = v;
        WritePresets();
        Log($"camera presets: '{old}' renamed '{name}' ({cam})");
        return true;
    }

    public void DeletePreset(string cam, string name)
    {
        PresetsOf(cam).Remove(name);
        WritePresets();
        Log($"camera presets: deleted '{name}' ({cam})");
    }

    void Push()
    {
        if (ReplayCameraMod.Instance is not { } rc) { Log("play cameras: the replay camera engine is not loaded."); return; }
        rc.SetPlayProfile(current, current == "" ? null : Parameters(current));
    }

    /// <summary>
    /// The full parameter set of a camera. The POV starts from the replay editor's "Realistic (look ahead)" preset (the values below that are not
    /// in the settings are the preset's), so it is the same view as in the replay. Keys are the replay camera's own setting keys.
    /// </summary>
    /// <summary>The camera's values as currently set in the game, for the replay camera ("In-game camera" preset).</summary>
    public Dictionary<string, object> LiveParameters(string profile) => Parameters(profile);

    Dictionary<string, object> Parameters(string profile)
    {
        var p = new Dictionary<string, object>();
        switch (profile)
        {
            case "POV":
                p["pov_level"] = 100.0; p["pov_fisheye"] = false; p["pov_shoulder"] = 0.0; p["pov_headyaw"] = 100.0; p["pov_maxpitch"] = 20.0; p["pov_base"] = "Shoulders";
                if (PovBasedOn == "Head") { p["pov_lock"] = "Head (locked)"; p["pov_slope"] = 30.0; p["pov_headpitch"] = 10.0; }
                else
                {
                    p["pov_lock"] = "Real (look where you go)"; p["pov_base"] = PovBasedOn == "Torso" ? "Chest" : PovBasedOn == "Spine" ? "Hips" : "Shoulders";
                    p["pov_slope"] = (double)PovSlopeFollow; p["pov_headpitch"] = (double)PovLeanFollow; p["pov_shoulder"] = 100.0;
                }
                p["pov_shift"] = 0.1; p["pov_airstab"] = 0.0; p["pov_neck"] = 140.0; p["pov_axis"] = "Z";   // the rig is always the same: learning the axes while riding sometimes got them wrong (a tilted view)
                p["pov_fov"] = (double)PovFov; p["pov_up"] = (double)PovUp; p["pov_fwd"] = (double)PovForward; p["pov_pitch"] = (double)PovPitch;
                p["pov_horizon"] = (double)PovHorizon; p["pov_smooth"] = (double)PovSmooth; p["pov_stab"] = (double)PovStab; p["pov_shake"] = (double)PovShake;
                p["pov_boost"] = (double)PovBoost; p["pov_hide"] = PovHide;
                break;
            case "Follow":
                p["cam_fisheye"] = false; p["cam_roll"] = 0.0; p["cam_sweep"] = 0.0;
                p["cam_angle"] = (double)FollowAngle; p["cam_dist"] = (double)FollowDistance; p["cam_height"] = (double)FollowHeight;
                p["cam_aim"] = (double)FollowAim; p["cam_focus"] = (double)FollowFocus; p["cam_react"] = (double)FollowReactivity;
                p["cam_lead"] = (double)FollowLead; p["cam_pull"] = (double)FollowPull; p["cam_shake"] = (double)FollowShake;
                p["x_speedshake"] = (double)ReliefShake; p["x_bank"] = (double)ReliefBank; p["x_fovkick"] = (double)ReliefFov;
                break;
            default: // Drone
                p["cam_fisheye"] = false; p["cam_roll"] = 0.0; p["cam_pull"] = 0.0; p["cam_focus"] = 50.0;
                p["cam_angle"] = (double)DroneAngle; p["cam_dist"] = (double)DroneDistance; p["cam_height"] = (double)DroneHeight;
                p["cam_aim"] = (double)DroneAim; p["cam_react"] = (double)DroneReactivity; p["cam_lead"] = (double)DroneLead;
                p["cam_sweep"] = (double)DroneSweep; p["cam_shake"] = (double)DroneShake;
                p["x_hover"] = (double)DroneHover; p["x_bank"] = (double)DroneBank; p["x_fovkick"] = (double)DroneFov; p["x_speedshake"] = 0.0;
                break;
        }
        return p;
    }
}
