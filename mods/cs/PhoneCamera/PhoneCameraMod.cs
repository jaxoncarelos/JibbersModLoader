using Jibbers.Sdk;
using Jibbers.Sdk.Game.Event;
using Jibbers.Sdk.Interop;
using PhoneCamera.Integration;
using Follow = Jibbers.Sdk.Game.Camera.Follow;

namespace PhoneCamera;

/// <summary>
/// Phone Camera: a Camera app on the phone. Press Camera on the phone (or K) and you film through your
/// phone, held up in front of your face, its picture on a phone on screen: it follows the mate you pick
/// (keeping them framed), looks around with the mouse or right stick, zooms (0.1x to 8x), flips to a
/// selfie or upright (portrait), goes first person, and records what it films to MP4 clips in
/// Videos\Jibbers Clips. While filming your rider holds the phone up (your own saved pose if you have
/// one) and can be hidden.
/// </summary>
public sealed unsafe class PhoneCameraMod : JibbersMod
{
    public override string Id => "phone_camera";
    public override string Name => "Phone Camera";
    public override string Author => "scotttucker1599";
    public override string Description => "Film your mates through your phone: follow cam, zoom, selfie, portrait, first person, and MP4 clips.";

    // Short labels: the Mods tab is phone-sized, and long ones wrap over each other.
    [Toggle("My HoldPhonecamera pose", true, Section = "Camera")] public bool MyPose { get; private set; }
    [Toggle("Hide my rider", true, Section = "Camera")] public bool HideSelf { get; private set; }
    [Toggle("First person", false, Section = "Camera")] public bool FirstPerson { get; private set; }
    [Toggle("First person follows mates", true, Section = "Camera")] public bool FpFollow { get; private set; }
    [Choice("Clip quality", "720p", "540p", "720p", "1080p", "1440p", Section = "Camera")] public string Quality { get; private set; } = "720p";
    [Toggle("Start in portrait", false, Section = "Camera")] public bool Portrait { get; private set; }
    [Toggle("Phone in the middle", false, Section = "Camera")] public bool PhoneMiddle { get; private set; }
    [Toggle("Head follows camera", false, Section = "Camera")] public bool HeadFollows { get; private set; }
    [Toggle("Blur around phone", false, Section = "Camera")] public bool Blur { get; private set; }
    [Slider("Blur strength", 0.6, 0.1, 1.0, 0.1, Section = "Camera")] public double BlurStrength { get; private set; }
    [Slider("Look speed", 1.0, 0.1, 3.0, 0.1, Section = "Camera")] public double LookSpeed { get; private set; }
    [Toggle("Invert look", false, Section = "Camera")] public bool InvertLook { get; private set; }
    [Toggle("Controls hint", true, Section = "Screen")] public bool ControlsHint { get; private set; }
    [Toggle("Controller", true, Section = "Keys")] public bool Controller { get; private set; }
    [KeyBinding("Open / close", Key.K, Section = "Keys")] public Key OpenKey { get; private set; }
    [KeyBinding("Next mate", Key.E, Section = "Keys")] public Key NextKey { get; private set; }
    [KeyBinding("Previous mate", Key.Q, Section = "Keys")] public Key PrevKey { get; private set; }
    [KeyBinding("Zoom in", Key.C, Section = "Keys")] public Key ZoomInKey { get; private set; }
    [KeyBinding("Zoom out", Key.Z, Section = "Keys")] public Key ZoomOutKey { get; private set; }
    [KeyBinding("Selfie", Key.F, Section = "Keys")] public Key SelfieKey { get; private set; }
    [KeyBinding("Record", Key.T, Section = "Keys")] public Key RecordKey { get; private set; }
    [KeyBinding("Portrait / landscape", Key.B, Section = "Keys")] public Key RotateKey { get; private set; }
    [KeyBinding("Re-aim at mate", Key.R, Section = "Keys")] public Key RecenterKey { get; private set; }
    [KeyBinding("Close camera", Key.Escape, Section = "Keys")] public Key CloseKey { get; private set; }

