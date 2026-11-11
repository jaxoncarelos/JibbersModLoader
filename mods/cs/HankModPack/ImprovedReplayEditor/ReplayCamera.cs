using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Game.Event;
using Jibbers.Sdk.Game.Player;
using Jibbers.Sdk.Game.Replay;

namespace HankModPack;

/// <summary>
/// Replay editor cameras. Three modes (chosen in the "Replay Camera" tab of the keyframe panel, or in Options > Mods):
///  - Off: the game's own replay camera / keyframes.
///  - Follow cam: a cameraman filming the rider (behind, beside, in front or from a drone), with lag, lead-ahead, hand-held shake and speed FOV.
///  - POV: first person, locked on the rider's head.
/// Only active inside the replay editor; leaving a mode gives the camera back (FOV, near plane, hidden head).
/// </summary>
public sealed unsafe class ReplayCameraMod : PackPart
{
    protected override string PartId => "replay_camera";
    public override string Name => "Hank Mod Pack: Replay Camera";
    public override string Author => "Hank";
    public override string Description => "Replay editor: Follow cam (cameraman) and POV (first person on the rider's head).";

    // ---------------------------------------------------------------- Settings

    [Choice("Replay camera", "Off", "Off", "Follow cam", "POV", Key = "mode", Section = "Mode",
        Tooltip = "Off = the game's replay camera. Follow cam = a cameraman films the rider. POV = first person, locked on the rider's head. Replaces the keyframe camera while on.")]
    public string Mode { get; private set; } = "Off";

    // ---- Follow cam
    [Choice("Shot preset", "Behind", "Behind", "Side (right)", "Side (left)", "Front", "Drone", "Skate fisheye", "Low chase", "In-game camera", "In-game drone", "Custom", Key = "cam_style", Section = "Follow cam",
        Tooltip = "Picking a preset sets the angle, distance, height and fisheye in one go. Then adjust each one freely. Skate fisheye = the low, close, wide shot of skate videographers.")]
    public string CamStyle { get => PlayVal("cam_style", _camStyle); private set => _camStyle = value; }
    string _camStyle = "Behind";

    [Slider("Angle around the rider (degrees)", 135, 0, 360, 5, Key = "cam_angle", Section = "Follow cam",
        Tooltip = "Where the cameraman stands, relative to the direction the rider travels: 180 = behind, 0 = in front, 90 = on the right, 270 = on the left.")]
    public float CamAngle { get => PlayVal("cam_angle", _camAngle); private set => _camAngle = value; }
    float _camAngle = 135;

    [Slider("Orbit around the rider (degrees / second)", 0, -90, 90, 1, Key = "cam_sweep", Section = "Follow cam",
        Tooltip = "The cameraman slowly circles the rider. 0 = stays at the angle above.")]
    public float CamSweep { get; private set; }

    [Slider("Distance (m)", 1.8, 0.8, 30, 0.1, Key = "cam_dist", Section = "Follow cam")]
    public float CamDistance { get => PlayVal("cam_dist", _camDistance); private set => _camDistance = value; }
    float _camDistance = 1.8f;

    [Slider("Height (m)", 0.6, -2, 15, 0.1, Key = "cam_height", Section = "Follow cam")]
    public float CamHeight { get => PlayVal("cam_height", _camHeight); private set => _camHeight = value; }
    float _camHeight = 0.6f;

    [Slider("Aim height on the rider (m)", -0.08, -0.5, 2, 0.05, Key = "cam_aim", Section = "Follow cam", Tooltip = "Which part of the rider the camera points at (0 = the hips, 1 = the head).")]
    public float CamAim { get => PlayVal("cam_aim", _camAim); private set => _camAim = value; }
    float _camAim = -0.08f;

    [Slider("Tilt / dutch angle (degrees)", 0, -45, 45, 1, Key = "cam_roll", Section = "Follow cam")]
    public float CamRoll { get; private set; }

    [Slider("Keep the rider in frame %", 60, 0, 100, 5, Key = "cam_focus", Section = "Follow cam",
        Tooltip = "Focus on the rider: 0 = free cinematic framing (he may leave the picture), 100 = he stays right in the middle. Also shortens the lead-ahead and the aim lag.")]
    public float CamFocus { get => PlayVal("cam_focus", _camFocus); private set => _camFocus = value; }
    float _camFocus = 60;

    [Slider("Reactivity", 9, 0.5, 12, 0.5, Key = "cam_react", Section = "Follow cam",
        Tooltip = "Low = the cameraman lags behind and swings wide in turns (cinematic). High = glued to the rider.")]
    public float CamReactivity { get => PlayVal("cam_react", _camReactivity); private set => _camReactivity = value; }
    float _camReactivity = 9;

    [Slider("Lead-ahead %", 19, 0, 100, 5, Key = "cam_lead", Section = "Follow cam", Tooltip = "How far ahead of the rider the cameraman aims.")]
    public float CamLead { get => PlayVal("cam_lead", _camLead); private set => _camLead = value; }
    float _camLead = 19;

    [Slider("Pull back with speed %", 2, 0, 100, 5, Key = "cam_pull", Section = "Follow cam", Tooltip = "The cameraman backs off as the rider speeds up (full effect at 150 km/h).")]
    public float CamPull { get => PlayVal("cam_pull", _camPull); private set => _camPull = value; }
    float _camPull = 2;

    [Slider("Hand-held shake %", 70, 0, 100, 5, Key = "cam_shake", Section = "Follow cam")]
    public float CamShake { get => PlayVal("cam_shake", _camShake); private set => _camShake = value; }
    float _camShake = 70;

    // The follow cam uses the game's own field of view (no FOV options of its own).
    public float CamFov => 0;

    // ---- Fisheye (both cameras)
    [Toggle("Fisheye (Follow cam)", false, Key = "cam_fisheye", Section = "Fisheye",
        Tooltip = "Wide fisheye look. Uses the lens distortion slider of the game's Camera Effects menu (open that menu once during the replay so the mod can find it) and widens the FOV.")]
    public bool CamFisheye { get; private set; }

    [Toggle("Fisheye (POV)", false, Key = "pov_fisheye", Section = "Fisheye")]
    public bool PovFisheye { get; private set; }

    [Slider("Fisheye strength %", 60, 0, 100, 5, Key = "fish_strength", Section = "Fisheye")]
    public float FisheyeStrength { get => PlayVal("fish_strength", _fisheyeStrength); private set => _fisheyeStrength = value; }
    float _fisheyeStrength = 60;

    [Slider("Fisheye extra FOV (degrees)", 25, 0, 60, 1, Key = "fish_fov", Section = "Fisheye", Tooltip = "Added to the field of view while the fisheye is on.")]
    public float FisheyeFov { get => PlayVal("fish_fov", _fisheyeFov); private set => _fisheyeFov = value; }
    float _fisheyeFov = 25;

    [Toggle("Reverse the distortion", false, Key = "fish_invert", Section = "Fisheye",
        Tooltip = "If the picture bulges the wrong way (pinched instead of fisheye), turn this on.")]
    public bool FisheyeInvert { get; private set; }

    // ---- POV
    [Choice("POV preset", "Realistic (look ahead)", "Realistic (look ahead)", "GoPro helmet", "Chest cam", "Cinematic", "In-game camera", "Custom", Key = "pov_preset", Section = "POV",
        Tooltip = "Realistic: the eye is on the head and, when the rider goes switch, looks over the shoulder to see where he is going. GoPro: locked on the helmet, ultra wide. Chest cam: low, looks where he goes. Cinematic: steady, level horizon.")]
    public string PovPreset { get => PlayVal("pov_preset", _povPreset); private set => _povPreset = value; }
    string _povPreset = "Realistic (look ahead)";

    [Choice("POV lock", "Head (locked)", "Real (look where you go)", "Head (locked)", "Head (level horizon)", "Travel direction", Key = "pov_lock", Section = "POV",
        Tooltip = "Real: follows the head; in switch the view turns over the shoulder towards the direction of travel. Head (locked): exactly the head. Level horizon: the head without roll. Travel direction: always looks where the rider goes.")]
    public string PovLock { get => PlayVal("pov_lock", _povLock); private set => _povLock = value; }
    string _povLock = "Head (locked)";

    [Slider("Over-the-shoulder look in switch %", 0, 0, 100, 5, Key = "pov_shoulder", Section = "POV",
        Tooltip = "Real mode: how far the view turns towards the direction of travel when the rider goes switch (100 = looks exactly where he goes).")]
    public float PovShoulder { get => PlayVal("pov_shoulder", _povShoulder); private set => _povShoulder = value; }
    float _povShoulder = 0;

    [Slider("Calm the view in the air %", 70, 0, 100, 5, Key = "pov_airstab", Section = "POV",
        Tooltip = "In the air the view ignores most of the body spinning and keeps looking along the travel, so it does not whip around during tricks.")]
    public float PovAirStab { get => PlayVal("pov_airstab", _povAirStab); private set => _povAirStab = value; }
    float _povAirStab = 70;

    [Slider("Max head turn over the shoulder (degrees)", 140, 30, 180, 5, Key = "pov_neck", Section = "POV",
        Tooltip = "Switch: how far from the torso the view can turn towards the direction of travel (neck + torso twist).")]
    public float PovNeckTurn { get => PlayVal("pov_neck", _povNeckTurn); private set => _povNeckTurn = value; }
    float _povNeckTurn = 140;

    [Slider("Shift towards the shoulder in switch (m)", 0.1, 0, 0.6, 0.01, Key = "pov_shift", Section = "POV",
        Tooltip = "Switch on the ground: the eye moves a little to the side of the shoulder the rider is looking over, like a real head turn. 0 = off.")]
    public float PovShift { get => PlayVal("pov_shift", _povShift); private set => _povShift = value; }
    float _povShift = 0.1f;

