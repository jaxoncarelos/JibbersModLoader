using System.Diagnostics;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Game.Event;

namespace HankModPack;

/// <summary>
/// Free look while playing, with a gamepad:
///  - Hold D-pad down (a short tap still changes the stance as usual) and move the right stick: the camera orbits around the rider,
///    like the game's orbit camera in the replay. L1 / R1 zoom out / in. Release to let the game's camera come back behind you.
///  - Click the right stick (R3): rear view, like in a car game. Release to look ahead again.
/// While the D-pad is held the player's own controls are switched off, so the stick does not move the skier.
/// </summary>
public sealed unsafe partial class LookAroundMod : PackPart
{
    protected override string PartId => "free_look";
    protected override string? LegacyId => "look_around";
    public override string Name => "Hank Mod Pack: Free Look";
    public override string Author => "Hank";
    public override string Description => "Hold D-pad down while playing: a menu shows what each button does (camera choice, spawn menu, Fun / Overlay on-off, look behind) and the right stick orbits the camera. A short tap still changes the stance.";

    // ---------------------------------------------------------------- Settings

    [Slider("Hold time before the free look starts (seconds)", 0.25f, 0.1f, 1f, 0.05f, Key = "hold", Section = "Free look",
        Tooltip = "A press shorter than this is a tap: it changes the stance as usual. Holding D-pad down longer starts free look. Stance changes remain controlled by the game.")]
    public float HoldTime { get; private set; } = 0.25f;

    [Slider("Look speed (degrees / second)", 140, 40, 360, 10, Key = "speed", Section = "Free look")]
    public float LookSpeed { get; private set; } = 140;

    [Toggle("Invert vertical", false, Key = "invert_y", Section = "Free look")]
    public bool InvertY { get; private set; }

    [Toggle("Invert horizontal", false, Key = "invert_x", Section = "Free look")]
    public bool InvertX { get; private set; }

    [Toggle("Switch the player's controls off while looking around", true, Key = "mute", Section = "Free look",
        Tooltip = "Otherwise the right stick also moves the skier while you orbit the camera.")]
    public bool MutePlayer { get; private set; } = true;

    [Choice("Horizontal camera angle", "yAngle", "xAngle", "yAngle", Key = "yaw_field", Section = "Free look",
        Tooltip = "If the camera moves up and down when you push the stick left and right, switch this.")]
    public string YawField { get; private set; } = "yAngle";

    [Toggle("Rear view when clicking the right stick", true, Key = "rear", Section = "Rear view")]
    public bool RearView { get; private set; } = true;

    [Slider("Rear view swing speed", 12, 2, 40, 1, Key = "rear_speed", Section = "Rear view",
        Tooltip = "How fast the camera turns round. Higher = quicker.")]
    public float RearSpeed { get; private set; } = 12;

    [Slider("Rear view camera height (degrees above the slope)", 8, -10, 40, 1, Key = "rear_height", Section = "Rear view",
        Tooltip = "Low = you look up the slope at the rider from below. The slope angle is measured from the rider's movement.")]
    public float RearHeight { get; private set; } = 8;

    // ---------------------------------------------------------------- State

    bool inReplay => HankModPack.CameraOwnership.InReplay;
    readonly bool[] connected = new bool[4];
    int frameNumber;
    nint lastSelf;
    readonly JoinSpawn.RewiredMute mute = new();
    readonly HashSet<string> logged = new();
    long lastTick;

    enum Phase { Idle, Pressed, Looking }
    Phase phase = Phase.Idle;
    long pressStart;
    float lookYaw, lookPitch;
    float rear;              // 0..1 : how far the camera has swung round to the rear view
    float lastPadYaw, lastPadPitch;

    static LookAroundMod? instance;
    internal static bool IsLooking => instance is { Enabled: true, phase: Phase.Looking };

    public override void OnLoad()
    {
        instance = this;
        ImportPackSettings();
        EnterReplay.Subscribe(_ => {  MainThread.Run(() => Stop("replay")); });
        ExitReplay.Subscribe(_ => {  });
        CameraCallbacks.Follow.Register(this, Before, null);
        ChangeScene.Subscribe(_ => OnDeactivated());
    }

