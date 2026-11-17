using System.Collections.Concurrent;
using System.Diagnostics;
using Jibbers.Sdk.Multiplayer;
using ChairLift;
using ChairLift.Integration;
using Jibbers.Interop.Game;
using Manager = Jibbers.Sdk.Game.Env.Manager;
using Scene = Jibbers.Sdk.Game.Physics.Scene;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.UI;
using Snowcat.Integration;
using SnowShovel;
using SnowShovel.Integration;

namespace Snowcat;

/// <summary>
/// A snowcat you can drive: bring one up in front of you, climb into the cab and drive it over the
/// mountain (it follows the ground, tilting over the slopes; tracks turn it on the spot). You sit in
/// the cab, held there by the physics (as Chair Lift holds you on a chair), and step out beside it.
/// </summary>
public sealed unsafe class SnowcatMod : JibbersMod
{
    public override string Id => "snowcat";
    public override string Name => "Snowcat";
    public override string Author => "scotttucker1599";
    public override string Description => "Bring up a snowcat, climb into the cab and drive it anywhere on the mountain. Drop the blade to push snow, lower the tiller to groom the slope, ride along in a friend's, and reset the slopes when you're done.";

    [KeyBinding("Bring the snowcat here", Key.B, Key = "bring_key", Section = "Keys")] public Key BringKey { get; private set; }
    [KeyBinding("Get in / out", Key.F, Key = "enter_key", Section = "Keys")] public Key EnterKey { get; private set; }
    [KeyBinding("Blade down / up", Key.G, Key = "blade_key", Section = "Keys")] public Key BladeKey { get; private set; }
    [KeyBinding("Tiller down / up (groom the slope)", Key.T, Key = "tiller_key", Section = "Keys")] public Key TillerKey { get; private set; }
    [KeyBinding("Blade: angle left (hold)", Key.Z, Key = "angle_left_key", Section = "Blade & tiller")] public Key AngleLeftKey { get; private set; }
    [KeyBinding("Blade: angle right (hold)", Key.C, Key = "angle_right_key", Section = "Blade & tiller")] public Key AngleRightKey { get; private set; }
    [KeyBinding("Blade: tilt left end down (hold)", Key.Q, Key = "tilt_left_key", Section = "Blade & tiller")] public Key TiltLeftKey { get; private set; }
    [KeyBinding("Blade: tilt right end down (hold)", Key.E, Key = "tilt_right_key", Section = "Blade & tiller")] public Key TiltRightKey { get; private set; }
    [KeyBinding("Blade: straighten (no angle, no tilt)", Key.H, Key = "straighten_key", Section = "Blade & tiller")] public Key StraightenKey { get; private set; }
    [Slider("Blade: most it angles (degrees each way)", 30, 0, 45, 1, Key = "max_angle", Section = "Blade & tiller")] public double MaxAngle { get; private set; }
    [Slider("Blade: most it tilts (degrees each way)", 10, 0, 20, 1, Key = "max_tilt", Section = "Blade & tiller")] public double MaxTilt { get; private set; }
    [KeyBinding("Undo your last snowcat run (your own pushing and grooming, for everyone)", Key.U, Key = "undo_key", Section = "Keys")] public Key UndoKey { get; private set; }
    [KeyBinding("Admin panel: log in with the admin password, then reset the slopes for everyone", Key.K, Key = "reset_key", Section = "Admin")] public Key ResetKey { get; private set; }
    [KeyBinding("Headlights on / off", Key.L, Key = "lights_key", Section = "Keys")] public Key LightsKey { get; private set; }
    [Slider("Blade raised: height (metres above where it rests on the snow)", 0.5, 0, 1.5, 0.05, Key = "blade_raised", Section = "Blade & tiller")] public double BladeRaised { get; private set; }
    [Slider("Blade lowered: height (metres; below 0 digs in, above 0 skims over)", 0, -0.3, 0.5, 0.05, Key = "blade_lowered", Section = "Blade & tiller")] public double BladeLowered { get; private set; }
    [Slider("Tiller raised: height (metres up from the model's own)", 0, -0.5, 1, 0.05, Key = "tiller_raised", Section = "Blade & tiller")] public double TillerRaised { get; private set; }
    [Slider("Tiller lowered: height (metres up from the model's own; minus is down)", -0.95, -1.5, 0, 0.05, Key = "tiller_lowered", Section = "Blade & tiller")] public double TillerLowered { get; private set; }
    [Slider("Blade push strength (% of the snow above the blade moved each pass)", 60, 5, 100, 5, Key = "push_strength", Section = "Blade & tiller")] public double PushStrength { get; private set; }
    [Slider("Tiller grooming strength (% smoothed each pass)", 60, 5, 100, 5, Key = "groom_strength", Section = "Blade & tiller")] public double GroomStrength { get; private set; }
    [Slider("Raise / lower time (seconds)", 1.2, 0.3, 4, 0.1, Key = "raise_time", Section = "Blade & tiller")] public double RaiseTime { get; private set; }
    [Slider("Camera distance in the snowcat (metres)", 14, 4, 40, 1, Key = "camera_distance", Section = "Camera")] public double CameraDistance { get; private set; }
    [Toggle("Pull the camera back while you're in a snowcat", true, Key = "camera_zoom", Section = "Camera")] public bool CameraZoom { get; private set; }
    [Toggle("Controller: Y in / out, RT gas, LT reverse, left stick steer, B brake, X blade, D-pad down tiller", true, Key = "controller", Section = "Keys")] public bool UseController { get; private set; }
    /// <summary>Where you sit in the cab: metres above the ground, and forward of the middle.</summary>
    const float SeatHeight = 2.0f, SeatForward = 0.9f;