    [Choice("Aim from", "Shoulders", "Shoulders", "Chest", "Hips", "Head", Key = "pov_base", Section = "POV",
        Tooltip = "Real / Travel modes: which part of the body decides where the view points. Shoulders = the line of the shoulders (a rider looks where his shoulders face), Chest = the torso, Hips, or Head.")]
    public string PovBase { get => PlayVal("pov_base", _povBase); private set => _povBase = value; }
    string _povBase = "Shoulders";

    [Slider("Horizon height in the frame (% from the top)", 45, 15, 70, 1, Key = "pov_horizon", Section = "POV",
        Tooltip = "50 = the horizon in the middle. Lower = the horizon sits higher in the picture (more ground and skis visible); about 35-40 is the usual action-cam framing.")]
    public float PovHorizon { get => PlayVal("pov_horizon", _povHorizon); private set => _povHorizon = value; }
    float _povHorizon = 45;

    [Slider("Look along the slope %", 30, 0, 100, 5, Key = "pov_slope", Section = "POV",
        Tooltip = "Real / Travel modes: how much the view follows the slope. 0 = looks at the horizon, 100 = looks exactly along the ground the rider travels on.")]
    public float PovSlope { get => PlayVal("pov_slope", _povSlope); private set => _povSlope = value; }
    float _povSlope = 30;

    [Slider("Torso lean influence %", 10, 0, 100, 5, Key = "pov_headpitch", Section = "POV",
        Tooltip = "Real mode: how much the forward lean of the torso (a tuck) moves the view. Low = the view keeps looking ahead.")]
    public float PovHeadPitch { get => PlayVal("pov_headpitch", _povHeadPitch); private set => _povHeadPitch = value; }
    float _povHeadPitch = 10;

    [Slider("Body turn influence %", 100, 0, 100, 5, Key = "pov_headyaw", Section = "POV",
        Tooltip = "Real mode: how much the body turning (spins, tricks) turns the view. 0 = always looks where the rider goes.")]
    public float PovHeadYaw { get => PlayVal("pov_headyaw", _povHeadYaw); private set => _povHeadYaw = value; }
    float _povHeadYaw = 100;

    [Slider("Max look up / down (degrees)", 20, 5, 90, 1, Key = "pov_maxpitch", Section = "POV", Tooltip = "Real / Travel modes: the view never tilts more than this.")]
    public float PovMaxPitch { get => PlayVal("pov_maxpitch", _povMaxPitch); private set => _povMaxPitch = value; }
    float _povMaxPitch = 20;

    [Choice("Head forward axis", "Auto", "Auto", "Z", "-Z", "X", "-X", "Y", "-Y", Key = "pov_axis", Section = "POV",
        Tooltip = "Auto learns the head's axes by itself after a second of riding. If the view is sideways or upside down, pick an axis by hand.")]
    public string PovAxis { get => PlayVal("pov_axis", _povAxis); private set => _povAxis = value; }
    string _povAxis = "Auto";

    [Slider("Forward offset (m)", 0, -0.3, 0.6, 0.01, Key = "pov_fwd", Section = "POV", Tooltip = "Moves the eye in front of the head so you do not see the inside of the face.")]
    public float PovForward { get => PlayVal("pov_fwd", _povForward); private set => _povForward = value; }
    float _povForward = 0f;

    [Slider("Up offset (m)", 0.3, -0.6, 0.4, 0.01, Key = "pov_up", Section = "POV")]
    public float PovUp { get => PlayVal("pov_up", _povUp); private set => _povUp = value; }
    float _povUp = 0.3f;

    [Slider("Look up / down (degrees)", 20, -30, 30, 1, Key = "pov_pitch", Section = "POV")]
    public float PovPitch { get => PlayVal("pov_pitch", _povPitch); private set => _povPitch = value; }
    float _povPitch = 20;

    [Slider("Stabilizer %", 0, 0, 100, 5, Key = "pov_stab", Section = "POV", Tooltip = "Steadies the view like a gimbal: heavy smoothing, the horizon is kept level and the head shake is reduced. 0 = off.")]
    public float PovStab { get => PlayVal("pov_stab", _povStab); private set => _povStab = value; }
    float _povStab;
    [Slider("Smoothing %", 20, 0, 95, 5, Key = "pov_smooth", Section = "POV", Tooltip = "0 = locked on the head. Higher = softer, steadier view (less head shake).")]
    public float PovSmooth { get => PlayVal("pov_smooth", _povSmooth); private set => _povSmooth = value; }
    float _povSmooth = 20;

    [Slider("Horizon stabilisation %", 100, 0, 100, 5, Key = "pov_level", Section = "POV", Tooltip = "How much of the head's roll is removed (100 = always level).")]
    public float PovLevel { get => PlayVal("pov_level", _povLevel); private set => _povLevel = value; }
    float _povLevel = 100;

    [Slider("Head shake %", 10, 0, 100, 5, Key = "pov_shake", Section = "POV", Tooltip = "Natural body movement of the eye, stronger with speed. 0 = none.")]
    public float PovShake { get => PlayVal("pov_shake", _povShake); private set => _povShake = value; }
    float _povShake = 10;

    [Slider("FOV", 100, 40, 130, 1, Key = "pov_fov", Section = "POV")]
    public float PovFov { get => PlayVal("pov_fov", _povFov); private set => _povFov = value; }
    float _povFov = 100;

    [Slider("Speed FOV boost (degrees)", 0, 0, 30, 1, Key = "pov_boost", Section = "POV")]
    public float PovBoost { get => PlayVal("pov_boost", _povBoost); private set => _povBoost = value; }
    float _povBoost = 0;

    [Toggle("Hide the rider's head and helmet", true, Key = "pov_hide", Section = "POV", Tooltip = "Hides what hangs on the head bone (head, helmet, goggles) so it does not block the view.")]
    public bool PovHideHead { get => PlayVal("pov_hide", _povHideHead); private set => _povHideHead = value; }
    bool _povHideHead = true;

    // ---------------------------------------------------------------- State

    bool inReplay => HankModPack.CameraOwnership.InReplay;
    int lastFrame = -1;
    string activeMode = "Off";

    nint camCache;
    GameRef? cameraRoot, followRoot;
    GameRef? headRef;
    nint headCache;
    GameRef? lampRef;
    float nextHeadScan;
    nint followSelf;

    // pose computed this frame
    bool havePose;
    Vector3 poseP;
    Quaternion poseR = Quaternion.Identity;
    float poseFov = -1;

    // follow-cam state
    Vector3? camPos, lookPt, prevFocus;
    Vector3 vel;
    float yawDeg;
    float fovShown;
    bool fovApplied;
    /// <summary>The camera's FOV as the game set it (not touched by this mod in the replay).</summary>
    float GameFov()
    {
        try { if (camCache != 0 && !Unity.IsNull(camCache)) return Unity.Call<float>(camCache, "get_fieldOfView"); } catch { }
        return origFov > 0 ? origFov : 60;
    }
    float swayClock;
    // pov state
    Vector3? povF, povU;
    // restore info
    float origFov = -1, origNear = -1;
    readonly List<(GameRef R, bool Was)> hidden = new();
    float lastScale;

    public override void OnLoad()
    {
        ImportPackSettings();
        EnterReplay.Subscribe(_ => {  MainThread.Run(() => { Reset(); StartFresh(); }); });
        ExitReplay.Subscribe(_ => {  MainThread.Run(() => { try { Release(); } catch { } Reset(); }); });
        Instance = this;
        CameraCallbacks.Follow.Register(this, null, self => { if (inReplay) ApplyPose(self); }, order: 10);
        CameraCallbacks.Recorder.Register(this, null, _ => { if (inReplay) ApplyPose(0); }, order: 10);
        ChangeScene.Subscribe(_ => {  Release(); Reset(); });

        // The camera is driven from the Improved Replay Editor's own hooks (TimelineFx), so that the camera shake and the other effects are applied on top of it.
    }

    /// <summary>Called by the replay editor every frame, after the game moved the camera and before the shake is added.</summary>
    public void ApplyPose(nint follow)
    {
        try
        {
            if (follow != 0 && follow != followSelf) { followRoot?.Dispose(); followRoot = new GameRef(follow); followSelf = follow; }
            Tick();
        }
        catch (Exception e) { LogOnce("pose", e); }
    }

    protected override void OnDeactivated() => Release();

    protected override void OnPackUnload()
    {

        followSelf = 0; followRoot?.Dispose(); followRoot = null;
        try { MainThread.Run(() => { try { Release(); } finally { Reset(); } }); } catch { }
    }

    readonly HashSet<string> logged = new();
    void LogOnce(string key, Exception e) { if (logged.Add(key)) Log($"replay camera ({key}): {e.Message}"); }

    public static ReplayCameraMod? Instance { get; private set; }

    /// <summary>A camera value: the keyframed one at the playhead if the clip has camera keyframes, else the setting.</summary>
    static float K(string key, float baseValue) => TimelineFx.TimelineFxMod.CameraKey("Cam/" + key) ?? baseValue;

    /// <summary>The camera values a keyframe holds (interpolated between keyframes like the other effects).</summary>
    public Dictionary<string, float> CameraSnapshot()
    {
        return new Dictionary<string, float>
        {
            ["Cam/FollowOn"] = 1, ["Cam/PovOn"] = 1,
            ["Cam/Angle"] = CamAngle, ["Cam/Distance"] = CamDistance, ["Cam/Height"] = CamHeight, ["Cam/Aim"] = CamAim,
            ["Cam/Roll"] = CamRoll, ["Cam/Focus"] = CamFocus, ["Cam/FisheyeStrength"] = FisheyeStrength,
            ["Cam/TimeScale"] = 1f, ["Cam/PovTimeScale"] = 1f,
            ["Cam/PovHorizon"] = PovHorizon, ["Cam/PovShift"] = PovShift, ["Cam/PovPitch"] = PovPitch,
        };
    }