    void Once(string key, Exception e)
    {
        if (logged.Add(key)) Log($"Free Look: error in {key}: {e.Message}");
    }

    // ---------------------------------------------------------------- Gamepad

    struct Pads
    {
        public PadButton Buttons;
        public float RightX, RightY, LT, RT;
    }

    Pads ReadPads()
    {
        var p = new Pads();
        var probe = ++frameNumber % 120 == 1;
        for (var slot = 0; slot < connected.Length; slot++)
        {
            if (!connected[slot] && !probe) continue;
            var pad = Gamepad.Read(slot);
            connected[slot] = pad.Connected;
            if (!pad.Connected) continue;
            p.Buttons |= pad.Buttons;
            var d = pad.WithDeadzone(0.2f);
            p.RightX += d.RightX;
            p.RightY += d.RightY;
            p.LT = Math.Max(p.LT, d.LeftTrigger);
            p.RT = Math.Max(p.RT, d.RightTrigger);
        }
        p.RightX = Math.Clamp(p.RightX, -1, 1);
        p.RightY = Math.Clamp(p.RightY, -1, 1);
        return p;
    }

    // ---------------------------------------------------------------- Free look

    void Before(nint self)
    {
        lastSelf = self;
        var now = Stopwatch.GetTimestamp();
        var dt = lastTick == 0 ? 0.016f : Math.Clamp((now - lastTick) / (float)Stopwatch.Frequency, 0.001f, 0.1f);
        lastTick = now;

        if (!Enabled || !CameraOwnership.CanControlPlayCamera(self))
        {
            if (editorOpen) CloseEditor();
            if (modsOpen) CloseMods();
            if (keysOpen) CloseKeys();
            if (phase != Phase.Idle) Stop("off");
            else if (rearMuted) { try { mute.Restore(); } catch { } rearMuted = false; }
            upMute.Restore();
            rear = 0;
            return;
        }

        var pad = ReadPads();
        var down = (pad.Buttons & PadButton.DpadDown) != 0;
        TrackSpawnView(pad.Buttons, down);
        var ms = (now - pressStart) * 1000f / Stopwatch.Frequency;

        try { TrackRider(self, dt); } catch (Exception e) { Once("rider", e); }

        switch (phase)
        {
            case Phase.Idle:
                if (!down) needRelease = false;
                else if (!needRelease && !editorOpen && !modsOpen && !keysOpen) { phase = Phase.Pressed; pressStart = now; }
                break;
            case Phase.Pressed:
                if (!down) phase = Phase.Idle;                       // a tap: the game changes the stance by itself
                else if (ms >= HoldTime * 1000) StartLooking(self);
                break;
            case Phase.Looking:
                if (!down) { Stop("released"); break; }
                Orbit(self, pad, dt);
                break;
        }

        try { MenuTick(self, pad, now); } catch (Exception e) { Once("menu", e); }

        // Rear view: the game's own camera angles are swung round by 180 degrees (same fields the free look writes, which work).
        rearTarget = RearView && phase == Phase.Looking && RearButton is { } rb && IsDown(rb, pad) ? 1f : 0f;
        if (rearTarget != lastRearTarget) { lastRearTarget = rearTarget; Log(rearTarget > 0 ? "Free Look: right stick click -> rear view" : "Free Look: rear view released"); }
        if (rearTarget > 0 && rear <= 0.001f && !rearHeld && phase != Phase.Looking) CaptureRear(self);
        // The game may use the right stick click itself (it would put the camera back behind): switch the player's controls off while it is held.
        if (MutePlayer && phase != Phase.Looking)
        {
            if (rearTarget > 0 && !rearMuted) { mute.Mute(); rearMuted = true; }
            else if (rearTarget == 0 && rearMuted) { mute.Restore(); rearMuted = false; }
        }
        rear += (rearTarget - rear) * (1 - MathF.Exp(-RearSpeed * dt));
        if (Math.Abs(rear - rearTarget) < 0.002f) rear = rearTarget;
        rearHeld = rear > 0 || rearTarget > 0;
        if (rear <= 0 && rearTarget <= 0) armed = false;
        if (rear > 0 && phase != Phase.Looking) WriteRear(self);
        else if (rear <= 0) rearHeld = false;
    }
    bool rearHeld, rearMuted;
    float rearYaw0, rearPitch0;