    /// <summary>The saved pose used for filming (named in the game's pose editor).</summary>
    const string PoseName = "HoldPhonecamera";

    CameraRig? rig;
    Pads? pads;
    readonly bool[] was = new bool[11];
    DateTime nextButtonCheck, escClosed;
    string lastButton = "";
    int frames;
    bool noAxes;

    public override void OnLoad()
    {
        rig = new CameraRig(Log);
        pads = new Pads(Log);
        Follow.LateUpdateMethod.Before((self, _) => Frame(self));
        Pause.Subscribe(_ =>
        {
            if (rig is null) return;
            rig.Paused = true;
            // Esc closed the camera and the game opened its pause menu (the phone) on the same press: put it away.
            if ((DateTime.UtcNow - escClosed).TotalSeconds < 1.5)
            {
                escClosed = default;
                MainThread.Later(() =>
                {
                    try { PhoneButton.Resume(); }
                    catch (Exception e) when (e is InvalidOperationException or Il2CppException) { Log($"couldn't put the phone away after Esc: {e.Message}"); }
                });
            }
        });
        Unpause.Subscribe(_ => { if (rig is not null) rig.Paused = false; });
        EnterReplay.Subscribe(_ => { if (rig is not null) rig.InReplay = true; });
        ExitReplay.Subscribe(_ => { if (rig is not null) rig.InReplay = false; });
        PlayerSpawn.Subscribe(_ => rig?.Respawned());
        Respawn.Subscribe(_ => rig?.Respawned());
        Log($"Phone Camera ready: clips go to {ClipRecorder.Folder}");
    }

    public override void OnUnload()
    {
        var r = rig;
        rig = null;
        if (r is not null) MainThread.Run(r.Dispose);
    }

    /// <summary>How far the mouse moved this frame (about pixels, up +y): the legacy "Mouse X/Y" axes (raw, so they work with the cursor locked), else the cursor's movement.</summary>
    (float, float) MouseDelta()
    {
        var input = Il2Cpp.RequireClass("UnityEngine.InputLegacyModule.dll", "UnityEngine.Input");
        if (!noAxes)
        {
            try
            {
                var axis = input.Method("GetAxisRaw", "System.String");
                float x = axis.Invoke<float>(0, Il2Cpp.String("Mouse X")), y = axis.Invoke<float>(0, Il2Cpp.String("Mouse Y"));
                if (x != 0 || y != 0) return (x * 10, y * 10);
            }
            catch (Il2CppException)
            {
                noAxes = true; // not set up in this game: it throws (and logs) on every call
            }
        }
        var d = input.Method("get_mousePositionDelta", 0);
        if (d.IsNull) return (0, 0);
        var v = d.Invoke<Vector3>(0);
        return (v.X, v.Y);
    }

    string Hint()
    {
        static string K(Key k) => k == Key.Escape ? "Esc" : k.ToString();
        return $"Mouse / right stick: look ({K(RecenterKey)} re-aims) • {K(PrevKey)}/{K(NextKey)} or LB/RB: mate • {K(ZoomOutKey)}/{K(ZoomInKey)} or D-pad: zoom • "
            + $"{K(SelfieKey)} or Y: selfie • {K(RotateKey)} or D-pad right: portrait • click ●, {K(RecordKey)} or X: record • {K(CloseKey)} or Back: close";
    }

    bool OnRecordButton()
    {
        if (rig?.RecordButton is not { } b) return false;
        // The mouse in Unity's screen space (pixels from the window's bottom left), as the button is.
        var position = Il2Cpp.RequireClass("UnityEngine.InputLegacyModule.dll", "UnityEngine.Input").Method("get_mousePosition", 0);
        if (position.IsNull) return false;
        var v = position.Invoke<Vector3>(0);
        return (v.X - b.X) * (v.X - b.X) + (v.Y - b.Y) * (v.Y - b.Y) <= b.R * 1.3f * (b.R * 1.3f);
    }