    /// <summary>The Improved Replay Editor reads these to drive the lens distortion of the game's Camera Effects.</summary>
    public bool WantsFisheye => inReplay && Enabled && ((activeMode == "Follow cam" && CamFisheye) || (activeMode == "POV" && PovFisheye));
    public float FisheyeAmount => Math.Clamp(K("FisheyeStrength", FisheyeStrength) / 100f, 0, 1);

    protected override void OnPackSettingChanged(string key, JsonNode value)
    {
        string text;
        try { text = value.GetValue<string>(); } catch { return; }
        if (key == "cam_style") ApplyShot(text);
        else if (key == "pov_preset") ApplyPov(text);
    }

    void Put(params (string Key, object Value)[] values)
    {
        foreach (var (k, v) in values)
        {
            try
            {
                SetSetting(k, v switch
                {
                    string t => JsonValue.Create(t)!,
                    bool b => JsonValue.Create(b)!,
                    float f => JsonValue.Create((double)f)!,
                    double d => JsonValue.Create(d)!,
                    int i => JsonValue.Create((double)i)!,
                    _ => JsonValue.Create(v.ToString())!,
                });
            }
            catch (Exception e) { Log($"preset: could not set '{k}': {e.Message}"); }
        }
    }

    /// <summary>The Follow cam / POV exactly as set in the game (Play Cameras), copied into the replay camera.</summary>
    Dictionary<string, double>? replayExtras;