    void CaptureRear(nint self)
    {
        var fc = new Jibbers.Interop.Game.FollowCam(self);
        rearYaw0 = YawField == "xAngle" ? fc.xAngle : fc.yAngle;
        rearPitch0 = YawField == "xAngle" ? fc.yAngle : fc.xAngle;
        Log($"Free Look: rear view from yaw {rearYaw0:0.#}, pitch {rearPitch0:0.#} (field {YawField}); reference known: {refKnown}, aligned yaw {heading:0}, slope {slopeDeg:0}");
    }

    void WriteRear(nint self)
    {
        var fc = new Jibbers.Interop.Game.FollowCam(self);
        RearPose(rearYaw0, rearPitch0, out var y, out var pch);
        if (YawField == "xAngle") { fc.xAngle = y; fc.yAngle = pch; } else { fc.yAngle = y; fc.xAngle = pch; }
    }
    float rearTarget, lastRearTarget;

    // ---------------------------------------------------------------- Rider / camera geometry (for the rear view)

    bool haveRider;
    float lpx, lpy, lpz, vx, vy, vz, speedH, heading, slopeDeg;
    bool camOk, idleKnown, refKnown;
    float phiCam, lastIdlePhi, forwardAngle;     // ground angle (degrees) of camera-from-rider, and of the way the rider faces
    nint bonesOf, lSh, rSh;
    readonly ObjectRoots boneRoots = new();
    bool noBones, bodyDone;
    float bodyAvg, bodySign = 1;
    int bodyN;

    static float Wrap(float a) => ((a % 360f) + 540f) % 360f - 180f;

    void TrackRider(nint self, float dt)
    {
        // The point the game's camera follows is the rider.
        nint r = 0;
        try { r = new Jibbers.Sdk.Game.Camera.Follow(self).Target.Ptr; } catch { }
        if (r == 0)
        {
            var sp = Spawner();
            if (sp != 0) { try { r = new Jibbers.Interop.Game.RagdollSpawner(sp).pha.Ptr; } catch { } }
        }
        if (r == 0 || Unity.IsNull(r)) { haveRider = false; if (logged.Add("no-rider")) Log("Free Look: no rider found for the rear view"); return; }
        if (logged.Add("rider-ok")) Log($"Free Look: rider for the rear view: '{Unity.Name(r)}' ({Il2Cpp.ClassOf(r).Name})");
        var pos = Unity.Position(r);
        if (haveRider)
        {
            var nvx = (pos.X - lpx) / dt; var nvy = (pos.Y - lpy) / dt; var nvz = (pos.Z - lpz) / dt;
            if (Math.Abs(nvx) + Math.Abs(nvz) < 90f)    // a respawn / teleport is not a movement
            {
                var k = 1 - MathF.Exp(-6 * dt);
                vx += (nvx - vx) * k; vy += (nvy - vy) * k; vz += (nvz - vz) * k;
            }
        }
        lpx = pos.X; lpy = pos.Y; lpz = pos.Z; haveRider = true;
        speedH = MathF.Sqrt(vx * vx + vz * vz);
        if (speedH >= 3f)
        {
            heading = MathF.Atan2(vx, vz) * 57.29578f;
            slopeDeg += (MathF.Atan2(-vy, speedH) * 57.29578f - slopeDeg) * (1 - MathF.Exp(-2 * dt));
        }

        // Where the camera really is around the rider (ground angle), measured on the picture the game just drew.
        camOk = false;
        foreach (var cam in RearCameras())
        {
            var cp = Unity.Position(cam);
            float dx = cp.X - pos.X, dz = cp.Z - pos.Z;
            if (dx * dx + dz * dz > 0.25f)
            {
                phiCam = MathF.Atan2(dx, dz) * 57.29578f; camOk = true;
                if (logged.Add("cam-used")) Log($"Free Look: camera used for the rear view: '{Unity.Name(cam)}', {MathF.Sqrt(dx * dx + dz * dz):0.0} m from the rider");
                break;
            }
        }
        if (!camOk && logged.Add("cam-none")) Log("Free Look: no camera found around the rider (rear view falls back to a half turn)");

        var idle = phase != Phase.Looking && rear < 0.01f && !rearHeld;
        if (idle)
        {
            var fc0 = new Jibbers.Interop.Game.FollowCam(self);
            natYaw = YawField == "xAngle" ? fc0.xAngle : fc0.yAngle;
            natPitch = YawField == "xAngle" ? fc0.yAngle : fc0.xAngle;
            natKnown = true;
        }
        var haveBody = BodyAxis(r, out var fx, out var fz);
        if (idle && camOk)
        {
            // The game's own camera sits behind the rider: that tells which way the rider faces (and which end of the shoulder line is the front).
            lastIdlePhi = phiCam; idleKnown = true;
            if (haveBody)
            {
                var nf = (phiCam + 180f) * 0.0174533f;
                var dot = fx * MathF.Sin(nf) + fz * MathF.Cos(nf);
                bodyAvg += (dot - bodyAvg) * 0.03f; bodyN++;
                if (bodyN > 40 && MathF.Abs(bodyAvg) > 0.4f)
                {
                    bodySign = MathF.Sign(bodyAvg);
                    if (!bodyDone) Log($"Free Look: body direction learned (sign {bodySign:+0;-0}, score {bodyAvg:0.00})");
                    bodyDone = true;
                }
            }
        }
        if (haveBody && bodyDone) { forwardAngle = MathF.Atan2(fx * bodySign, fz * bodySign) * 57.29578f; refKnown = true; }
        else if (idleKnown) { forwardAngle = lastIdlePhi + 180f; refKnown = true; }
        else refKnown = false;
    }