    void Frame(nint follow)
    {
        if (rig is null) return;
        var now = DateTime.UtcNow;
        // Each map builds a new phone: add the button whenever the current one lacks it.
        if (now >= nextButtonCheck)
        {
            nextButtonCheck = now.AddSeconds(1);
            try
            {
                var outcome = PhoneButton.Ensure(() => rig?.Open(Portrait));
                if (outcome != lastButton) Log(lastButton = outcome);
            }
            catch (Exception e) when (e is InvalidOperationException or Il2CppException or MissingMethodException or MissingMemberException)
            {
                var outcome = $"couldn't add the button: {e.Message}";
                if (outcome != lastButton) Log(lastButton = outcome);
            }
        }

        var focused = Input.GameFocused && !Input.Typing;
        var (buttons, stickX, stickY) = Controller && focused ? pads!.Read() : (PadButton.None, 0f, 0f);
        bool Pad(PadButton b) => (buttons & b) != 0;
        bool KeyDown(Key k) => focused && Input.IsDown(k);
        var held = new[]
        {
            KeyDown(OpenKey),
            KeyDown(NextKey) || Pad(PadButton.RightShoulder),
            KeyDown(PrevKey) || Pad(PadButton.LeftShoulder),
            KeyDown(ZoomInKey) || Pad(PadButton.DpadUp),
            KeyDown(ZoomOutKey) || Pad(PadButton.DpadDown),
            KeyDown(SelfieKey) || Pad(PadButton.Y),
            KeyDown(RecordKey) || Pad(PadButton.X) || (KeyDown(Key.MouseLeft) && OnRecordButton()),
            KeyDown(CloseKey) || Pad(PadButton.Back),
            KeyDown(RecenterKey) || KeyDown(Key.MouseMiddle) || Pad(PadButton.RightStick),
            KeyDown(RotateKey) || Pad(PadButton.DpadRight),
        };
        for (var i = 0; i < held.Length; i++)
        {
            var pressed = held[i] && !was[i];
            was[i] = held[i];
            if (!pressed) continue;
            switch (i)
            {
                case 0: if (rig.On) rig.Close(); else rig.Open(Portrait); break;
                case 1: rig.NextMate(1); break;
                case 2: rig.NextMate(-1); break;
                case 3: rig.ZoomStep(1); break;
                case 4: rig.ZoomStep(-1); break;
                case 5: rig.ToggleSelfie(); break;
                case 6: rig.ToggleRecording(); break;
                case 7:
                    if (!rig.On) break;
                    if (CloseKey == Key.Escape && KeyDown(Key.Escape)) escClosed = now;
                    rig.Close();
                    break;
                case 8: rig.Recenter(); break;
                case 9: rig.Rotate(); break;
            }
        }
        if (!rig.On) return;

        var mouse = focused ? MouseDelta() : (0f, 0f);
        var options = new RigOptions(FirstPerson, FpFollow, PhoneMiddle, HeadFollows, Blur, (float)BlurStrength, (float)LookSpeed, InvertLook, Quality,
            new HoldOptions(MyPose, PoseName, HideSelf, FirstPerson), ControlsHint ? Hint() : null);
        frames++;
        try
        {
            rig.Frame(follow == 0 ? null : new Follow(follow), options, mouse, (stickX, stickY), frames / 15 % 2 == 0);
            lastProblem = "";
        }
        catch (Exception e) when (e is InvalidOperationException or Il2CppException or MissingMethodException or MissingMemberException)
        {
            // Logged once until it changes (e.g. "you're not riding" on the menu).
            // Where in this mod it happened: the first stack frame of ours.
            var where = e.StackTrace?.Split(Environment.NewLine).Select(l => l.Trim()).FirstOrDefault(l => l.Contains("PhoneCamera.")) ?? "";
            var problem = $"{e.Message} {where}".Trim();
            if (problem != lastProblem) Log($"camera: {lastProblem = problem}");
        }
    }

    string lastProblem = "";
}