    /// <summary>The ground the tracks stand on: half its length and width (metres) from the middle.</summary>
    const float HalfLength = 3f, HalfWidth = 1.7f;
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    CatBody? body;
    SnowWorld? snow;
    bool bladeDown, bladeWas, padBladeWas, tillerDown, tillerWas, padTillerWas;
    float tillerAmount, bladeAmount; // 0 up, 1 down (eased when drawn)
    Vector3 TillerShift(float amount) => Parts.Shift(amount, (float)TillerRaised, (float)TillerLowered, Parts.TillerBack);
    Parts.Pose BladePose(float amount, float angle, float tilt) => new(Parts.Shift(amount, (float)BladeRaised, (float)BladeLowered), angle, tilt);
    /// <summary>The blade's swing (degrees, + to the right) and tilt (degrees, + right end up).</summary>
    float bladeAngle, bladeTilt;
    float pitch, roll;
    float leftTread, rightTread; // how far each belt has run (pattern repeats)

    // ---- other players' snowcats ----
    /// <summary>Another player's snowcat: drawn between their last two updates, and plowing on our terrain as theirs does.</summary>
    sealed class Remote(CatBody body)
    {
        public readonly CatBody Body = body;
        public CatPacket Previous, Last;
        public float LastTime, Interval = 0.1f;
        public CatPacket Shown;
        public bool HasShown;
        public DateTime Seen, NextPlow;
        /// <summary>In your party or on your server's player list (not only in the Snowcat lobby).</summary>
        public bool Trusted;
        /// <summary>On your server: trusted, or its driver's body is where a rider in your game is.</summary>
        public bool Near;
        public float BladeAmount, BladeAngle, BladeTilt, AngleTo, TiltTo;
    }
    // Every Snowcat player in the game, through one Steam lobby (no party needed); kept to the ones on your server.
    CatLobby? lobby;
    readonly HashSet<ulong> accepted = new();
    List<Vector3> shells = new();
    DateTime nextShells, nextPresence, nextHello;
    /// <summary>Lobby players who've said hello: their map, where their rider is, and when they said it.</summary>
    readonly Dictionary<ulong, (int Map, Vector3 At, DateTime When)> hellos = new();
    Vector3 myBody;
    /// <summary>Your runs of work (blade or tiller down, until both are up): each can be undone.</summary>
    int myRun, runCounter = Environment.TickCount & 0xFFFFFF;
    bool workOn, undoWas;
    // ---- the admin panel ----
    enum AdminStep { Closed, Login, NewPassword, Confirm, Menu }
    AdminStep adminStep;
    readonly TypedText typing = new();
    HudPanel? adminHud;
    HudLabel? adminLabel;
    string adminNote = "", newPassword = "", fingerprint = "";
    /// <summary>The admin password once you've logged in (sent with a reset so other players' Snowcat trusts it).</summary>
    string? adminPassword;
    static string AdminFile => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "JibbersMods", "snowcat_admin.txt");
    ServerLink? link;
    // Your party too (a server hosted through Play together lists no Steam players, but its party has them all).
    PartyChannel? party;
    readonly ConcurrentQueue<(ulong From, byte[] Data)> partyInbox = new();
    int saidPlayers = -1;
    readonly Dictionary<ulong, Remote> remotes = new();
    DateTime nextSend, nextRoster;
    int? mapId;
    DateTime nextPlow;
    readonly PhysicsHold physics = new();
    readonly Rider rider = new();
    HudPanel? hud;
    HudLabel? prompt;
    CatState? cat;
    Quaternion rotation = Quaternion.Identity;
    bool driving, active, enterWas, bringWas, padEnterWas, resetWas, lightsOn, lightsWas;
    GameRef? followCam;
    float? cameraWas; // the follow camera's own distance, to put back when you get out
    /// <summary>Riding along in another player's snowcat (theirs, by Steam id), or null.</summary>
    ulong? riding;
    /// <summary>Where the seats are across the cab (metres right of the middle): the driver's and the passenger's.</summary>
    const float DriverX = -0.45f, PassengerX = 0.55f;
    readonly Stopwatch since = new(), lastStep = new();
    (Vector3 At, Vector3 Velocity, float Time)? target;
    readonly List<IDisposable> subscriptions = new();
    string? lastError, shown;
    int frame;

    public override void OnLoad()
    {
        body = new CatBody(Log);
        snow = new SnowWorld(Log);
        link = ServerLink.Open(out var why, 0x5343); // "SC": the snowcat's own channel
        try
        {
            party = Party.Channel(this);
            party.Received += m => partyInbox.Enqueue((m.From.Id.Value, m.Data.ToArray()));
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { Log($"no party: {e.Message}"); }
        lobby = link is null ? null : CatLobby.Open(Log, out var lobbyWhy);
        Log(link is null && party is null ? $"other players won't see your snowcat: {why}" : "your snowcat is shown to everyone on your server who has Snowcat (party or not)");
        LoadFingerprint();
        active = true;
        hud = Hud.Register("Snowcat");
        prompt = hud.AddText("Prompt", new HudTextOptions { Anchor = HudAnchor.BottomCenter, Offset = new(0, 110), Size = new(1300, 50), FontSize = 24 });
        adminHud = Hud.Register("Snowcat admin");
        adminLabel = adminHud.AddText("Admin", new HudTextOptions { Anchor = HudAnchor.Center, Offset = new(0, 0), Size = new(1100, 220), FontSize = 28 });
        adminHud.Hide();
        Manager.UpdateMethod.Before((_, _) => Guard(OnFrame));
        Scene.FixedUpdateMethod.Before((scene, _) => Guard(() => PhysicsStep(scene)));
        subscriptions.Add(Events.Subscribe("event.respawn", _ => Guard(() => GetOut(placeBeside: false))));
        subscriptions.Add(Events.Subscribe("event.reset", _ => Guard(() => GetOut(placeBeside: false))));
        subscriptions.Add(Events.Subscribe("event.player_spawn", _ => Guard(MapChanged)));
        Log("Snowcat ready: press B to bring one, F to get in");
    }

    void Guard(Action a)
    {
        if (!active || !Enabled) return;
        try { a(); }
        catch (Exception e)
        {
            if (lastError == e.Message) return;
            lastError = e.Message;
            Log($"error: {e.Message}");
        }
    }

    /// <summary>
    /// Where your rider's body is: the middle of your own ragdoll parts (the controller's own object
    /// stays where you spawned while the physics moves the body).
    /// </summary>
    static Vector3 Body(RagdollController me)
    {
        var sum = new Vector3(0, 0, 0);
        var n = 0;
        foreach (var part in RagdollPart.Live())
        {
            if (part.IsNull || Unity.IsNull(part.Ptr) || part.IsShell) continue;
            sum = sum + Unity.Position(part.Ptr);
            n++;
        }
        return n > 0 ? sum * (1f / n) : Unity.Position(me.Ptr);
    }

    // ---- the ground ----

    /// <summary>The terrain's height under a point, or null off every terrain.</summary>
    static float? Ground(Vector3 at)
    {
        foreach (var t in Il2Cpp.ArrayObjects(Unity.CallStatic(Unity.Class("UnityEngine.Terrain"), "get_activeTerrains")))
        {
            if (t == 0 || Unity.IsNull(t)) continue;
            var data = Unity.Call(t, "get_terrainData");
            if (data == 0) continue;
            var origin = Unity.Position(t);
            var size = Unity.Call<Vector3>(data, "get_size");
            if (at.X < origin.X || at.Z < origin.Z || at.X > origin.X + size.X || at.Z > origin.Z + size.Z) continue;
            return origin.Y + Il2Cpp.ClassOf(t).Method("SampleHeight", 1).Invoke<float>(t, (nint)(&at));
        }
        return null;
    }

    /// <summary>Stands the snowcat on the ground at its spot: height and tilt from under its tracks. False off the terrain.</summary>
    bool Settle_(ref CatState s)
    {
        var f = Drive.Forward(s.Yaw) * HalfLength;
        var r = Drive.Right(s.Yaw) * HalfWidth;
        if (Ground(s.At + f - r) is not { } fl || Ground(s.At + f + r) is not { } fr || Ground(s.At - f - r) is not { } bl || Ground(s.At - f + r) is not { } br)
            return false;
        var (p, r2, height) = Drive.Sit(fl, fr, bl, br, HalfLength * 2, HalfWidth * 2);
        (pitch, roll) = (p, r2);
        s = s with { At = s.At with { Y = height } };
        rotation = Drive.Rotation(s.Yaw, p, r2);
        return true;
    }

    // ---- frames ----

    void OnFrame()
    {
        if (body is null) return;
        frame++;
        snow?.Tick();
        Network();
        var boxOpen = AdminFrame();
        var focused = Input.GameFocused && !Input.Typing && !boxOpen;
        var pad = focused && UseController ? Gamepad.Read() : default;
        bool Down(Key k) => focused && Input.IsDown(k);
        var enter = Down(EnterKey);
        var padEnter = pad.Held(PadButton.Y);
        var bring = Down(BringKey) && !Input.IsDown(Key.Control);
        var enterPressed = (enter && !enterWas) || (padEnter && !padEnterWas);
        var bringPressed = bring && !bringWas;
        (enterWas, padEnterWas, bringWas) = (enter, padEnter, bring);

        var reset = Down(ResetKey);
        if (reset && !resetWas) OpenAdmin();
        resetWas = reset;
        var undo = Down(UndoKey);
        if (undo && !undoWas) UndoMyRun();
        undoWas = undo;
        var lightsKey = Down(LightsKey);
        if (lightsKey && !lightsWas && cat is not null)
        {
            lightsOn = !lightsOn;
            Log(lightsOn ? "headlights on" : "headlights off");
        }
        lightsWas = lightsKey;
        Camera(driving || riding is not null);

        var r = Rider.Find();
        if (r is { } body_) myBody = Body(body_);
        if (!driving) workOn = false;
        var you = UseController ? $"{EnterKey} / Y" : $"{EnterKey}";
        if (bringPressed && !driving && riding is null && r is { } me) Bring(me);

        if (riding is not null)
        {
            // Riding along: your own snowcat (if out) waits where you left it.
            if (cat is not null) Step(new Controls(0, 0, true));
            if (enterPressed && since.Elapsed > TimeSpan.FromSeconds(0.5)) { GetOut(placeBeside: true); return; }
            Seat(r);
            var speed = Vehicle() is { } v ? MathF.Abs(v.Speed) * 3.6f : 0;
            Prompt($"Riding along • {you}: get out • {speed:0} km/h");
            return;
        }
        if (driving)
        {
            if (enterPressed && since.Elapsed > TimeSpan.FromSeconds(0.5)) { GetOut(placeBeside: true); return; }
            var throttle = (Down(Key.W) || Down(Key.Up) ? 1f : 0) - (Down(Key.S) || Down(Key.Down) ? 1f : 0) + pad.RightTrigger - pad.LeftTrigger;
            var steer = (Down(Key.D) || Down(Key.Right) ? 1f : 0) - (Down(Key.A) || Down(Key.Left) ? 1f : 0) + (MathF.Abs(pad.LeftX) > 0.15f ? pad.LeftX : 0);
            var brake = Down(Key.Space) || pad.Held(PadButton.B);
            var blade = Down(BladeKey);
            var padBlade = pad.Held(PadButton.X);
            if ((blade && !bladeWas) || (padBlade && !padBladeWas))
            {
                bladeDown = !bladeDown;
                Log(bladeDown ? "blade down: pushing snow" : "blade up");
            }
            (bladeWas, padBladeWas) = (blade, padBlade);
            var tillerKey = Down(TillerKey);
            var padTiller = pad.Held(PadButton.DpadDown);
            if ((tillerKey && !tillerWas) || (padTiller && !padTillerWas))
            {
                tillerDown = !tillerDown;
                Log(tillerDown ? "tiller down: grooming" : "tiller up");
            }
            (tillerWas, padTillerWas) = (tillerKey, padTiller);
            // The blade swings and tilts while the keys are held (D-pad left / right and the shoulder buttons on a controller).
            var swing = (Down(AngleRightKey) || pad.Held(PadButton.DpadRight) ? 1 : 0) - (Down(AngleLeftKey) || pad.Held(PadButton.DpadLeft) ? 1 : 0);
            var lean = (Down(TiltLeftKey) || pad.Held(PadButton.LeftShoulder) ? 1 : 0) - (Down(TiltRightKey) || pad.Held(PadButton.RightShoulder) ? 1 : 0);
            var dtBlade = Math.Clamp(Unity.DeltaTime, 0, 0.1f);
            bladeAngle = Math.Clamp(bladeAngle + swing * 25 * dtBlade, -(float)MaxAngle, (float)MaxAngle);
            bladeTilt = Math.Clamp(bladeTilt + lean * 8 * dtBlade, -(float)MaxTilt, (float)MaxTilt);
            if (Down(StraightenKey)) (bladeAngle, bladeTilt) = (MoveTo(bladeAngle, 0, 40 * dtBlade), MoveTo(bladeTilt, 0, 15 * dtBlade));
            // A new run of work each time the blade or tiller goes down (after both were up): undone as one.
            if ((bladeDown || tillerDown) && !workOn) myRun = ++runCounter;
            workOn = bladeDown || tillerDown;
            Step(new Controls(Math.Clamp(throttle, -1, 1), Math.Clamp(steer, -1, 1), brake));
            Seat(r);
            Prompt($"Driving • W/S gas, reverse • A/D steer • Space brake • {BladeKey}{(UseController ? " / X" : "")}: blade {(bladeDown ? "UP" : "down")} • {TillerKey}{(UseController ? " / D-pad down" : "")}: tiller {(tillerDown ? "UP" : "down")} • {AngleLeftKey}/{AngleRightKey}: angle {bladeAngle:0}° • {TiltLeftKey}/{TiltRightKey}: tilt {bladeTilt:0}° • {LightsKey}: lights {(lightsOn ? "off" : "on")} • {UndoKey}: undo run • {you}: get out • {MathF.Abs(cat!.Value.Speed) * 3.6f:0} km/h{(bladeDown ? " • PUSHING SNOW" : "")}{(tillerAmount > 0.9f ? " • GROOMING" : "")}");
            return;
        }

        if (cat is not null) Step(new Controls(0, 0, true));
        // The nearest snowcat within reach: yours (drive it) or another player's (ride along in it).
        if (r is not { } walker) { Prompt(null); return; }
        var at = Body(walker);
        (ulong? Whose, float Distance)? nearest = null;
        if (cat is not null) nearest = (null, Vec.Distance(at, SeatAt(null)));
        foreach (var (id, rc) in remotes)
            if (rc.HasShown && Vec.Distance(at, SeatAt(id)) is var d && d < (nearest?.Distance ?? float.MaxValue))
                nearest = (id, d);
        if (nearest is { } n && n.Distance < 6)
        {
            if (enterPressed) GetIn(walker, n.Whose);
            Prompt(n.Whose is null ? $"{you}: drive the snowcat" : $"{you}: ride along in their snowcat");
        }
        else Prompt(null);
    }

    static float MoveTo(float from, float to, float step) => MathF.Abs(to - from) <= step ? to : from + MathF.Sign(to - from) * step;

    /// <summary>Brings the snowcat up a few metres in front of you, facing the way you're looking.</summary>
    void Bring(RagdollController me)
    {
        var at = Body(me);
        // Camera.main isn't set in this game: the follow camera (FollowCam) is the one you look through.
        var follow = Unity.FindObjects(Il2Cpp.RequireClass("Assembly-CSharp.dll", "FollowCam")).FirstOrDefault(c => c != 0 && !Unity.IsNull(c));
        var camera = follow != 0 ? follow : Unity.MainCamera;
        var look = camera != 0 && !Unity.IsNull(camera) ? Unity.Call<Vector3>(Unity.Transform(camera), "get_forward") : new Vector3(0, 0, 1);
        var dir = Vec.FlatDirection(look) ?? new Vector3(0, 0, 1);
        var yaw = MathF.Atan2(dir.X, dir.Z) * 180 / MathF.PI;
        var s = new CatState(at + dir * 8, yaw, 0);
        if (!Settle_(ref s)) { Log("no ground there for a snowcat"); return; }
        cat = s;
        body!.Show(s.At, rotation, tillerShift: TillerShift(tillerAmount), blade_: BladePose(bladeAmount, bladeAngle, bladeTilt), headlights: lightsOn);
        Log($"snowcat brought to ({s.At.X:0}, {s.At.Y:0}, {s.At.Z:0})");
    }

    /// <summary>Drives the snowcat one frame on, over the ground.</summary>
    void Step(Controls c)
    {
        if (cat is not { } s) return;
        var dt = Math.Clamp(Unity.DeltaTime, 0, 0.1f);
        var f = Drive.Forward(s.Yaw) * HalfLength;
        var grade = Ground(s.At + f) is { } ahead && Ground(s.At - f) is { } behind ? (ahead - behind) / (HalfLength * 2) : 0;
        var next = Drive.Step(s, c, dt, grade);
        if (!Settle_(ref next)) next = s with { Speed = 0 }; // the terrain's edge: stop there
        cat = next;
        // The tracks run with the ground: the bottom of each belt moves back as the snowcat goes forward.
        if (dt > 0)
        {
            var (left, right) = Belt.Speeds(next.Speed, (next.Yaw - s.Yaw) / dt);
            leftTread = (leftTread - left * dt / Belt.Repeat) % 1000;
            rightTread = (rightTread - right * dt / Belt.Repeat) % 1000;
        }
        // The tiller lowers and lifts over a second.
        // The tiller and blade go up and down smoothly, over the raise / lower time.
        var rate = dt / (float)Math.Max(0.1, RaiseTime);
        tillerAmount = Math.Clamp(tillerAmount + (tillerDown && driving ? rate : -rate), 0, 1);
        bladeAmount = Math.Clamp(bladeAmount + (bladeDown ? rate : -rate), 0, 1);
        body!.Show(next.At, rotation, leftTread, rightTread, TillerShift(tillerAmount), BladePose(bladeAmount, bladeAngle, bladeTilt), lightsOn);
        var grooming = tillerAmount > 0.9f;
        var pushing = bladeDown && bladeAmount > 0.9f;
        if (driving && (pushing || grooming) && MathF.Abs(next.Speed) > 0.2f && DateTime.UtcNow >= nextPlow)
        {
            nextPlow = DateTime.UtcNow + TimeSpan.FromSeconds(0.1);
            Work(next, pitch, roll, pushing, grooming, "", myRun, bladeAngle, bladeTilt);
        }
    }

    /// <summary>
    /// The blade pushes snow (whatever stands above its edge, moved into a ridge ahead) and the
    /// tiller grooms behind: one pass over the terrain under the snowcat.
    /// </summary>
    void Work(CatState s, float pitch, float roll, bool push, bool groom, string owner, int run, float angle = 0, float tilt = 0)
    {
        var (tanPitch, tanRoll) = (MathF.Tan(pitch * MathF.PI / 180), MathF.Tan(roll * MathF.PI / 180));
        snow?.Reshape(s.At, s.Yaw, 6, (cells, w, h, local, spacing, baseY) =>
        {
            // The blade's bottom: the ground plane the tracks stand on, at that spot (metres above the terrain's base).
            float Ground(float x, float z) => s.At.Y - baseY + z * tanPitch + x * tanRoll + (float)BladeLowered;
            if (push)
            {
                // In the blade's own frame: swung round about its pivot, and tilted (one end lower than the other).
                var (pz, tanTilt) = (Parts.BladePivot.Z, MathF.Tan(tilt * MathF.PI / 180));
                (float X, float Z) BladeLocal(int i, int j) { var (x, z) = local(i, j); return Plow.BladeFrame(x, z, angle, pz); }
                float BladeGround(float bx, float bz) { var (x, z) = Plow.FromBlade(bx, bz, angle, pz); return Ground(x, z) + bx * tanTilt; }
                Plow.Push(cells, w, h, BladeLocal, BladeGround, spacing, (float)Math.Clamp(PushStrength / 100, 0.01, 1));
            }
            if (groom)
            {
                // The tiller: smooths the strip behind, a little more each pass, so bumps and ruts groom out.
                var blurred = Sculpt.Blur(cells, w, h, Math.Max(1, (int)MathF.Round(0.5f / spacing)));
                Plow.Groom(cells, blurred, w, h, local, (float)Math.Clamp(GroomStrength / 100, 0.01, 1));
            }
        }, owner, run);
    }

    /// <summary>The snowcat you're in (yours, or the one you're riding along in): where it is, how it sits, which way and how fast it's going.</summary>
    (Vector3 At, Quaternion Rotation, float Yaw, float Speed)? Vehicle(ulong? whose)
    {
        if (whose is { } id)
            return remotes.TryGetValue(id, out var rc) && rc.HasShown ? (rc.Shown.At, Drive.Rotation(rc.Shown.Yaw, rc.Shown.Pitch, rc.Shown.Roll), rc.Shown.Yaw, rc.Shown.Speed) : null;
        return cat is { } s ? (s.At, rotation, s.Yaw, s.Speed) : null;
    }

    (Vector3 At, Quaternion Rotation, float Yaw, float Speed)? Vehicle() => Vehicle(riding);

    /// <summary>
    /// A seat in a snowcat (the seat settings, in the cab): the driver's in yours, the passenger's in
    /// another player's (<paramref name="whose"/>).
    /// </summary>
    Vector3 SeatAt(ulong? whose) => Vehicle(whose) is { } v
        ? v.At + Drive.Rotate(v.Rotation, new Vector3(whose is null ? DriverX : PassengerX, SeatHeight, SeatForward))
        : default;

    Vector3 SeatAt() => SeatAt(riding);

    Vector3 Velocity() => Vehicle() is { } v ? Drive.Rotate(v.Rotation, new Vector3(0, 0, v.Speed)) : default;

    void GetIn(RagdollController me, ulong? whose)
    {
        driving = whose is null;
        riding = whose;
        since.Restart();
        physics.End();
        target = null;
        rider.Sit(me, 85, 55);
        Log(whose is null ? "in the snowcat" : "riding along in another player's snowcat");
    }

    /// <summary>Holds you on the seat: placed there the hard way at first, then held by the physics every step.</summary>
    void Seat(RagdollController? r)
    {
        if (r is not { } me || Vehicle() is not { } v) { GetOut(placeBeside: false); return; }
        var at = SeatAt();
        var velocity = Velocity();
        var facing = Vec.Yaw(Drive.Forward(v.Yaw));
        Rider.AllowPhone(me);
        if (frame % 10 == 0) rider.Sit(me, 85, 55);
        rider.LookAt(me, at + Drive.Forward(v.Yaw) * 20 + new Vector3(0, 0.5f, 0));
        if (physics.Holding && !physics.StillValid())
        {
            physics.End();
            target = null;
            since.Restart();
        }
        if (since.Elapsed < Settle)
        {
            Rider.Place(me, at, facing, velocity);
            return;
        }
        if (!physics.Holding)
        {
            if (!physics.Begin(at, Body(me), 1.8f, Rider.Parts(me)))
            {
                Rider.Place(me, at, facing, velocity);
                return;
            }
            Log("holding you in the cab");
        }
        target = (at, velocity, Unity.Time);
        if (!lastStep.IsRunning || lastStep.Elapsed > TimeSpan.FromMilliseconds(250))
            physics.Hold(PhysicsHold.Scene(), at, velocity);
        if (physics.RootPosition() is { } root && Vec.Distance(root, at) > 3)
            Rider.Place(me, at, facing, velocity);
    }

    /// <summary>Out of the cab: standing beside it on your seat's side (or just let go, after a respawn).</summary>
    void GetOut(bool placeBeside)
    {
        if (!driving && riding is null) return;
        var v = Vehicle();
        var passenger = riding is not null;
        driving = false;
        riding = null;
        target = null;
        physics.End();
        var r = Rider.Find();
        rider.Stand(r);
        if (placeBeside && r is { } me && v is { } s)
        {
            var side = s.At + Drive.Right(s.Yaw) * ((passenger ? 1 : -1) * (HalfWidth + 1.6f));
            var ground = Ground(side) ?? s.At.Y;
            Rider.Place(me, side with { Y = ground + 1.2f }, Vec.Yaw(Drive.Forward(s.Yaw)), new Vector3(0, 0, 0));
        }
        Log("out of the snowcat");
    }

    /// <summary>This map, as a number (players on another map ignore our snowcat), once its terrain's loaded.</summary>
    int? Map()
    {
        if (mapId is { } id) return id;
        var name = SnowWorld.Map();
        return name.Length == 0 ? null : mapId = CatPacket.MapId(name);
    }

    /// <summary>
    /// Every frame: who has Snowcat (your party, your server's player list, and the Snowcat lobby, which
    /// has every Snowcat player in the game: only the ones whose rider is near one in your game count);
    /// our snowcat to them twenty times a second (once a second to lobby players not yet seen here, so
    /// they can find us); theirs from them, drawn smoothly, plowing our terrain as theirs does.
    /// </summary>
    void Network()
    {
        if (link is null && party is null) return;
        var now = DateTime.UtcNow;
        lobby?.Tick();
        if (now >= nextRoster)
        {
            nextRoster = now + TimeSpan.FromSeconds(3);
            link?.Refresh();
            if (link is not null && lobby is not null)
                foreach (var id in lobby.Members) if (accepted.Add(id)) link.Accept(id);
        }
        if (now >= nextShells)
        {
            nextShells = now + TimeSpan.FromSeconds(0.5);
            shells = Rider.Others();
        }
        var partyPeers = PartyPeers();
        var serverOnly = link is null ? new List<ulong>() : link.Players.Where(id => !partyPeers.Contains(id)).ToList();
        var lobbyOnly = link is null || lobby is null ? new List<ulong>() : lobby.Members.Where(id => !partyPeers.Contains(id) && !link.Players.Contains(id)).ToList();
        var map = Map();
        // On our server: their hello says the same map, with their rider where a rider in our game is
        // (or their snowcat is already showing here).
        bool Here(ulong id) =>
            (hellos.TryGetValue(id, out var h) && now - h.When < TimeSpan.FromSeconds(8) && h.Map == map && shells.Any(at => Vec.Distance(at, h.At) < 8))
            || (remotes.TryGetValue(id, out var rm) && rm.Near);
        var here = lobbyOnly.Where(Here).ToList();
        if (map is { } hm && now >= nextHello)
        {
            nextHello = now + TimeSpan.FromSeconds(2);
            var hello = HelloPacket.Encode(hm, myBody);
            Send(hello, reliable: false);
            foreach (var id in lobbyOnly) link!.Send(id, hello, reliable: false);
        }
        var count = partyPeers.Count + serverOnly.Count + here.Count;
        if (count * 1000 + lobbyOnly.Count != saidPlayers)
        {
            saidPlayers = count * 1000 + lobbyOnly.Count;
            Log($"sharing your snowcat with {partyPeers.Count} in your party, {serverOnly.Count + here.Count} more on your server ({lobbyOnly.Count} Snowcat players online in all)");
        }
        if (map is { } m && cat is { } c && now >= nextSend)
        {
            nextSend = now + TimeSpan.FromSeconds(0.05);
            var bytes = new CatPacket(m, c.At, c.Yaw, pitch, roll, c.Speed, leftTread, rightTread, tillerAmount, driving && bladeDown, driving && tillerAmount > 0.9f, lightsOn, myBody, myRun, BladeLowered: bladeDown).Encode();
            var bladeBytes = BladePacket.Encode(bladeAngle, bladeTilt);
            Send(bytes, reliable: false);
            Send(bladeBytes, reliable: false);
            foreach (var id in here) { link!.Send(id, bytes, reliable: false); link.Send(id, bladeBytes, reliable: false); }
            if (now >= nextPresence)
            {
                nextPresence = now + TimeSpan.FromSeconds(1);
                foreach (var id in lobbyOnly.Where(id => !here.Contains(id))) link!.Send(id, bytes, reliable: false);
            }
        }
        var time = Unity.Time;
        var arrived = link?.Receive() ?? new List<(ulong, byte[])>();
        while (partyInbox.TryDequeue(out var item)) arrived.Add(item);
        foreach (var (from, data) in arrived)
        {
            if (ResetPacket.Decode(data) is { } rs)
            {
                if (rs.Map != map || snow is null) continue;
                if (AdminPassword.Matches(rs.Password, fingerprint)) Log($"an admin reset the slopes ({snow.ResetAll()} patches put back)");
                else Log("ignored a slope reset from a player without the admin password");
                continue;
            }
            if (UndoPacket.Decode(data) is { } u)
            {
                if (u.Map == map && snow is not null && snow.UndoRun(from.ToString(), u.Run) is var n && n > 0) Log($"another player undid a snowcat run ({n} patches put back)");
                continue;
            }
            if (HelloPacket.Decode(data) is { } hi)
            {
                hellos[from] = (hi.Map, hi.At, now);
                continue;
            }
            if (BladePacket.Decode(data) is { } bp)
            {
                if (remotes.TryGetValue(from, out var rb)) (rb.AngleTo, rb.TiltTo) = (bp.Angle, bp.Tilt);
                continue;
            }
            if (CatPacket.Decode(data) is not { } p || p.Map != map) continue;
            if (!remotes.TryGetValue(from, out var r))
                remotes[from] = r = new Remote(new CatBody(Log)) { Previous = p, Last = p, LastTime = time };
            r.Trusted = partyPeers.Contains(from) || (link?.Players.Contains(from) ?? false);
            r.Interval = Math.Clamp(time - r.LastTime, 0.05f, 0.5f);
            (r.Previous, r.Last, r.LastTime, r.Seen) = (r.Last, p, time, now);
        }
        foreach (var (id, r) in remotes.ToList())
        {
            if (now - r.Seen > TimeSpan.FromSeconds(3))
            {
                if (riding == id) GetOut(placeBeside: false);
                r.Body.Dispose();
                remotes.Remove(id);
                continue;
            }
            // Lobby players on another server of the same map: their driver isn't in your game. Not drawn.
            var near = r.Trusted || shells.Any(at => Vec.Distance(at, r.Last.Driver) < 12)
                || (hellos.TryGetValue(id, out var hh) && now - hh.When < TimeSpan.FromSeconds(8) && shells.Any(at => Vec.Distance(at, hh.At) < 8));
            if (near != r.Near)
            {
                r.Near = near;
                if (near) Log("another player's snowcat is here");
            }
            if (!near)
            {
                if (riding == id) GetOut(placeBeside: false);
                if (r.HasShown) r.Body.Hide();
                r.HasShown = false;
                continue;
            }
            // Between updates it carries on at its last speed (with ~300 ms ping updates come late and in
            // bunches), and it glides towards where it should be rather than jumping there.
            var age = Math.Min(time - r.LastTime, 0.6f);
            var ahead = r.Last with { At = r.Last.At + Drive.Forward(r.Last.Yaw) * (r.Last.Speed * age) };
            var follow = 1 - MathF.Exp(-Math.Clamp(Unity.DeltaTime, 0, 0.1f) * 10);
            var s = r.HasShown && Vec.Distance(r.Shown.At, ahead.At) < 15 ? CatPacket.Between(r.Shown, ahead, follow) : ahead;
            var step = Math.Clamp(Unity.DeltaTime, 0, 0.1f) / (float)Math.Max(0.1, RaiseTime);
            r.BladeAmount = Math.Clamp(r.BladeAmount + (s.Blade || s.BladeLowered ? step : -step), 0, 1);
            r.BladeAngle = MoveTo(r.BladeAngle, r.AngleTo, 40 * Math.Clamp(Unity.DeltaTime, 0, 0.1f));
            r.BladeTilt = MoveTo(r.BladeTilt, r.TiltTo, 15 * Math.Clamp(Unity.DeltaTime, 0, 0.1f));
            r.Body.Show(s.At, Drive.Rotation(s.Yaw, s.Pitch, s.Roll), s.LeftTread, s.RightTread, TillerShift(s.Tiller), BladePose(r.BladeAmount, r.BladeAngle, r.BladeTilt), s.Lights);
            (r.Shown, r.HasShown) = (s, true);
            if ((s.Blade || s.Grooming) && MathF.Abs(s.Speed) > 0.2f && now >= r.NextPlow)
            {
                r.NextPlow = now + TimeSpan.FromSeconds(0.1);
                Work(new CatState(s.At, s.Yaw, s.Speed), s.Pitch, s.Roll, s.Blade, s.Grooming, id.ToString(), s.Run, r.BladeAngle, r.BladeTilt);
            }
        }
    }

    HashSet<ulong> PartyPeers() => party is not null && Party.State == PartyState.InParty ? party.Peers.Select(p => p.Id.Value).ToHashSet() : new HashSet<ulong>();

    /// <summary>To every Snowcat player in the lobby too (an undo or a reset: rare, and everyone should get it).</summary>
    void SendEveryone(byte[] bytes)
    {
        Send(bytes, reliable: true);
        if (link is null || lobby is null) return;
        var partyPeers = PartyPeers();
        foreach (var id in lobby.Members.Where(id => !partyPeers.Contains(id) && !link.Players.Contains(id))) link.Send(id, bytes, reliable: true);
    }

    /// <summary>Takes back your last run of snowcat work (the blade's pushing, the tiller's grooming), here and for everyone.</summary>
    void UndoMyRun()
    {
        if (snow is null) return;
        if (snow.RunsOf("") is not { Count: > 0 } mine) { Log("no snowcat runs of yours to undo"); return; }
        var run = mine[^1];
        var put = snow.UndoRun("", run);
        if (Map() is { } m) SendEveryone(UndoPacket.Encode(m, run));
        // Still working: carry on in a new run.
        if (workOn) myRun = ++runCounter;
        Log($"undid your last snowcat run ({put} patches put back){(mine.Count > 1 ? $", {mine.Count - 1} more to undo" : "")}");
    }

    // ---- the admin panel ----

    void LoadFingerprint()
    {
        try { fingerprint = File.Exists(AdminFile) ? File.ReadAllText(AdminFile).Trim() : ""; }
        catch (IOException) { fingerprint = ""; }
        if (!AdminPassword.IsFingerprint(fingerprint)) fingerprint = "";
    }

    void OpenAdmin()
    {
        LoadFingerprint();
        if (adminPassword is not null && !AdminPassword.Matches(adminPassword, fingerprint)) adminPassword = null;
        adminStep = adminPassword is not null ? AdminStep.Menu : fingerprint.Length > 0 ? AdminStep.Login : AdminStep.NewPassword;
        adminNote = "";
        typing.Start();
    }

    /// <summary>Every frame the admin box is open: typing the password, or the admin's menu. True while it's open (the game keys are off).</summary>
    bool AdminFrame()
    {
        if (adminStep == AdminStep.Closed || adminHud is null || adminLabel is null) return false;
        var done = typing.Frame();
        var stars = new string('*', typing.Text.Length);
        void Close() { adminStep = AdminStep.Closed; adminHud.Hide(); }
        if (done == false) { Close(); return true; }
        switch (adminStep)
        {
            case AdminStep.Login:
                if (done == true)
                {
                    if (AdminPassword.Matches(typing.Text, fingerprint)) { adminPassword = typing.Text; adminStep = AdminStep.Menu; adminNote = "Logged in."; Log("logged in as a Snowcat admin"); }
                    else adminNote = "Wrong password, try again.";
                    typing.Start();
                }
                adminLabel.Text = $"SNOWCAT ADMIN\nAdmin password: {stars}_\n{adminNote}\nEnter: log in • Esc: close";
                break;
            case AdminStep.NewPassword:
                if (done == true)
                {
                    if (typing.Text.Length < 4) adminNote = "At least 4 characters.";
                    else { newPassword = typing.Text; adminStep = AdminStep.Confirm; adminNote = ""; }
                    typing.Start();
                }
                adminLabel.Text = $"SNOWCAT ADMIN\nNo admin password on this PC yet. Make one:\n{stars}_\n{adminNote}\nEnter: next • Esc: close";
                break;
            case AdminStep.Confirm:
                if (done == true)
                {
                    if (typing.Text != newPassword) { adminStep = AdminStep.NewPassword; adminNote = "They didn't match, start again."; }
                    else
                    {
                        fingerprint = AdminPassword.Fingerprint(newPassword);
                        try
                        {
                            File.WriteAllText(AdminFile, fingerprint + Environment.NewLine);
                            Log($"admin password set: give other players {AdminFile} (it holds only a scrambled fingerprint, not the password) so their Snowcat trusts your resets");
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"couldn't save the admin password: {e.Message}"); }
                        adminPassword = newPassword;
                        adminStep = AdminStep.Menu;
                        adminNote = "Admin password set.";
                    }
                    newPassword = "";
                    typing.Start();
                }
                adminLabel.Text = $"SNOWCAT ADMIN\nType it again:\n{stars}_\n{adminNote}\nEnter: set it • Esc: close";
                break;
            case AdminStep.Menu:
                var key = typing.Text.ToLowerInvariant();
                if (key.Contains('r')) { ResetSlopes(); Close(); return true; }
                if (key.Contains('o')) { adminPassword = null; Log("logged out of Snowcat admin"); Close(); return true; }
                if (typing.Text.Length > 0) typing.Start();
                adminLabel.Text = $"SNOWCAT ADMIN\n{adminNote}\nR: reset the slopes for everyone\nO: log out • Esc: close";
                break;
        }
        adminHud.Show();
        return true;
    }

    /// <summary>
    /// The follow camera pulled back to the camera distance setting while you're in a snowcat (it's a big
    /// machine), and put back to what it was when you get out.
    /// </summary>
    void Camera(bool inside)
    {
        if (followCam is null || Unity.IsNull(followCam.Target))
        {
            var found = Unity.FindObjects(Il2Cpp.RequireClass("Assembly-CSharp.dll", "FollowCam")).FirstOrDefault(c => c != 0 && !Unity.IsNull(c));
            if (found == 0) return;
            followCam = new GameRef(found);
            cameraWas = null;
        }
        var cam = new Jibbers.Interop.Game.FollowCam(followCam.Target);
        if (inside && CameraZoom)
        {
            cameraWas ??= cam.distance;
            cam.distance = (float)CameraDistance;
        }
        else if (cameraWas is { } was)
        {
            cam.distance = was;
            cameraWas = null;
        }
    }

    /// <summary>To everyone with Snowcat: the party's members, and the server's players outside it.</summary>
    void Send(byte[] bytes, bool reliable)
    {
        var partyPeers = PartyPeers();
        foreach (var id in partyPeers) party!.Send(new SteamId(id), bytes, reliable ? Delivery.Reliable : Delivery.Unreliable);
        if (link is not null)
            foreach (var id in link.Players.Where(id => !partyPeers.Contains(id))) link.Send(id, bytes, reliable);
    }

    /// <summary>Admins only: puts the slopes back as the map had them, here and for everyone else with Snowcat.</summary>
    void ResetSlopes()
    {
        if (snow is null || adminPassword is null) return;
        var put = snow.ResetAll();
        if (Map() is { } m) SendEveryone(ResetPacket.Encode(m, adminPassword));
        Log($"reset the slopes: {put} patches put back, for everyone");
    }

    void MapChanged()
    {
        mapId = null;
        hellos.Clear();
        followCam?.Dispose();
        followCam = null;
        cameraWas = null;
        foreach (var r in remotes.Values) r.Body.Forget();
        remotes.Clear();
        GetOut(placeBeside: false);
        cat = null;
        bladeDown = false;
        tillerDown = false;
        tillerAmount = 0;
        bladeAmount = 0;
        (bladeAngle, bladeTilt) = (0, 0);
        body?.Forget();
        snow?.Forget();
        Rider.ResetLookup();
    }

    // ---- physics steps ----

    void PhysicsStep(nint scene)
    {
        lastStep.Restart();
        if ((!driving && riding is null) || target is not { } t || !physics.Holding) return;
        physics.Hold(scene, Hold.SeatAt(t.At, t.Velocity, Unity.Time - t.Time), t.Velocity);
    }

    void Prompt(string? text)
    {
        if (hud is null || prompt is null || text == shown) return;
        shown = text;
        if (text is null) hud.Hide();
        else
        {
            prompt.Text = text;
            hud.Show();
        }
    }

    public override void OnUnload()
    {
        active = false;
        foreach (var s in subscriptions) s.Dispose();
        subscriptions.Clear();
        try
        {
            MainThread.Run(() =>
            {
                GetOut(placeBeside: false);
                Camera(false);
                rider.Dispose();
                body?.Dispose();
                foreach (var r in remotes.Values) r.Body.Dispose();
                remotes.Clear();
                party?.Dispose();
                lobby?.Dispose();
                adminHud?.Dispose();
                snow?.Tick(force: true);
            });
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException) { }
    }
}