    /// <summary>Unit vector (ground) perpendicular to the shoulder line; which of the two ways is the front is decided by the caller.</summary>
    bool BodyAxis(nint rider, out float fx, out float fz)
    {
        fx = fz = 0;
        if (bonesOf != rider || (!noBones && (lSh == 0 || Unity.IsNull(lSh) || rSh == 0 || Unity.IsNull(rSh))))
        {
            boneRoots.Dispose(); boneRoots.Keep(rider);
            bonesOf = rider; lSh = rSh = 0; bodyDone = false; bodyAvg = 0; bodyN = 0; bodySign = 1; noBones = false;
            var tr = Unity.Transform(rider);
            var root = tr == 0 ? 0 : Unity.Call(tr, "get_root");
            if (root != 0 && !Unity.IsNull(root))
                foreach (var t in Unity.GetComponentsInChildren(root, Unity.Class("UnityEngine.Transform"), true))
                {
                    if (Unity.IsNull(t)) continue;
                    var n = Unity.Name(t).ToLowerInvariant();
                    if (n.Contains("head") || n.Contains("neck")) continue;
                    if (!(n.Contains("shoulder") || n.Contains("clavicle"))) continue;
                    var left = n.Contains("left") || n.EndsWith("_l") || n.EndsWith(".l") || n.StartsWith("l_");
                    var right = n.Contains("right") || n.EndsWith("_r") || n.EndsWith(".r") || n.StartsWith("r_");
                    if (left && lSh == 0) lSh = t; else if (right && rSh == 0) rSh = t;
                }
            boneRoots.Keep(lSh); boneRoots.Keep(rSh);
            noBones = lSh == 0 || rSh == 0;
            Log($"Free Look: shoulder bones {(noBones ? "not found (using the camera's side)" : "found")}");
        }
        if (noBones) return false;
        var l = Unity.Position(lSh); var rr = Unity.Position(rSh);
        float x = -(rr.Z - l.Z), z = rr.X - l.X;
        var m = MathF.Sqrt(x * x + z * z);
        if (m < 0.05f) return false;
        fx = x / m; fz = z / m;
        return true;
    }

    // Rear view feedback: the camera is steered until its real place around the rider (not the game's angle number) is where we want it.
    bool armed;
    float phiStart, rearOff, prevErr;
    int yawSign = 1, hist;
    float prevPhi, prevYawW, prevPrevYawW, corr;

    /// <summary>
    /// The rear view pose, blended by <see cref="rear"/> from the given start: the camera goes round to the FRONT of the rider (the direction his body faces)
    /// and looks back at him - straight behind him - low, looking up the slope. The yaw is steered by measuring where the camera really ends up.
    /// </summary>
    void RearPose(float startYaw, float startPitch, out float yaw, out float pitch)
    {
        // The game's own camera, when it is free, sits behind the rider (natYaw). The rear view is exactly the opposite side of that.
        // The game's xAngle is measured from the vertical (80 = camera almost level with the rider, 0 = straight above), so a low camera is a HIGH xAngle.
        var elevation = RearHeight - Math.Clamp(slopeDeg, -10, 70);   // wanted height of the camera above the horizontal, degrees
        var targetPitch = natKnown ? Math.Clamp(90f - elevation, 15f, 88f) : startPitch;
        pitch = startPitch + (targetPitch - startPitch) * rear;
        if (!natKnown) { yaw = startYaw + 180f * rear; return; }
        if (!armed) { armed = true; ListCameras(); Log($"Free Look: rear view: behind-the-rider yaw {natYaw:0}, view goes to {natYaw + 180f:0}, slope {slopeDeg:0}"); }
        yaw = startYaw + Wrap(natYaw + 180f - startYaw) * rear;
    }

    float natYaw, natPitch;
    bool natKnown;

    /// <summary>Diagnostics, once per press: every enabled camera, its distance to the rider and its parent.</summary>
    void ListCameras()
    {
        if (!logged.Add("cams-" + (frameNumber / 600))) return;
        try
        {
            var k = Unity.Class("UnityEngine.Camera");
            var all = Unity.FindObjects(k);
            var sb = new System.Text.StringBuilder();
            foreach (var c in all.Take(8))
            {
                var p = Unity.Position(c);
                sb.Append($"[{Unity.Name(c)} enabled={Unity.Call<byte>(c, "get_enabled")} depth={Unity.Call<float>(c, "get_depth"):0} fov={Unity.Call<float>(c, "get_fieldOfView"):0} at {p.X:0},{p.Y:0},{p.Z:0}] ");
            }
            var fp = Unity.Position(lastSelf);
            Log($"Free Look: cameras: {sb} | follow object '{Unity.Name(lastSelf)}' at {fp.X:0},{fp.Y:0},{fp.Z:0}; rider at {lpx:0},{lpy:0},{lpz:0}");
        }
        catch (Exception e) { Once("cams", e); }
    }

    void StartLooking(nint self)
    {
        phase = Phase.Looking;
        OpenMenu();
        var fc = new Jibbers.Interop.Game.FollowCam(self);
        try
        {
            lookYaw = YawField == "xAngle" ? fc.xAngle : fc.yAngle;
            lookPitch = YawField == "xAngle" ? fc.yAngle : fc.xAngle;
            Log($"Free Look: free look started (xAngle {fc.xAngle:0.#}, yAngle {fc.yAngle:0.#}, distance {fc.distance:0.#})");
        }
        catch (Exception e) { Once("fields", e); }

        if (MutePlayer)
        {
            var n = rearMuted ? 0 : mute.Mute();
            rearMuted = false;
            Log(mute.LastError is { } err ? "Free Look: could not switch the controls off: " + err : $"Free Look: player controls off ({n} maps)");
        }
    }

    static nint Spawner() => Jibbers.Sdk.Game.Player.RagdollSpawner.First()?.Ptr ?? 0;

    void Orbit(nint self, Pads pad, float dt)
    {
        var fc = new Jibbers.Interop.Game.FollowCam(self);
        // The game keeps easing the camera back behind the rider; our own angles are written again every frame, before it moves the camera.
        if (rear < 0.001f && rearTarget == 0)   // R3 only looks behind the rider: the stick is ignored while it is held
        {
            lookYaw += pad.RightX * (InvertX ? -1 : 1) * LookSpeed * dt;
            lookPitch += pad.RightY * (InvertY ? 1 : -1) * LookSpeed * dt * 0.6f;
        }
        lookPitch = Math.Clamp(lookPitch, -20, 85);
        float shownYaw = lookYaw, shownPitch = lookPitch;
        if (rear > 0.001f) RearPose(lookYaw, lookPitch, out shownYaw, out shownPitch);   // R3 while looking around: behind the rider, low
        if (YawField == "xAngle") { fc.xAngle = shownYaw; fc.yAngle = shownPitch; } else { fc.yAngle = shownYaw; fc.xAngle = shownPitch; }

        var zoom = 0f;
        if ((pad.Buttons & PadButton.RightShoulder) != 0 && !ZoomBlocked(PadBtn.RB)) zoom -= 1;   // R1: closer
        if ((pad.Buttons & PadButton.LeftShoulder) != 0 && !ZoomBlocked(PadBtn.LB)) zoom += 1;    // L1: farther
        if (zoom != 0) fc.distance = Math.Clamp(fc.distance * MathF.Exp(zoom * 1.2f * dt), 1.2f, 40);

        // Rare, so the log shows if the angles move the way the stick does.
        if ((Math.Abs(pad.RightX) > 0.5f || Math.Abs(pad.RightY) > 0.5f) && (frameNumber % 90) == 0)
            Log($"Free Look: stick ({pad.RightX:0.##}, {pad.RightY:0.##}) -> xAngle {fc.xAngle:0.#}, yAngle {fc.yAngle:0.#}, distance {fc.distance:0.#}");
    }

    void Stop(string why)
    {
        if (phase == Phase.Looking)
        {
            Log($"Free Look: free look ended ({why})");
            try { mute.Restore(); } catch { }
        }
        if (rearMuted) { try { mute.Restore(); } catch { } rearMuted = false; }
        phase = Phase.Idle;
        rear = 0; rearHeld = false; armed = false;
    }

    // ---------------------------------------------------------------- Rear view

    /// <summary>
    /// After the game placed the camera: swings it round the rider by rear x 180 degrees (to the other side, looking back at the rider),
    /// like the rear-view camera of a car game. Applied to the follow camera and to the main camera, whichever really draws the picture.
    /// </summary>
    PadButton spawnPrev;
    long pauseSince; bool pauseSeen;
    /// <summary>
    /// Two screens need the game's own camera: the spawn selector (D-pad up; it has no signal we can read, so D-pad up opens / closes it, A / Start close it)
    /// and the pause menu (Start; it is over when the cursor goes away again). The chosen camera is switched off for them, then comes back.
    /// </summary>
    void TrackSpawnView(PadButton b, bool dpadDown)
    {
        bool Pressed(PadButton x) => (b & x) != 0 && (spawnPrev & x) == 0;
        spawnPrev = b;
        if (PlayCamerasMod.Instance is not { } pc) return;
        var now = Stopwatch.GetTimestamp();
        if (upMute.Active && ((b & PadButton.DpadUp) == 0 || upFlipAt != 0 && now >= upFlipAt)) { upFlipAt = 0; try { upMute.Restore(); } catch { } }   // the game gets its D-pad back (while still held: a fresh press for it)

        // Pause menu: released once the cursor has shown and gone, or if it never showed.
        if (pc.Holding("pause menu"))
        {
            if (PlayCamerasMod.UiOpen) pauseSeen = true;
            if (pauseSeen && !PlayCamerasMod.UiOpen || !pauseSeen && now - pauseSince > Stopwatch.Frequency * 3) pc.Hold("pause menu", false);
        }

        if (PlayCamerasMod.SpawnView)
        {
            if (Pressed(PadButton.DpadUp) || Pressed(PadButton.A) || Pressed(PadButton.Start)) pc.SpawnSelector(false);
            return;
        }
        if (editorOpen || modsOpen || keysOpen || menuOpen || dpadDown || !pc.Active) return;
        if (Pressed(PadButton.Start) && !PlayCamerasMod.UiOpen && !pc.Holding("pause menu")) { pauseSince = now; pauseSeen = false; pc.Hold("pause menu", true); return; }
        // D-pad up: a short press is the game's (drone view / spawn selector). Held for a moment with a play camera on: back to the game's camera.
        // The game opened its selector at the press, so it is closed again: the game's controls are cut for a moment while the button is still down
        // (the game sees it released) and given back (it sees a new press, which closes the selector).
        if ((b & PadButton.DpadUp) != 0 && !PlayCamerasMod.UiOpen)
        {
            if (upSince == 0) upSince = now;
            else if (!upDone && now - upSince > Stopwatch.Frequency * 6 / 10)
            {
                upDone = true; pc.Select("Game camera"); Log("play camera: D-pad up held, the game's camera is back");
                try { upMute.Mute(); upFlipAt = now + Stopwatch.Frequency / 12; } catch (Exception e) { Once("upmute", e); }
            }
        }
        else { upSince = 0; upDone = false; }
    }
    long upFlipAt;
    readonly JoinSpawn.RewiredMute upMute = new();
    long upSince; bool upDone;

    void ApplyRear(nint tr, float d)
    {
        var pos = Unity.Call<Vector3>(tr, "get_position");
        var rot = Unity.Call<Quaternion>(tr, "get_rotation");
        var f = Rotate(rot, new Vector3(0, 0, 1));
        var pivot = new Vector3(pos.X + f.X * d, pos.Y + f.Y * d, pos.Z + f.Z * d);
        var a = rear * MathF.PI;
        var c = MathF.Cos(a); var s = MathF.Sin(a);
        Vector3 Turn(Vector3 v) => new(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        var off = Turn(new Vector3(pos.X - pivot.X, pos.Y - pivot.Y, pos.Z - pivot.Z));
        var np = new Vector3(pivot.X + off.X, pivot.Y + off.Y, pivot.Z + off.Z);
        var q = LookRotation(Turn(f), new Vector3(0, 1, 0));
        Unity.Call(tr, "set_position", (nint)(&np));
        Unity.Call(tr, "set_rotation", (nint)(&q));
    }

    IEnumerable<nint> RearCameras()
    {
        var k = Unity.Class("UnityEngine.Camera");
        if (lastSelf != 0 && !Unity.IsNull(lastSelf))
        {
            var c = Unity.GetComponent(lastSelf, k);
            if (c != 0 && !Unity.IsNull(c)) yield return c;
            else foreach (var x in Unity.GetComponentsInChildren(lastSelf, k, true)) if (x != 0 && !Unity.IsNull(x)) yield return x;
        }
        var main = Unity.MainCamera;
        if (main != 0 && !Unity.IsNull(main)) yield return main;
    }

    static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        var d = u.X * v.X + u.Y * v.Y + u.Z * v.Z;
        var uu = u.X * u.X + u.Y * u.Y + u.Z * u.Z;
        var cr = new Vector3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
        var k1 = 2 * d; var k2 = q.W * q.W - uu; var k3 = 2 * q.W;
        return new Vector3(u.X * k1 + v.X * k2 + cr.X * k3, u.Y * k1 + v.Y * k2 + cr.Y * k3, u.Z * k1 + v.Z * k2 + cr.Z * k3);
    }

    static Vector3 Norm(Vector3 v, Vector3 fallback)
    {
        var m = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return m < 1e-5f ? fallback : new Vector3(v.X / m, v.Y / m, v.Z / m);
    }

    static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        var z = Norm(forward, new Vector3(0, 0, 1));
        var x = Norm(new Vector3(up.Y * z.Z - up.Z * z.Y, up.Z * z.X - up.X * z.Z, up.X * z.Y - up.Y * z.X), new Vector3(1, 0, 0));
        var y = new Vector3(z.Y * x.Z - z.Z * x.Y, z.Z * x.X - z.X * x.Z, z.X * x.Y - z.Y * x.X);
        float m00 = x.X, m01 = y.X, m02 = z.X, m10 = x.Y, m11 = y.Y, m12 = z.Y, m20 = x.Z, m21 = y.Z, m22 = z.Z;
        var tr = m00 + m11 + m22;
        if (tr > 0) { var s = MathF.Sqrt(tr + 1) * 2; return new((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s); }
        if (m00 > m11 && m00 > m22) { var s = MathF.Sqrt(1 + m00 - m11 - m22) * 2; return new(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s); }
        if (m11 > m22) { var s = MathF.Sqrt(1 + m11 - m00 - m22) * 2; return new((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s); }
        { var s = MathF.Sqrt(1 + m22 - m00 - m11) * 2; return new((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s); }
    }
}