    void ApplyInGame(string cam)
    {
        if (PlayCamerasMod.Instance is not { } pc) return;
        replayExtras = null;
        foreach (var kv in pc.LiveParameters(cam))
        {
            // x_* are the extras of the play cameras (drone hover, relief...): not settings, kept here and read by the camera engine.
            if (kv.Key.StartsWith("x_")) (replayExtras ??= new())[kv.Key] = Convert.ToDouble(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
            else Put((kv.Key, kv.Value));
        }
        Log($"replay camera: the in-game {cam} settings are used");
    }

    void ApplyShot(string name)
    {
        if (name == "In-game camera") { ApplyInGame("Follow"); return; }
        if (name == "In-game drone") { ApplyInGame("Drone"); return; }
        replayExtras = null;
        switch (name)
        {
            case "Behind": Put(("cam_angle", 135f), ("cam_dist", 1.8f), ("cam_height", 0.6f), ("cam_aim", -0.08f), ("cam_roll", 0f), ("cam_react", 9f), ("cam_lead", 19f), ("cam_pull", 2f), ("cam_shake", 70f), ("cam_sweep", 0f), ("cam_focus", 60f), ("cam_fisheye", false), ("fish_strength", 60f)); break;
            case "Side (right)": Put(("cam_angle", 90f), ("cam_dist", 5f), ("cam_height", 1.4f), ("cam_aim", 0.6f), ("cam_roll", 0f), ("cam_react", 3.5f), ("cam_lead", 40f), ("cam_shake", 25f), ("cam_sweep", 0f), ("cam_focus", 60f), ("cam_fisheye", false)); break;
            case "Side (left)": Put(("cam_angle", 270f), ("cam_dist", 5f), ("cam_height", 1.4f), ("cam_aim", 0.6f), ("cam_roll", 0f), ("cam_react", 3.5f), ("cam_lead", 40f), ("cam_shake", 25f), ("cam_sweep", 0f), ("cam_focus", 60f), ("cam_fisheye", false)); break;
            case "Front": Put(("cam_angle", 0f), ("cam_dist", 5f), ("cam_height", 1.5f), ("cam_aim", 0.7f), ("cam_roll", 0f), ("cam_react", 4f), ("cam_lead", 0f), ("cam_shake", 25f), ("cam_sweep", 0f), ("cam_focus", 60f), ("cam_fisheye", false)); break;
            case "Drone": Put(("cam_angle", 180f), ("cam_dist", 11f), ("cam_height", 8f), ("cam_aim", 0.4f), ("cam_roll", 0f), ("cam_react", 2f), ("cam_lead", 70f), ("cam_shake", 10f), ("cam_sweep", 0f), ("cam_focus", 60f), ("cam_fisheye", false)); break;
            case "Skate fisheye": Put(("cam_angle", 20f), ("cam_dist", 2.3f), ("cam_height", 0.45f), ("cam_aim", 0.5f), ("cam_roll", 0f), ("cam_react", 7f), ("cam_lead", 0f), ("cam_shake", 20f), ("cam_sweep", 0f), ("cam_focus", 80f), ("cam_pull", 20f), ("cam_fisheye", true), ("fish_strength", 75f), ("fish_fov", 30f)); break;
            case "Low chase": Put(("cam_angle", 180f), ("cam_dist", 3.5f), ("cam_height", 0.6f), ("cam_aim", 0.6f), ("cam_roll", 0f), ("cam_react", 5f), ("cam_lead", 20f), ("cam_shake", 30f), ("cam_sweep", 0f), ("cam_focus", 60f), ("cam_fisheye", false)); break;
        }
        Log($"Follow cam preset: {name}");
    }

    void ApplyPov(string name)
    {
        if (name == "In-game camera") { ApplyInGame("POV"); return; }
        switch (name)
        {
            case "Realistic (look ahead)": Put(("pov_lock", "Head (locked)"), ("pov_shoulder", 0f), ("pov_fwd", 0f), ("pov_up", 0.3f), ("pov_pitch", 20f), ("pov_smooth", 20f), ("pov_level", 100f), ("pov_shake", 10f), ("pov_fov", 100f), ("pov_boost", 0f), ("pov_hide", true), ("pov_fisheye", false), ("pov_slope", 30f), ("pov_headpitch", 10f), ("pov_headyaw", 100f), ("pov_maxpitch", 20f), ("pov_base", "Shoulders"), ("pov_horizon", 45f), ("pov_shift", 0.1f), ("pov_airstab", 70f), ("pov_neck", 140f)); break;
            case "GoPro helmet": Put(("pov_lock", "Head (locked)"), ("pov_shoulder", 0f), ("pov_fwd", 0.2f), ("pov_up", 0.15f), ("pov_pitch", 0f), ("pov_smooth", 0f), ("pov_level", 0f), ("pov_shake", 10f), ("pov_fov", 115f), ("pov_boost", 0f), ("pov_hide", true), ("pov_fisheye", true), ("fish_strength", 60f), ("fish_fov", 10f), ("pov_base", "Head")); break;
            case "Chest cam": Put(("pov_lock", "Real (look where you go)"), ("pov_base", "Chest"), ("pov_horizon", 45f), ("pov_headpitch", 25f), ("pov_headyaw", 100f), ("pov_shoulder", 0f), ("pov_fwd", 0.25f), ("pov_up", -0.45f), ("pov_pitch", 0f), ("pov_smooth", 25f), ("pov_level", 80f), ("pov_shake", 35f), ("pov_fov", 110f), ("pov_boost", 6f), ("pov_hide", false), ("pov_fisheye", false), ("pov_slope", 60f), ("pov_maxpitch", 35f)); break;
            case "Cinematic": Put(("pov_lock", "Head (level horizon)"), ("pov_shoulder", 0f), ("pov_fwd", 0.12f), ("pov_up", 0.06f), ("pov_pitch", 0f), ("pov_smooth", 60f), ("pov_level", 90f), ("pov_shake", 0f), ("pov_fov", 90f), ("pov_boost", 10f), ("pov_hide", true), ("pov_fisheye", false)); break;
        }
        Log($"POV preset: {name}");
    }

    /// <summary>Picks the camera from the "Replay Camera" tab of the keyframe panel.</summary>
    public void SetMode(string mode) => Set("mode", mode);

    public void Set(string key, string value) => Apply(key, JsonValue.Create(value)!);
    public void Set(string key, float value) => Apply(key, JsonValue.Create((double)value)!);
    public void Set(string key, bool value) => Apply(key, JsonValue.Create(value)!);

    void Apply(string key, JsonNode value)
    {
        try { SetSetting(key, value); }
        catch (Exception e) { Log($"could not change '{key}': {e.Message}"); }
    }

    /// <summary>A new replay always starts on the game's own camera, with the default presets.</summary>
    void StartFresh()
    {
        try
        {
            ApplyShot("Behind"); ApplyPov("Realistic (look ahead)");
            Set("cam_style", "Behind"); Set("pov_preset", "Realistic (look ahead)");
            // Replay opened while a play camera (POV / Follow cam) was chosen: it starts with that same camera, no keyframe needed.
            var chosen = PlayCamerasMod.Instance?.Chosen ?? "";
            Set("mode", chosen == "POV" ? "POV" : chosen is "Follow" or "Drone" ? "Follow cam" : "Off");
            if (chosen is "POV" or "Follow" or "Drone")
            {
                ApplyInGame(chosen);
                Set(chosen == "POV" ? "pov_preset" : "cam_style", chosen == "Drone" ? "In-game drone" : "In-game camera");
                Log($"replay camera: the replay starts with the {chosen} camera");
            }
            calibs.Clear(); calibActive = -1; calibPending = false;
        }
        catch (Exception e) { Log($"could not reset the replay camera: {e.Message}"); }
    }

    void Reset()
    {
        camPos = lookPt = prevFocus = null;
        povF = povU = null;
        vel = new Vector3(0, 0, 0);
        headRef?.Dispose(); headRef = null; headCache = 0;
        lampRef?.Dispose(); lampRef = null;
        camCache = 0;
        cameraRoot?.Dispose(); cameraRoot = null;
        lastFrame = -1;
        havePose = false;
        poseFov = 0;
        nextHeadScan = 0;
    }


    // ---------------------------------------------------------------- Play cameras (while riding, not in a replay)

    string playProfile = "";
    Dictionary<string, object>? playParams;

    /// <summary>True while one of the play cameras (POV / Follow / Drone) drives the camera outside of the replay.</summary>
    public bool PlayActive => !inReplay && playProfile != "";
    public string PlayProfile => playProfile;

    /// <summary>Every parameter read through <see cref="PlayVal{T}"/> comes from here while a play camera is on, instead of the replay settings.</summary>
    T PlayVal<T>(string key, T value)
    {
        if (!PlayActive || playParams is not { } p || !p.TryGetValue(key, out var o)) return value;
        try { return (T)Convert.ChangeType(o, typeof(T), System.Globalization.CultureInfo.InvariantCulture); } catch { return value; }
    }

    /// <summary>An extra number the play cameras use (relief, drone hover...): 0 outside of them.</summary>
    float Extra(string key) => PlayActive && playParams is { } p && p.TryGetValue(key, out var o) ? Convert.ToSingle(o, System.Globalization.CultureInfo.InvariantCulture)
        : inReplay && replayExtras is { } re && re.TryGetValue(key, out var v) ? (float)v : 0f;

    /// <summary>Switches a play camera on ("POV", "Follow", "Drone") or off (""). Main thread.</summary>
    public void SetPlayProfile(string profile, Dictionary<string, object>? parameters)
    {
        var changed = profile != playProfile;
        if (changed) { try { Release(); } catch { } Reset(); sweepDeg = 0; }
        playParams = parameters;
        playProfile = profile;
        if (changed) Log(profile == "" ? "play camera: off (the game's camera)" : $"play camera: {profile}");
    }

    /// <summary>Gives the camera back to the game (menu, replay...) without forgetting the chosen profile.</summary>
    public void SuspendPlay() { if (activeMode != "Off") { try { Release(); } catch { } } }

    // ---------------------------------------------------------------- Frame

    static int FrameCount => Il2Cpp.Unbox<int>(Unity.CallStatic(Unity.Class("UnityEngine.Time"), "get_frameCount"));
    static float RealDelta => Il2Cpp.Unbox<float>(Unity.CallStatic(Unity.Class("UnityEngine.Time"), "get_unscaledDeltaTime"));

    float lastPh; int loopLogs, fovLogs, tsProbeLogs; long lastFovLog, lastTsProbe;

    void Tick()
    {
        var play = PlayActive;
        if ((!inReplay && !play) || (!play && !Enabled))
        {
            if (activeMode != "Off") Release();
            return;
        }
        if (inReplay)
        {
            // Diagnostic: the game's FOV at every loop of the replay and every 2 s (the FOV drift while looping).
            try
            {
                var ph = Playhead(); var nowT = Environment.TickCount64;
                if (ph < lastPh - 0.5f && loopLogs < 40) { loopLogs++; Log($"replay camera: loop #{loopLogs}: game FOV {GameFov():0.0} (first seen {origFov:0.0}, shown {fovShown:0.0}, pose {poseFov:0.0}, applied {fovApplied}), camera '{activeMode}'"); }
                else if (nowT - lastFovLog > 2000 && fovLogs < 80) { lastFovLog = nowT; fovLogs++; Log($"replay camera: FOV {GameFov():0.0} at {ph:0.00}s (camera '{activeMode}')"); }
                lastPh = ph;
            }
            catch { }
        }
        // Diagnostic: how the game itself plays a keyframed time scale (every 0.5 s while the game keyframes hold one).
        if (inReplay)
        {
            try
            {
                var nowP = Environment.TickCount64;
                if (nowP - lastTsProbe > 500 && tsProbeLogs < 160 && TimelineFx.TimelineFxMod.GameTimeScaleKey() is float kts)
                {
                    lastTsProbe = nowP; tsProbeLogs++;
                    var rec = Recorder.Instance.Raw;
                    Log($"replay camera: TS probe: key {kts:0.###}, Time.timeScale {GetTimeScale():0.###}, playhead {rec.playbackTime:0.000}s, camera '{activeMode}', "
                        + $"rec f[pxq {rec.ubr:0.###} pyj {rec.ucm:0.###} pyo {rec.ucr:0.###}] i[pxc {rec.uax} pxd {rec.uay} pxm {rec.ubj} pyp {rec.ucs}] b[pxv {rec.ubw} pxw {rec.uby} pyi {rec.ucl} pyn {rec.ucq} pyq {rec.uct} pyr {rec.ucu}]");
                }
            }
            catch (Exception e) { LogOnce("ts-probe", e); }
        }
        var keyedMode = play ? null : TimelineFx.TimelineFxMod.CameraModeKey();
        var mode = play ? (playProfile == "POV" ? "POV" : "Follow cam") : keyedMode is { } km ? (km == 1 ? "Follow cam" : km == 2 ? "POV" : "Off") : Mode;
        if (mode != activeMode)
        {
            Release();
            activeMode = mode;
            camPos = lookPt = prevFocus = null;
            povF = povU = null;
            yawInit = false;
            vel = new Vector3(0, 0, 0);
            lastFrame = -1;
        }
        if (mode == "Off") return;

        var cam = ResolveCamera();
        if (cam == 0) return;

        var frame = FrameCount;
        if (frame != lastFrame)
        {
            lastFrame = frame;
            havePose = mode == "POV" ? ComputePov(cam) : ComputeFollow(cam);
        }
        ApplyTimeScale();
        if (!havePose) return;

        var tr = Unity.Transform(cam);
        var p = poseP; var r = poseR;
        Unity.Call(tr, "set_position", (nint)(&p));
        Unity.Call(tr, "set_rotation", (nint)(&r));
        if (poseFov > 0) { var fv = poseFov; Unity.Call(cam, "set_fieldOfView", (nint)(&fv)); fovApplied = true; }
    }

    // ---------------------------------------------------------------- Time scale (keyframed with the camera)

    float tsOriginal = 1; bool tsApplied;
    float lastLoggedTs = -1; int tsLogs;

    static float GetTimeScale() => Il2Cpp.Unbox<float>(Unity.CallStatic(Unity.Class("UnityEngine.Time"), "get_timeScale"));
    static void SetTimeScale(float v) => Unity.CallStatic(Unity.Class("UnityEngine.Time"), "set_timeScale", (nint)(&v));

    /// <summary>The keyed slow motion / fast forward. 1 = normal (nothing is touched). Never overrides a paused game.</summary>
    void ApplyTimeScale()
    {
        // The game applies the "Time Scale" of its keyframes only while its own keyframe camera drives the view: with this mod's cameras
        // (POV, Follow cam, drone) nobody does it, so the keyed value of the game's keyframes is applied here (held after the last one).
        try
        {
            var want = inReplay ? TimelineFx.TimelineFxMod.GameTimeScaleKey() : null;
            var cur = GetTimeScale();
            if (want is float w && cur > 0.001f)
            {
                if (!tsApplied) tsOriginal = cur;
                w = Math.Clamp(w, 0.02f, 10f);
                if (MathF.Abs(cur - w) > 0.0005f) SetTimeScale(w);
                if (!tsApplied) { tsApplied = true; Log($"replay camera: the keyframes' time scale is applied by this mod ({w:0.##})"); }
            }
            else if (want is null) RestoreTimeScale();
        }
        catch (Exception e) { LogOnce("ts-apply", e); }
        // Diagnostic: the game's own time scale while a camera of this mod is on (changes only, at most 60 lines).
        try
        {
            var ts = GetTimeScale();
            if (MathF.Abs(ts - lastLoggedTs) > 0.005f && tsLogs < 60) { lastLoggedTs = ts; tsLogs++; Log($"replay camera: time scale {ts:0.###} (camera '{activeMode}', playhead {Playhead():0.00}s)"); }
        }
        catch { }
    }

    void RestoreTimeScale()
    {
        if (!tsApplied) return;
        tsApplied = false;
        try { if (GetTimeScale() > 0.001f) SetTimeScale(tsOriginal); } catch { }
    }

    // ---------------------------------------------------------------- Camera and rider lookup

    nint ResolveCamera()
    {
        if (camCache != 0 && !Unity.IsNull(camCache) && Unity.Call<byte>(camCache, "get_enabled") != 0) return camCache;
        camCache = 0;
        cameraRoot?.Dispose(); cameraRoot = null;
        var k = Unity.Class("UnityEngine.Camera");
        nint found = 0;
        if (followSelf != 0 && !Unity.IsNull(followSelf))
        {
            found = Unity.GetComponent(followSelf, k);
            if (found == 0) { var c = Unity.GetComponentsInChildren(followSelf, k, true); if (c.Length > 0) found = c[0]; }
        }
        if (found == 0)
        {
            var main = Unity.MainCamera;
            if (main != 0 && !Unity.IsNull(main)) found = main;
        }
        if (found == 0)
        {
            float best = float.MinValue;
            foreach (var c in Unity.FindObjects(k))
            {
                if (Unity.IsNull(c) || Unity.Call<byte>(c, "get_enabled") == 0) continue;
                if (Unity.Call(c, "get_targetTexture") != 0) continue;
                var d = Unity.Call<float>(c, "get_depth");
                if (d > best) { best = d; found = c; }
            }
        }
        if (found != 0)
        {
            cameraRoot?.Dispose(); cameraRoot = new GameRef(found);
            camCache = found;
            origFov = Unity.Call<float>(found, "get_fieldOfView");
            origNear = Unity.Call<float>(found, "get_nearClipPlane");
            fovShown = origFov;
            Log($"replay camera: using camera '{Unity.Name(found)}' (FOV {origFov:0})");
        }
        return found;
    }

    /// <summary>The transform the game's camera follows (the rider), else the rider of the spawner.</summary>
    nint RiderTransform()
    {
        try
        {
            if (followSelf != 0 && !Unity.IsNull(followSelf))
            {
                var t = new Follow(followSelf).Target.Ptr;
                if (t != 0 && !Unity.IsNull(t)) return t;
            }
        }
        catch { }
        try
        {
            if (RagdollSpawner.First() is { } sp && !sp.Rider.IsNull && !Unity.IsNull(sp.Rider.Ptr))
                return Unity.Transform(sp.Rider.Ptr);
        }
        catch { }
        return 0;
    }

    /// <summary>The head bone ("QuickRigCharacter_Head") nearest to the rider; looked for again once a second while it is missing.</summary>
    nint FindHead(nint rider)
    {
        if (headRef is { } h && !Unity.IsNull(h.Target)) return h.Target;
        if (Unity.Time < nextHeadScan) return 0;
        nextHeadScan = Unity.Time + 1f;
        var origin = Unity.Position(rider);
        nint best = 0; var bestD = float.MaxValue;
        var root = Unity.Call(Unity.Transform(rider), "get_root");
        if (root == 0 || Unity.IsNull(root)) return 0;
        foreach (var t in Unity.GetComponentsInChildren(root, Unity.Class("UnityEngine.Transform"), true))
        {
            if (Unity.IsNull(t) || Unity.Name(t) != "QuickRigCharacter_Head") continue;
            var d = (Unity.Position(t) - origin).Magnitude;
            if (d < bestD) { bestD = d; best = t; }
        }
        if (best == 0) return 0;
        headRef?.Dispose(); headRef = new GameRef(best);
        calibs.Clear(); calibActive = -1; inSwitch = false; shoulder = 0;
        if (!rigSaved) LoadRig();
        // The rig is always the same: learned once while riding (kept on disk), never again by itself. A replay never learns anything: it uses the same axes as the game.
        if (rigSaved) ApplyRig(); else if (inReplay) { ApplyDefaultRig(); } else Recalibrate();
        FindBones(best);
        lampRef?.Dispose(); lampRef = null;
        foreach (var child in Unity.GetComponentsInChildren(best, Unity.Class("UnityEngine.Transform"), true))
            if (!Unity.IsNull(child) && Unity.Name(child) == "Lamp") { lampRef = new GameRef(child); break; }
        Log($"replay camera: head found ({bestD:0.0} m from the rider{(lampRef is null ? ", no headlamp" : ", headlamp found")})");
        return best;
    }

    // ---------------------------------------------------------------- Math

    static Vector3 Add(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static Vector3 Sub(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    static Vector3 Mul(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    static Vector3 Lerp(Vector3 a, Vector3 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static Vector3 Norm(Vector3 v, Vector3 fallback) { var m = v.Magnitude; return m < 1e-5f ? fallback : Mul(v, 1 / m); }
    static float Wrap(float a) { while (a > 180) a -= 360; while (a < -180) a += 360; return a; }

    static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        var cross = Cross(u, v);
        return Add(Add(Mul(u, 2 * Dot(u, v)), Mul(v, q.W * q.W - Dot(u, u))), Mul(cross, 2 * q.W));
    }

    static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        var z = Norm(forward, new Vector3(0, 0, 1));
        var x = Norm(Cross(up, z), new Vector3(1, 0, 0));
        var y = Cross(z, x);
        float m00 = x.X, m01 = y.X, m02 = z.X, m10 = x.Y, m11 = y.Y, m12 = z.Y, m20 = x.Z, m21 = y.Z, m22 = z.Z;
        var tr = m00 + m11 + m22;
        Quaternion q;
        if (tr > 0) { var s = MathF.Sqrt(tr + 1) * 2; q = new((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s); }
        else if (m00 > m11 && m00 > m22) { var s = MathF.Sqrt(1 + m00 - m11 - m22) * 2; q = new(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s); }
        else if (m11 > m22) { var s = MathF.Sqrt(1 + m11 - m00 - m22) * 2; q = new((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s); }
        else { var s = MathF.Sqrt(1 + m22 - m00 - m11) * 2; q = new((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s); }
        return q;
    }

    /// <summary>Smoothed velocity of a point, ignoring jumps (scrubbing the timeline, cuts).</summary>
    void TrackVelocity(Vector3 focus, float dt)
    {
        if (prevFocus is { } pf && dt > 1e-4f)
        {
            var raw = Mul(Sub(focus, pf), 1 / dt);
            if (raw.Magnitude < 90)
            {
                vel = Lerp(vel, raw, 1 - MathF.Exp(-3 * dt));
                // Free fall (vertical acceleration near -g) = in the air; on the ground it stays small.
                var ay = (raw.Y - rawVyPrev) / dt;
                airScore += ((ay < -5f ? 1f : 0f) - airScore) * (1 - MathF.Exp(-6 * dt));
                rawVyPrev = raw.Y;
            }
            else
            {
                vel = new Vector3(0, 0, 0);
                airScore = 0; rawVyPrev = 0;
            }
        }
        prevFocus = focus;
    }

    // ---------------------------------------------------------------- Follow cam

    float sweepDeg;

    bool ComputeFollow(nint cam)
    {
        var rider = RiderTransform();
        if (rider == 0) return false;
        // Game time, not wall-clock time: while exporting a replay the game renders frames one by one (fixed step), and slow-motion changes the step too.
        var gameDt = Unity.DeltaTime;
        var paused = gameDt < 1e-4f;
        var dt = Math.Clamp(paused ? RealDelta : gameDt, 0.001f, 0.1f);
        var focus = Add(Unity.Position(rider), new Vector3(0, K("Aim", CamAim), 0));
        if (!paused) TrackVelocity(focus, dt);
        var flat = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
        var kmh = vel.Magnitude * 3.6f;
        var level = Math.Clamp(kmh / 150f, 0, 1);

        var focusAmt = Math.Clamp(K("Focus", CamFocus) / 100f, 0, 1);
        sweepDeg += CamSweep * dt;
        var offset = K("Angle", CamAngle) + sweepDeg;
        var dist = K("Distance", CamDistance) * (1 + CamPull / 100f * level * 0.6f);

        if (camPos is null) yawDeg = Wrap(offset);
        if (flat > 1.5f)
        {
            var heading = MathF.Atan2(vel.X, vel.Z) * 180 / MathF.PI;
            yawDeg += Wrap(heading + offset - yawDeg) * (1 - MathF.Exp(-CamReactivity * 0.7f * dt));
        }
        else
            yawDeg += Wrap(lastHeading + offset - yawDeg) * (1 - MathF.Exp(-CamReactivity * 0.7f * dt));   // stopped: keep the shot, only the orbit moves
        if (flat > 1.5f) lastHeading = MathF.Atan2(vel.X, vel.Z) * 180 / MathF.PI;

        var yr = yawDeg * MathF.PI / 180;
        var desired = new Vector3(focus.X + MathF.Sin(yr) * dist, focus.Y + K("Height", CamHeight), focus.Z + MathF.Cos(yr) * dist);

        var aim = focus;
        if (flat > 1.0f && CamLead > 0)
        {
            var lead = Math.Clamp(flat * 0.18f, 0, 5) * (CamLead / 100f) * (1 - 0.7f * focusAmt);
            aim = new Vector3(focus.X + vel.X / flat * lead, focus.Y, focus.Z + vel.Z / flat * lead);
        }

        if (camPos is not { } cp || Sub(desired, cp).Magnitude > 40)
        {
            camPos = desired;
            lookPt = aim;
        }
        else
        {
            camPos = Lerp(cp, desired, 1 - MathF.Exp(-CamReactivity * dt));
            lookPt = Lerp(lookPt ?? aim, aim, 1 - MathF.Exp(-(8 + 24 * focusAmt) * dt));
        }

        var pos = camPos.Value;
        var look = lookPt!.Value;
        var amp = CamShake / 100f * (1 + Extra("x_speedshake") / 100f * level * 2.5f);
        if (amp > 0)
        {
            swayClock += dt;
            var t = swayClock;
            var sx = (MathF.Sin(t * 1.3f) + 0.6f * MathF.Sin(t * 3.1f + 1.7f)) * 0.10f * amp;
            var sy = (MathF.Sin(t * 1.7f + 0.5f) + 0.5f * MathF.Sin(t * 4.3f)) * 0.08f * amp;
            look = new Vector3(look.X + sx, look.Y + sy, look.Z + sx * 0.5f);
            pos = new Vector3(pos.X + sy * 0.4f, pos.Y + sx * 0.3f, pos.Z);
        }
        if (PlayActive || inReplay && replayExtras != null)
        {
            // Drone: it hovers (slow drift and bob, a faint motor buzz), always a little behind the action.
            var hov = Extra("x_hover") / 100f;
            if (hov > 0)
            {
                droneClock += dt;
                var d = droneClock;
                pos = new Vector3(pos.X + (MathF.Sin(d * 0.53f) * 0.45f + MathF.Sin(d * 1.31f + 1f) * 0.15f) * hov,
                                  pos.Y + (MathF.Sin(d * 0.91f) * 0.35f + MathF.Sin(d * 0.37f + 2f) * 0.3f) * hov + MathF.Sin(d * 41f) * 0.004f * hov,
                                  pos.Z + (MathF.Sin(d * 0.47f + 2f) * 0.45f + MathF.Sin(d * 1.17f) * 0.15f) * hov);
            }
            // Relief: the heading rate (how fast the rider turns) banks the picture into the turn.
            if (flat > 3f)
            {
                var hd = MathF.Atan2(vel.X, vel.Z) * 180 / MathF.PI;
                if (haveHd && dt > 1e-4f) bankSm += (Math.Clamp(Wrap(hd - prevHd) / dt, -240f, 240f) - bankSm) * (1 - MathF.Exp(-4 * dt));
                prevHd = hd; haveHd = true;
            }
            else { bankSm *= MathF.Exp(-3 * dt); haveHd = false; }
        }
        var fwd = Norm(Sub(look, pos), new Vector3(0, 0, 1));
        // Keep the rider in the picture: if he drifts too far from the centre, the aim is pulled back towards him.
        if (focusAmt > 0.01f)
        {
            var toR = Norm(Sub(focus, pos), fwd);
            var ang = MathF.Acos(Math.Clamp(Dot(fwd, toR), -1, 1)) * 180 / MathF.PI;
            var vfov = fovShown > 5 ? fovShown : (origFov > 0 ? origFov : 60);
            var limit = Math.Max(vfov * 0.5f * (0.55f - 0.5f * focusAmt), 0.5f);
            if (ang > limit) fwd = Norm(Lerp(toR, fwd, limit / ang), fwd);
        }
        var up = new Vector3(0, 1, 0);
        var rollDeg = K("Roll", CamRoll) - Extra("x_bank") * Math.Clamp(bankSm / 90f, -1f, 1f);
        if (MathF.Abs(rollDeg) > 0.01f)
        {
            var a = rollDeg * MathF.PI / 180;
            up = Add(Mul(up, MathF.Cos(a)), Mul(Cross(fwd, up), MathF.Sin(a)));
        }
        poseP = pos;
        poseR = LookRotation(fwd, up);

        // In the replay the FOV is the game's own (its FOV keyframes): never overridden. Only the play cameras set one.
        if (!PlayActive) { fovShown = GameFov(); poseFov = 0; return true; }
        var baseFov = Math.Clamp(origFov > 0 ? origFov : 60, 20, 150);
        var kick = Extra("x_fovkick");
        if (PlayActive && kick > 0)
        {
            // Speed makes the picture wider: a stronger feeling of speed and relief.
            var want = Math.Clamp(baseFov + kick * level, 20, 150);
            fovShown = fovShown < 5 ? want : fovShown + (want - fovShown) * (1 - MathF.Exp(-4 * dt));
        }
        else fovShown = baseFov;
        poseFov = fovShown;
        return true;
    }

    float lastHeading, lastGoYaw, droneClock, bankSm, prevHd;
    bool haveHd;
    float airScore, rawVyPrev, shoulderSide = 1, shiftNow, yawView, pitchView;
    bool yawInit;

    static Vector3 Dir(float yawDeg, float pitchDeg)
    {
        var y = yawDeg * MathF.PI / 180; var p = pitchDeg * MathF.PI / 180;
        return new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
    }

    // ---------------------------------------------------------------- POV

    // Bone axes are learned while riding (the rig's axes are not known in advance): one learner per bone.
    static readonly Vector3[] Axes = { new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) };

    sealed class AxisLearner
    {
        readonly float[] sumUp = new float[3], sumFwd = new float[3];
        int n;
        public bool Done;
        public Vector3 Fwd = new(0, 0, 1), Up = new(0, 1, 0);

        public void Reset()
        {
            Array.Clear(sumUp); Array.Clear(sumFwd);
            n = 0; Done = false;
            Fwd = new Vector3(0, 0, 1); Up = new Vector3(0, 1, 0);
        }

        /// <summary>While the rider skis fast, the axis that points up and the axis that points along the travel are learned from the bone's rotation.</summary>
        public void Update(Quaternion q, Vector3 vel, float flat, Action<string> log, string label)
        {
            if (Done || flat < 4f) return;
            var tx = vel.X / flat; var tz = vel.Z / flat;
            for (var i = 0; i < 3; i++)
            {
                var a = Rotate(q, Axes[i]);
                sumUp[i] += a.Y;
                var af = MathF.Sqrt(a.X * a.X + a.Z * a.Z);
                if (af > 0.3f) sumFwd[i] += (a.X * tx + a.Z * tz) / af;
            }
            n++;
            if (n < 45) return;

            var k = 0;
            for (var i = 1; i < 3; i++) if (MathF.Abs(sumUp[i]) > MathF.Abs(sumUp[k])) k = i;
            var j = -1;
            for (var i = 0; i < 3; i++)
                if (i != k && (j < 0 || MathF.Abs(sumFwd[i]) > MathF.Abs(sumFwd[j]))) j = i;
            var upOk = MathF.Abs(sumUp[k]) / n > 0.5f;
            var fwdOk = MathF.Abs(sumFwd[j]) / n > 0.3f;
            if (!upOk || !fwdOk)
            {
                if (n < 200) return;
                log($"replay camera: {label} axes unclear (up {sumUp[k] / n:0.00}, forward {sumFwd[j] / n:0.00}), using Z forward / Y up");
                Done = true;
                return;
            }
            Up = Mul(Axes[k], MathF.Sign(sumUp[k]));
            Fwd = Mul(Axes[j], MathF.Sign(sumFwd[j]));
            Done = true;
            log($"replay camera: {label} axes learned: up = {AxisName(Up)}, forward = {AxisName(Fwd)}");
        }
    }

    readonly AxisLearner headL = new(), chestL = new(), hipsL = new();
    GameRef? chestRef, hipsRef, lShRef, rShRef;
    float shSum, shSign = 1; int shN; bool shDone;
    float shoulder;
    bool inSwitch;

    // Each press learns the body axes again and stores them as a calibration point at the playhead,
    // like a keyframe: the POV uses the latest calibration at or before the current replay time.
    sealed record Calib(float T, Vector3[] F, Vector3[] U, float Sign);
    readonly List<Calib> calibs = new();
    bool calibPending; float calibT; int calibFrames, calibActive = -1;

    static float Playhead() { try { return Recorder.Instance.Raw.playbackTime; } catch { return 0; } }

    // The body axes of the character never change: once learned they are kept (until "Learn the body axes" is pressed) for every replay and ride.
    static bool rigSaved; static Vector3[] rigF = new Vector3[3], rigU = new Vector3[3]; static float rigSign = 1;
    void ApplyRig()
    {
        var ls = new[] { headL, chestL, hipsL };
        for (var i = 0; i < 3; i++) { ls[i].Fwd = rigF[i]; ls[i].Up = rigU[i]; ls[i].Done = true; }
        shSign = rigSign; shDone = true; calibPending = false;
    }
    static string RigFile => Path.Combine(Bootstrap.ModsDir, "POV rig.json");

    void ApplyDefaultRig()
    {
        var ls = new[] { headL, chestL, hipsL };
        foreach (var l in ls) { l.Fwd = new Vector3(0, 0, 1); l.Up = new Vector3(0, 1, 0); l.Done = true; }
        shSign = 1; shDone = true; calibPending = false;
        Log("replay camera: no learned body axes yet, using the default ones (Z forward, Y up)");
    }

    void LoadRig()
    {
        try
        {
            if (!File.Exists(RigFile) || JsonNode.Parse(File.ReadAllText(RigFile)) is not JsonObject o) return;
            var f = o["F"]!.AsArray().Select(n => (float)n!.GetValue<double>()).ToArray();
            var u = o["U"]!.AsArray().Select(n => (float)n!.GetValue<double>()).ToArray();
            if (f.Length != 9 || u.Length != 9) return;
            for (var i = 0; i < 3; i++) { rigF[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]); rigU[i] = new Vector3(u[i * 3], u[i * 3 + 1], u[i * 3 + 2]); }
            rigSign = (float)o["S"]!.GetValue<double>(); rigSaved = true;
            Log("replay camera: body axes loaded from the saved file");
        }
        catch (Exception e) { Log("replay camera: could not read the saved body axes: " + e.Message); }
    }

    static void WriteRig()
    {
        try
        {
            var o = new JsonObject
            {
                ["F"] = new JsonArray(rigF.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(x => (JsonNode)JsonValue.Create((double)x)!).ToArray()),
                ["U"] = new JsonArray(rigU.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(x => (JsonNode)JsonValue.Create((double)x)!).ToArray()),
                ["S"] = (double)rigSign,
            };
            File.WriteAllText(RigFile, o.ToJsonString());
        }
        catch { }
    }

    void SaveRig()
    {
        if (rigSaved || calibPending || calibs.Count > 0) return;
        if ((chestRef != null && !chestL.Done) || (hipsRef != null && !hipsL.Done) || (lShRef != null && !shDone)) return;
        var ls = new[] { headL, chestL, hipsL };
        for (var i = 0; i < 3; i++) { rigF[i] = ls[i].Fwd; rigU[i] = ls[i].Up; }
        rigSign = shSign; rigSaved = true;
        WriteRig();
    }

    public void Recalibrate()
    {
        rigSaved = false;
        headL.Reset(); chestL.Reset(); hipsL.Reset();
        shSum = 0; shN = 0; shDone = false; shSign = 1;
        calibPending = true; calibT = Playhead(); calibFrames = 0;
        Log($"replay camera: body axes will be learned at {calibT:0.00}s");
    }

    void UpdateCalibration()
    {
        var ls = new[] { headL, chestL, hipsL };
        if (calibPending)
        {
            calibFrames++;
            var ready = (chestRef == null || chestL.Done) && (hipsRef == null || hipsL.Done) && (shDone || lShRef == null);
            if (ready || calibFrames > 400)
            {
                var c = new Calib(calibT, ls.Select(l => l.Fwd).ToArray(), ls.Select(l => l.Up).ToArray(), shSign);
                calibs.RemoveAll(x => MathF.Abs(x.T - calibT) < 0.05f);
                calibs.Add(c); calibs.Sort((a, b) => a.T.CompareTo(b.T));
                calibPending = false; calibActive = calibs.IndexOf(c);
                Log($"replay camera: calibration stored at {calibT:0.00}s ({calibs.Count} total)");
            }
            return;
        }
        if (calibs.Count == 0) return;
        var t = Playhead(); var idx = 0;
        for (var i = 0; i < calibs.Count; i++) if (calibs[i].T <= t + 0.001f) idx = i;
        if (idx == calibActive) return;
        calibActive = idx;
        var k = calibs[idx];
        for (var i = 0; i < 3; i++) { ls[i].Fwd = k.F[i]; ls[i].Up = k.U[i]; ls[i].Done = true; }
        shSign = k.Sign; shDone = true;
    }

    static Vector3 LocalAxis(string name) => name switch
    {
        "Z" => new Vector3(0, 0, 1), "-Z" => new Vector3(0, 0, -1),
        "X" => new Vector3(1, 0, 0), "-X" => new Vector3(-1, 0, 0),
        "Y" => new Vector3(0, 1, 0), "-Y" => new Vector3(0, -1, 0),
        _ => new Vector3(0, 0, 1),
    };

    static Vector3 UpFor(string axis) => axis is "Y" or "-Y" ? new Vector3(0, 0, -1) : new Vector3(0, 1, 0);

    /// <summary>Finds the torso bones of the rider that owns the head: shoulders, chest/spine, hips. Their names are logged once.</summary>
    void FindBones(nint head)
    {
        chestRef?.Dispose(); chestRef = null;
        hipsRef?.Dispose(); hipsRef = null;
        lShRef?.Dispose(); lShRef = null;
        rShRef?.Dispose(); rShRef = null;
        var root = Unity.Call(head, "get_root");
        if (root == 0 || Unity.IsNull(root)) return;
        var names = new List<string>();
        nint chest = 0, hips = 0, ls = 0, rs = 0;
        var chestY = float.MinValue;
        foreach (var t in Unity.GetComponentsInChildren(root, Unity.Class("UnityEngine.Transform"), true))
        {
            if (Unity.IsNull(t)) continue;
            var n = Unity.Name(t).ToLowerInvariant();
            if (n.StartsWith("quickrigcharacter_")) n = n.Substring(18);
            names.Add(n);
            if (n.Contains("head") || n.Contains("neck") || n.Contains("lamp")) continue;
            if (n.Contains("shoulder") || n.Contains("clavicle"))
            {
                var left = n.Contains("left") || n.EndsWith("_l") || n.EndsWith(".l") || n.StartsWith("l_");
                var right = n.Contains("right") || n.EndsWith("_r") || n.EndsWith(".r") || n.StartsWith("r_");
                if (left && ls == 0) ls = t;
                else if (right && rs == 0) rs = t;
            }
            else if (n.Contains("spine") || n.Contains("chest"))
            {
                var y = Unity.Position(t).Y;
                if (y > chestY) { chestY = y; chest = t; }
            }
            else if ((n.Contains("hips") || n.Contains("pelvis")) && hips == 0) hips = t;
        }
        if (ls != 0 && rs != 0) { lShRef = new GameRef(ls); rShRef = new GameRef(rs); }
        if (chest != 0) chestRef = new GameRef(chest);
        if (hips != 0) hipsRef = new GameRef(hips);
        var all = string.Join(", ", names);
        Log($"replay camera: rig bones ({names.Count}): {(all.Length > 1400 ? all.Substring(0, 1400) + "…" : all)}");
        Log($"replay camera: torso bones: shoulders {(lShRef != null ? "yes" : "no")}, chest {(chestRef != null ? "yes" : "no")}, hips {(hipsRef != null ? "yes" : "no")}");
    }

    /// <summary>Forward / up of the body part the view is based on (shoulders, chest, hips or head). Lean = the forward vector also carries the body's forward lean.</summary>
    (Vector3 F, Vector3 U, bool Lean) BodyFrame(Vector3 headF, Vector3 headU, float flat)
    {
        var want = PovBase;
        if (want == "Shoulders" && lShRef is { } l && rShRef is { } r && !Unity.IsNull(l.Target) && !Unity.IsNull(r.Target))
        {
            var rv = Sub(Unity.Position(r.Target), Unity.Position(l.Target));
            var f = Cross(rv, new Vector3(0, 1, 0));   // horizontal, perpendicular to the shoulder line
            var fm = MathF.Sqrt(f.X * f.X + f.Z * f.Z);
            if (fm > 0.05f)
            {
                var fn = new Vector3(f.X / fm, 0, f.Z / fm);
                if (!shDone && flat > 4f)
                {
                    shSum += (fn.X * vel.X + fn.Z * vel.Z) / flat;
                    shN++;
                    if (shN >= 45)
                    {
                        shDone = true;
                        if (MathF.Abs(shSum / shN) > 0.3f) shSign = MathF.Sign(shSum);
                        Log($"replay camera: shoulder line learned (sign {shSign:+0;-0}, score {shSum / shN:0.00})");
                    }
                }
                return (Mul(fn, shSign), new Vector3(0, 1, 0), false);
            }
        }
        if ((want == "Shoulders" || want == "Chest") && chestRef is { } c && !Unity.IsNull(c.Target))
            return Learned(c.Target, chestL, flat, "chest");
        if ((want == "Hips") && hipsRef is { } h && !Unity.IsNull(h.Target))
            return Learned(h.Target, hipsL, flat, "hips");
        if (want != "Head" && want != "Hips" && hipsRef is { } h2 && !Unity.IsNull(h2.Target))
            return Learned(h2.Target, hipsL, flat, "hips");
        return (headF, headU, true);
    }

    (Vector3 F, Vector3 U, bool Lean) Learned(nint bone, AxisLearner learner, float flat, string label)
    {
        var q = Unity.Call<Quaternion>(bone, "get_rotation");
        learner.Update(q, vel, flat, Log, label);
        return (Rotate(q, learner.Fwd), Rotate(q, learner.Up), true);
    }

    static string AxisName(Vector3 v)
    {
        var n = v.X > 0.5f ? "X" : v.X < -0.5f ? "-X" : v.Y > 0.5f ? "Y" : v.Y < -0.5f ? "-Y" : v.Z > 0.5f ? "Z" : "-Z";
        return n;
    }

    static Vector3 RotY(Vector3 v, float deg)
    {
        var a = deg * MathF.PI / 180;
        var c = MathF.Cos(a); var s = MathF.Sin(a);
        return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    /// <summary>Per-frame blend factor of the stabilizer: a time constant of 0.6 s x stab squared (100 % = a 0.6 s lag, so the view still follows the body).</summary>
    float StabK(float dt) { var st = Math.Clamp(PovStab / 100f, 0, 1); var tau = MathF.Max(0.6f * st * st, 0.01f); return 1 - MathF.Exp(-dt / tau); }

    bool ComputePov(nint cam)
    {
        var rider = RiderTransform();
        if (rider == 0) return false;
        var head = FindHead(rider);
        if (head == 0) { if (logged.Add("nohead")) Log("replay camera: head bone not found yet, POV waits"); return false; }

        // Game time, not wall-clock time: while exporting a replay the game renders frames one by one (fixed step), and slow-motion changes the step too.
        var gameDt = Unity.DeltaTime;
        var paused = gameDt < 1e-4f;
        var dt = Math.Clamp(paused ? RealDelta : gameDt, 0.001f, 0.1f);
        UpdateCalibration();
        var hp = Unity.Position(head);
        var hr = Unity.Call<Quaternion>(head, "get_rotation");
        if (!paused) TrackVelocity(hp, dt);
        var flat = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
        var kmh = vel.Magnitude * 3.6f;
        var speed01 = Math.Clamp(kmh / 150f, 0, 1);
        headL.Update(hr, vel, flat, Log, "head");
        SaveRig();

        // The head's own forward / up, in the world (used by the Head modes).
        Vector3 localF, localU;
        if (PovAxis != "Auto" || PlayActive || true) { var ax = PovAxis == "Auto" ? "Z" : PovAxis; localF = LocalAxis(ax); localU = headL.Done && PovAxis != "Auto" && !PlayActive ? headL.Up : UpFor(ax); }   // fixed head axes (Z forward, Y up) so the replay view never tilts
        else if (headL.Done) { localF = headL.Fwd; localU = headL.Up; }
        else if (lampRef is { } lr0 && !Unity.IsNull(lr0.Target)) { localF = new Vector3(0, 0, 1); localU = new Vector3(0, 1, 0); hr = Unity.Call<Quaternion>(Unity.Transform(lr0.Target), "get_rotation"); }
        else { localF = new Vector3(0, 0, 1); localU = new Vector3(0, 1, 0); }
        var headF = Rotate(hr, localF);
        var headU = Rotate(hr, localU);

        // The body (shoulders / chest / hips): where the torso faces, independent of the head nodding.
        var (bodyF, bodyU, lean) = BodyFrame(headF, headU, flat);

        Vector3 fwd, up;
        var lock_ = PovLock;
        var worldUp = new Vector3(0, 1, 0);
        var shiftTarget = 0f;
        var bodyRight = new Vector3(1, 0, 0);

        // Aims by yaw / pitch (not by a bone's own pitch), so a tucked body does not point the camera at the ground.
        float yawGo = flat > 1.5f ? MathF.Atan2(vel.X, vel.Z) * 180 / MathF.PI : lastGoYaw;
        if (flat > 1.5f) lastGoYaw = yawGo;
        var slopePitch = vel.Magnitude > 2.5f ? MathF.Asin(Math.Clamp(vel.Y / vel.Magnitude, -1, 1)) * 180 / MathF.PI : 0f;
        var leanDeg = lean ? MathF.Asin(Math.Clamp(bodyF.Y, -1, 1)) * 180 / MathF.PI : 0f;
        var bfm = MathF.Sqrt(bodyF.X * bodyF.X + bodyF.Z * bodyF.Z);
        var yawBody = bfm > 0.25f ? MathF.Atan2(bodyF.X, bodyF.Z) * 180 / MathF.PI : yawGo;
        var maxP = PovMaxPitch;
        // Horizon in the frame: 50 = centred, lower = the horizon sits higher in the picture (the camera looks a little below it).
        var vf = Math.Clamp(fovShown < 5 ? K("PovFov", PovFov) : fovShown, 30f, 130f) * MathF.PI / 180;
        var horizonOffset = -MathF.Atan((0.5f - Math.Clamp(K("PovHorizon", PovHorizon) / 100f, 0.1f, 0.9f)) * 2 * MathF.Tan(vf / 2)) * 180 / MathF.PI;

        if (lock_ == "Travel direction")
        {
            var pitch = Math.Clamp(slopePitch * PovSlope / 100f + horizonOffset, -maxP, maxP);
            fwd = Dir(yawGo, pitch);
            up = worldUp;
        }
        else if (lock_ == "Real (look where you go)")
        {
            var airborne = airScore > 0.5f;
            var offGo = Wrap(yawGo - yawBody);   // how far the torso is from the direction of travel
            // Switch = the torso faces away from the travel. Decided on the ground only: spins in the air must not flip it.
            if (!airborne)
            {
                if (flat > 3f && bfm > 0.5f)
                {
                    var d = (bodyF.X * vel.X + bodyF.Z * vel.Z) / (bfm * flat);
                    if (!inSwitch && d < -0.25f) inSwitch = true;
                    else if (inSwitch && d > 0.1f) inSwitch = false;
                }
                else if (flat < 1.5f) inSwitch = false;
            }
            var target = inSwitch ? PovShoulder / 100f : 0f;
            shoulder += (target - shoulder) * (1 - MathF.Exp(-5 * dt));

            // Wanted heading: the shoulders' heading. In switch the head turns over the shoulder towards the travel, within the neck limit.
            var yawT = yawBody + Math.Clamp(offGo, -PovNeckTurn, PovNeckTurn) * shoulder;
            yawT = yawGo + Wrap(yawT - yawGo) * (PovHeadYaw / 100f);
            yawT += Wrap(yawGo - yawT) * (PovAirStab / 100f * airScore);   // in the air: mostly along the travel

            var pitchT = slopePitch * PovSlope / 100f + leanDeg * PovHeadPitch / 100f + horizonOffset;
            pitchT = Math.Clamp(pitchT, -maxP, maxP);

            // Smoothing and a limit on the turning speed, so the view never whips around.
            if (!yawInit) { yawView = yawT; pitchView = pitchT; yawInit = true; }
            var sk = 1 - MathF.Pow(Math.Clamp(PovSmooth / 100f, 0, 0.95f), dt * 30);
            if (PovStab > 0) sk = MathF.Min(sk, StabK(dt));   // stabilizer: slows the shakes, but the view still turns with the body (at most ~0.6 s of lag at 100 %)
            var yawRate = 900f;   // the view must follow spins and flips of the body
            var dy = Math.Clamp(Wrap(yawT - yawView) * sk, -yawRate * dt, yawRate * dt);
            yawView = Wrap(yawView + dy);
            var dp = Math.Clamp((pitchT - pitchView) * sk, -90f * dt, 90f * dt);
            pitchView += dp;
            fwd = Dir(yawView, pitchView);
            up = worldUp;   // the horizon stays level

            // Switch on the ground: the eye leans towards the shoulder the rider looks over.
            if (inSwitch && MathF.Abs(offGo) < 160f) shoulderSide = offGo > 0 ? 1 : -1;
            var yb = yawBody * MathF.PI / 180;
            bodyRight = new Vector3(MathF.Cos(yb), 0, -MathF.Sin(yb));
            shiftTarget = inSwitch ? K("PovShift", PovShift) * shoulderSide * shoulder * (1 - airScore) : 0f;
        }
        else if (lock_ == "Head (level horizon)")
        {
            fwd = headF;
            up = Norm(Lerp(headU, worldUp, Math.Clamp(PovLevel / 100f, 0, 1)), worldUp);
        }
        else
        {
            fwd = headF; up = headU;   // Head (locked)
        }

        // Look up / down.
        if (MathF.Abs(K("PovPitch", PovPitch)) > 0.01f)
        {
            var right = Norm(Cross(up, fwd), new Vector3(1, 0, 0));
            var a = K("PovPitch", PovPitch) * MathF.PI / 180;
            fwd = Add(Mul(fwd, MathF.Cos(a)), Mul(up, MathF.Sin(a)));
            up = Norm(Cross(fwd, right), up);
        }

        // Softening: 0 = locked to the head.
        var stab = Math.Clamp(PovStab / 100f, 0, 1);
        var smooth = Math.Clamp(PovSmooth / 100f, 0, 0.95f);
        if (stab > 0) up = Norm(Lerp(up, worldUp, stab * 0.85f), up);   // gimbal: the horizon stays level
        if (lock_ != "Real (look where you go)" && povF is { } pf && povU is { } pu && (smooth > 0 || stab > 0) && Dot(pf, fwd) > -0.5f)
        {
            var k = 1 - MathF.Pow(smooth, dt * 30);
            if (stab > 0) k = MathF.Min(k, StabK(dt));
            fwd = Norm(Lerp(pf, fwd, k), fwd);
            up = Norm(Lerp(pu, up, k), up);
        }
        povF = fwd; povU = up;

        // Body movement of the eye: small bob and sway, stronger with speed.
        shiftNow += (shiftTarget - shiftNow) * (1 - MathF.Exp(-6 * dt));
        var pos = Add(Add(Add(hp, Mul(fwd, PovForward)), Mul(up, PovUp)), Mul(bodyRight, shiftNow));
        var look = fwd; var upv = up;
        var amp = PovShake / 100f * (0.5f + 0.5f * speed01) * (1 - 0.8f * stab);
        if (amp > 0.001f)
        {
            swayClock += dt * (1.6f + 2.5f * speed01);
            var t = swayClock;
            var right = Norm(Cross(up, fwd), new Vector3(1, 0, 0));
            var bob = MathF.Sin(t * 5.2f) * 0.06f * amp + MathF.Sin(t * 1.7f) * 0.05f * amp;
            pos = Add(pos, Mul(up, bob));
            var yawOff = (MathF.Sin(t * 1.9f + 1f) + 0.5f * MathF.Sin(t * 4.1f)) * 0.06f * amp;
            var pitchOff = (MathF.Sin(t * 2.3f) + 0.4f * MathF.Sin(t * 5.2f + 2f)) * 0.05f * amp;
            look = Norm(Add(Add(fwd, Mul(right, yawOff)), Mul(up, pitchOff)), fwd);
            upv = Norm(Add(up, Mul(right, MathF.Sin(t * 1.3f) * 0.06f * amp)), up);
        }
        poseP = pos;
        poseR = LookRotation(look, upv);

        var dtf = dt;
        if (!PlayActive) { fovShown = GameFov(); poseFov = 0; }   // replay: the game's own FOV keyframes decide
        else
        {
            var wanted = K("PovFov", PovFov) + PovBoost * speed01 + (PovFisheye ? FisheyeFov : 0);
            wanted = Math.Clamp(wanted, 30, 150);
            if (fovShown < 5) fovShown = wanted;
            fovShown += (wanted - fovShown) * (1 - MathF.Exp(-6 * dtf));
            poseFov = fovShown;
        }

        var near = 0.12f;
        Unity.Call(cam, "set_nearClipPlane", (nint)(&near));
        if (PovHideHead) HideHead(head); else ShowHead();
        return true;
    }

    // ---------------------------------------------------------------- Hide / restore

    void HideHead(nint head)
    {
        if (hidden.Count > 0 && hidden.All(h => !Unity.IsNull(h.R.Target))) { ReapplyHidden(); return; }
        ShowHead();
        var off = (byte)0;
        foreach (var r in Unity.GetComponentsInChildren(head, Unity.Class("UnityEngine.Renderer"), true))
        {
            if (Unity.IsNull(r)) continue;
            hidden.Add((new GameRef(r), Unity.Call<byte>(r, "get_enabled") != 0));
            Unity.Call(r, "set_enabled", (nint)(&off));
        }
        if (hidden.Count != lastHiddenLog) { lastHiddenLog = hidden.Count; Log($"replay camera: {hidden.Count} head renderer(s) hidden"); }
    }
    int lastHiddenLog = -1;

    void ReapplyHidden()
    {
        var off = (byte)0;
        foreach (var h in hidden)
            if (!Unity.IsNull(h.R.Target)) Unity.Call(h.R.Target, "set_enabled", (nint)(&off));
    }

    void ShowHead()
    {
        foreach (var h in hidden)
        {
            try { if (!Unity.IsNull(h.R.Target)) { var was = (byte)(h.Was ? 1 : 0); Unity.Call(h.R.Target, "set_enabled", (nint)(&was)); } } catch { }
            try { h.R.Dispose(); } catch { }
        }
        hidden.Clear();
    }

    /// <summary>Gives everything back: head, near plane, FOV. The game's camera takes over again by itself.</summary>
    void Release()
    {
        try { ShowHead(); } catch { }
        try { RestoreTimeScale(); } catch { }
        try
        {
            if (camCache != 0 && !Unity.IsNull(camCache))
            {
                if (origNear > 0) { var n = origNear; Unity.Call(camCache, "set_nearClipPlane", (nint)(&n)); }
                if (origFov > 0 && fovApplied) { var f = origFov; Unity.Call(camCache, "set_fieldOfView", (nint)(&f)); }
            }
        }
        catch { }
        fovApplied = false;
        activeMode = "Off";
        yawInit = false;
        havePose = false;
        camPos = lookPt = prevFocus = null;
        povF = povU = null;
    }
}
