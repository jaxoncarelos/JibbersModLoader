using Jibbers.Interop.Game;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Follow = Jibbers.Sdk.Game.Camera.Follow;

namespace PhoneCamera.Integration;

/// <summary>The settings the camera reads every frame.</summary>
internal sealed record RigOptions(bool FirstPerson, bool FollowInFirstPerson, bool PhoneMiddle, bool HeadFollows, bool Blur, float BlurStrength,
    float LookSpeed, bool InvertLook, string Quality, HoldOptions Hold, string? Hint);

/// <summary>
/// The phone camera: a camera of our own, held in front of your face, rendering into a texture shown
/// on the phone on screen (the game's camera is left alone). It follows the mate you pick (keeping
/// them framed by zooming with the distance), films you (selfie), or films where you look (first
/// person), and steps aside while the phone menu or a replay is open. Moved every frame from a
/// pre-hook on <c>FollowCam.LateUpdate</c>. Main thread only.
/// </summary>
internal sealed unsafe class CameraRig : IDisposable
{
    readonly Action<string> log;
    readonly Holding holding;
    readonly MotionBlur blur;
    readonly PhoneScreen screen = new();
    public readonly ClipRecorder Recorder;
    GameRef? camera, feed, gameCamera, target, aim, head;
    nint rider;
    (int W, int H) feedSize;
    string targetName = "";
    Quaternion rot = Quaternion.Identity;
    float fov, t, lookYaw, lookPitch, mouseProbe;
    bool fpOwnLook, fpProbed;
    Vector3 fpStart;
    DateTime? savedAt;
    int mates;

    public bool On { get; private set; }
    public bool Selfie { get; private set; }
    public bool Portrait { get; private set; }
    public float Zoom { get; private set; } = 1;
    public DateTime? RecordingSince { get; private set; }
    public bool Paused { get; set; }
    public bool InReplay { get; set; }
    public (float X, float Y, float R)? RecordButton => screen.RecordButton;

    public CameraRig(Action<string> log)
    {
        this.log = log;
        holding = new Holding(log);
        blur = new MotionBlur(log);
        Recorder = new ClipRecorder(log);
    }

    static bool Alive(GameRef? r) => r is not null && !Unity.IsNull(r.Target);

    /// <summary>Calls a no-argument method if this game build kept it (some engine methods are stripped).</summary>
    static void TryCall(nint o, string method)
    {
        var m = Il2Cpp.ClassOf(o).Method(method, 0);
        if (!m.IsNull) m.Invoke(o);
    }

    // ---- opening and closing ----

    public void Open(bool portrait)
    {
        if (On) return;
        On = true;
        Zoom = 1;
        Selfie = false;
        Portrait = portrait;
        t = 0;
        Recenter();
        fpOwnLook = fpProbed = false;
        mouseProbe = 0;
        rot = Quaternion.Identity;
        fov = 0; // snap to the first view rather than swinging into it
        PickMate(0);
        log("camera app open");
    }

    public void Close()
    {
        if (!On) return;
        On = false;
        if (RecordingSince is not null) StopRecording();
        StepAside();
        log("camera app closed");
    }

    /// <summary>Stops filming and hides the phone; the pose, blur and game camera go back.</summary>
    void StepAside()
    {
        if (Alive(camera)) UnityProperties.Set(camera!.Target, "set_enabled", false);
        holding.Release();
        blur.Set(false, 0, Alive(camera) ? camera!.Target : 0);
        screen.Hide();
        gameCamera?.Dispose();
        gameCamera = null;
    }

    public void Recenter() => (lookYaw, lookPitch) = (0f, 0f);

    // ---- presses ----

    public void NextMate(int step) { if (On) PickMate(step); }
    public void ZoomStep(int step) { if (On) Zoom = CamMath.Zoom(Zoom, step); }
    public void ToggleSelfie() { if (On) { Selfie = !Selfie; Recenter(); } }

    public void Rotate()
    {
        if (!On) return;
        if (RecordingSince is not null) { log("stop recording to turn the phone"); return; }
        Portrait = !Portrait;
        log(Portrait ? "phone upright (portrait)" : "phone sideways (landscape)");
    }

    public void ToggleRecording()
    {
        if (!On) return;
        if (RecordingSince is not null) { StopRecording(); log("recording stopped"); return; }
        try
        {
            var (w, h) = Alive(feed) ? feedSize : (0, 0);
            if (w == 0) return;
            var path = Recorder.Start(w, h);
            RecordingSince = DateTime.UtcNow;
            savedAt = null;
            log($"recording to {path}");
        }
        catch (InvalidOperationException e)
        {
            log($"couldn't start recording: {e.Message}");
        }
    }

    void StopRecording()
    {
        Recorder.Stop();
        RecordingSince = null;
        savedAt = DateTime.UtcNow;
    }

    // ---- mates ----

    /// <summary>Other players' characters: the roots of their "shell" ragdoll parts.</summary>
    static List<nint> ShellRoots()
    {
        var roots = new List<nint>();
        foreach (var part in RagdollPart.Live())
        {
            if (part.IsNull || Unity.IsNull(part.Ptr) || !part.IsShell) continue;
            nint clone = 0, below = Unity.Transform(part.Ptr);
            for (var tr = below; tr != 0 && !Unity.IsNull(tr);)
            {
                if (Unity.Name(tr).EndsWith("(Clone)")) clone = tr;
                var parent = Unity.Call(tr, "get_parent");
                if (parent != 0) below = tr;
                tr = parent;
            }
            var root = clone != 0 ? clone : below;
            if (!roots.Contains(root)) roots.Add(root);
        }
        return roots;
    }

    static nint Bone(nint root, params string[] names)
    {
        var bones = Unity.GetComponentsInChildren(root, Unity.Class("UnityEngine.Transform"), includeInactive: true)
            .Select(b => (b, Unity.Name(b).ToLowerInvariant())).ToList();
        foreach (var n in names)
            foreach (var (b, name) in bones)
                if (name.EndsWith(n, StringComparison.Ordinal)) return b;
        return 0;
    }

    static string? Nametag(nint root)
    {
        foreach (var t in Unity.GetComponentsInChildren(root, Unity.Class("TMPro.TMP_Text"), includeInactive: true))
        {
            var s = Il2Cpp.ReadString(Unity.Call(t, "get_text")).Trim();
            if (s.Length > 0) return s;
        }
        return null;
    }

    /// <summary>Films the next (+1) / previous (-1) mate, or the nearest (0).</summary>
    void PickMate(int step)
    {
        if (step != 0) Recenter();
        var me = Holding.Rider() is { } r ? Unity.Position(r.Ptr) : new Vector3(0, 0, 0);
        var roots = ShellRoots().Select(x => (Root: x, D: CamMath.Length(Unity.Position(x) - me))).OrderBy(x => x.D).ToList();
        var current = Alive(target) ? target!.Target : 0;
        var i = roots.FindIndex(x => x.Root == current);
        i = step == 0 || i < 0 ? 0 : ((i + step) % Math.Max(roots.Count, 1) + roots.Count) % Math.Max(roots.Count, 1);
        target?.Dispose();
        aim?.Dispose();
        target = aim = null;
        targetName = "";
        mates = roots.Count;
        if (i < roots.Count)
        {
            var root = roots[i].Root;
            var bone = Bone(root, "spine2", "spine1", "spine", "hips");
            target = new GameRef(root);
            aim = new GameRef(bone != 0 ? bone : root);
            targetName = Nametag(root) is { Length: > 0 } n ? n : $"Rider {i + 1}";
        }
    }

    // ---- the camera ----

    nint GameCamera(Follow? follow)
    {
        if (Alive(gameCamera)) return gameCamera!.Target;
        var main = Unity.MainCamera;
        if (main == 0 || Unity.IsNull(main))
            main = follow is { } f ? Unity.Call(f.Ptr, "GetComponentInChildren", Unity.Class("UnityEngine.Camera").SystemType) : 0;
        if (main == 0 || Unity.IsNull(main) || (Alive(camera) && main == camera!.Target)) return 0;
        gameCamera = new GameRef(main);
        return main;
    }

    /// <summary>The phone's screen texture, made again when its shape or quality changes (not mid-clip).</summary>
    nint Feed(string quality, nint cam)
    {
        var want = CamMath.FeedSize(quality, Portrait);
        if (Alive(feed) && (RecordingSince is not null || feedSize == want)) return feed!.Target;
        if (Alive(feed)) TryCall(feed!.Target, "Release");
        feed?.Dispose();
        var k = Unity.Class("UnityEngine.RenderTexture");
        var rt = k.New();
        int w = want.W, h = want.H, depth = 24;
        k.Method(".ctor", "System.Int32", "System.Int32", "System.Int32").Invoke(rt, (nint)(&w), (nint)(&h), (nint)(&depth));
        TryCall(rt, "Create");
        Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", rt);
        feed = new GameRef(rt);
        feedSize = want;
        if (cam != 0)
        {
            Unity.Call(cam, "set_targetTexture", rt);
            if (!Il2Cpp.ClassOf(cam).Method("ResetAspect", 0).IsNull) TryCall(cam, "ResetAspect");
        }
        return rt;
    }

    /// <summary>
    /// The game camera's look on ours: <c>Camera.CopyFrom</c> where this build kept it, else the
    /// settings that matter one by one (what it draws, how it clears, how far it sees).
    /// </summary>
    static void CopyLook(nint from, nint to)
    {
        var k = Il2Cpp.ClassOf(to);
        var copy = k.Method("CopyFrom", 1);
        if (!copy.IsNull)
        {
            copy.Invoke(to, from);
            return;
        }
        void Same<T>(string name) where T : unmanaged
        {
            var get = k.Method("get_" + name, 0);
            if (get.IsNull || k.Method("set_" + name, 1).IsNull) return;
            UnityProperties.Set(to, "set_" + name, get.Invoke<T>(from));
        }
        Same<int>("cullingMask");
        Same<int>("clearFlags");
        Same<Color>("backgroundColor");
        Same<float>("farClipPlane");
        Same<float>("fieldOfView");
        Same<bool>("allowHDR");
        Same<bool>("useOcclusionCulling");
    }

    /// <summary>Our camera, made once (a copy of the game camera's settings) and kept across maps.</summary>
    nint Camera(nint game, string quality)
    {
        if (Alive(camera)) return camera!.Target;
        using var go = OwnedGameObject.Create("Mod_PhoneCamera");
        Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", go.Target);
        var cam = go.AddComponent("UnityEngine.Camera");
        CopyLook(game, cam);
        UnityProperties.Set(cam, "set_nearClipPlane", 0.05f);
        UnityProperties.Set(cam, "set_depth", Unity.Call<float>(game, "get_depth") - 1);
        UnityProperties.Set(cam, "set_enabled", false);
        camera = new GameRef(cam);
        feedSize = default; // point it at a screen texture
        Feed(quality, cam);
        log("phone camera made");
        // The GameObject stays (DontDestroyOnLoad) with the camera: don't destroy it with `go`.
        go.AttachTo(0);
        return cam;
    }

    /// <summary>Every frame (the FollowCam.LateUpdate pre-hook).</summary>
    public void Frame(Follow? follow, RigOptions o, (float X, float Y) mouse, (float X, float Y) stick, bool blink)
    {
        if (!On) return;
        // The phone menu or a replay is open: the game's camera shows.
        if (Paused || InReplay)
        {
            StepAside();
            return;
        }
        var game = GameCamera(follow);
        if (game == 0) throw new InvalidOperationException("no game camera");
        var cam = Camera(game, o.Quality);
        if (Feed(o.Quality, cam) is var rt && rt == 0) return;

        // Where the phone is: in front of your face.
        if (rider == 0 || Unity.IsNull(rider))
        {
            if (Holding.Rider() is not { } r) throw new InvalidOperationException("you're not riding");
            rider = r.Ptr;
            head?.Dispose();
            var bone = Bone(CharacterRootOf(r.Ptr), "_head", "head");
            head = bone != 0 ? new GameRef(bone) : null;
        }
        var me = new RagdollController(rider);
        var headAt = Alive(head) ? Unity.Position(head!.Target) : Unity.Position(rider) + new Vector3(0, 1.5f, 0);
        var gameTr = Unity.Transform(game);
        var gameFwd = CamMath.Rotate(Unity.Call<Quaternion>(gameTr, "get_rotation"), new(0, 0, 1));
        var flatFwd = CamMath.Normalize(new(gameFwd.X, 0, gameFwd.Z));

        try { holding.Hold(me, follow, o.Hold with { FirstPerson = o.FirstPerson }); }
        catch (InvalidOperationException e) { Note($"couldn't hold the phone up: {e.Message}"); }
        blur.Set(o.Blur, o.BlurStrength, cam);

        var dt = Math.Clamp(Unity.DeltaTime, 0, 0.1f);
        var sens = o.LookSpeed;
        var fovScale = MathF.Min(MathF.Max(fov, 10) / 60, 1); // slower when zoomed in
        var up = o.InvertLook ? -1 : 1;
        void Look()
        {
            lookYaw = ((lookYaw + (mouse.X * 0.08f + stick.X * 120 * dt) * sens * fovScale) % 360 + 360) % 360;
            lookPitch = Math.Clamp(lookPitch + (mouse.Y * 0.08f + stick.Y * 90 * dt) * sens * fovScale * up, -80, 80);
        }

        Vector3 at, dir;
        Vector3? targetAt;
        var mateAt = !Selfie && Alive(aim) ? Unity.Position(aim!.Target) + new Vector3(0, 0.25f, 0) : (Vector3?)null;
        if (!Selfie && target is not null && mateAt is null) PickMate(0); // they left
        if (o.FirstPerson)
        {
            // Held out in front of your eyes: following your mate, or filming where you look. The
            // game's POV camera may not turn with the mouse; if it doesn't, the phone turns itself.
            var eye = Unity.Position(gameTr);
            if (!fpOwnLook && !fpProbed)
            {
                if (mouseProbe == 0) fpStart = gameFwd;
                mouseProbe += MathF.Abs(mouse.X) + MathF.Abs(stick.X) * 20;
                if (mouseProbe > 400)
                {
                    fpProbed = true;
                    var turned = MathF.Acos(Math.Clamp(gameFwd.X * fpStart.X + gameFwd.Z * fpStart.Z, -1, 1)) * 180 / MathF.PI;
                    if (turned < 3)
                    {
                        fpOwnLook = true;
                        log("the game's first-person view doesn't turn with the mouse: the phone turns on its own");
                    }
                }
            }
            var mate = o.FollowInFirstPerson ? mateAt : null;
            if (Selfie)
            {
                at = eye + flatFwd * 0.8f + new Vector3(0, 0.05f, 0);
                (dir, targetAt) = (headAt - at, headAt);
            }
            else if (mate is { } m)
            {
                Look();
                at = eye + CamMath.Normalize(m - eye) * 0.5f;
                (dir, targetAt) = (CamMath.Turn(m - at, lookYaw, lookPitch), m);
            }
            else
            {
                var fwd = gameFwd;
                if (fpOwnLook)
                {
                    Look();
                    fwd = CamMath.Turn(gameFwd, lookYaw, lookPitch);
                }
                (at, dir, targetAt) = (eye + CamMath.Normalize(fwd) * 0.5f, fwd, null);
            }
        }
        else
        {
            targetAt = Selfie ? headAt : mateAt;
            if (Selfie)
                at = headAt + flatFwd * 0.75f + new Vector3(0, 0.12f, 0);
            else
            {
                var toward = targetAt is { } tg ? CamMath.Normalize(new(tg.X - headAt.X, 0, tg.Z - headAt.Z)) : flatFwd;
                var right = CamMath.Normalize(CamMath.Cross(new(0, 1, 0), toward));
                at = headAt + toward * 0.45f + right * 0.12f + new Vector3(0, -0.08f, 0);
            }
            dir = targetAt is { } p ? p - at : gameFwd;
            Look(); // the mouse and the right stick turn the phone off its subject
            dir = CamMath.Turn(dir, lookYaw, lookPitch);
        }

        // Your rider looks where the phone films (into the phone, for a selfie).
        try { holding.LookAt(o.HeadFollows ? (Selfie ? at : at + CamMath.Normalize(dir) * 8) : null); }
        catch (Il2CppException e) { Note($"couldn't turn your rider: {e.Message}"); }

        t += dt;
        // A little handheld sway.
        var sway = new Vector3(MathF.Sin(t * 1.3f) * 0.004f, MathF.Cos(t * 0.9f) * 0.004f, 0);
        var want = CamMath.LookRotation(CamMath.Normalize(dir) + CamMath.Rotate(CamMath.LookRotation(dir), sway));
        var first = fov == 0;
        rot = first ? want : CamMath.Nlerp(rot, want, 1 - MathF.Exp(-dt * 7));
        var dist = targetAt is { } ta ? CamMath.Length(ta - at) : 10f;
        var fovWant = CamMath.FieldOfView(Selfie, targetAt is null || Selfie ? null : dist, Zoom, Portrait);
        fov = first ? fovWant : fov + (fovWant - fov) * (1 - MathF.Exp(-dt * 4));

        var tr = Unity.Transform(cam);
        var (pos, q) = (at, rot);
        Unity.Call(tr, "SetPositionAndRotation", (nint)(&pos), (nint)(&q));
        UnityProperties.Set(cam, "set_fieldOfView", fov);
        UnityProperties.Set(cam, "set_enabled", true);
        if (RecordingSince is not null)
        {
            try { Recorder.Capture(feed!.Target); }
            catch (InvalidOperationException e) { Note($"recording: {e.Message}"); }
        }

        var who = Selfie ? "Selfie"
            : targetName.Length > 0 && targetAt is not null ? $"{targetName} • {MathF.Round(dist)} m"
            : o.FirstPerson ? "First person" : "No mates around";
        var saved = savedAt is { } s && (DateTime.UtcNow - s).TotalSeconds < 4;
        var rec = RecordingSince is { } since ? (int)(DateTime.UtcNow - since).TotalSeconds : (int?)null;
        screen.Show(new PhoneView(Selfie, o.FirstPerson || o.PhoneMiddle, Portrait, who, Zoom, rec, saved, o.Hint, blink), feed!.Target);
    }

    static nint CharacterRootOf(nint component)
    {
        nint clone = 0, below = Unity.Transform(component);
        for (var tr = below; tr != 0 && !Unity.IsNull(tr);)
        {
            if (Unity.Name(tr).EndsWith("(Clone)")) clone = tr;
            var parent = Unity.Call(tr, "get_parent");
            if (parent != 0) below = tr;
            tr = parent;
        }
        return clone != 0 ? clone : below;
    }

    /// <summary>A respawn or a new map: find your rider again.</summary>
    public void Respawned()
    {
        rider = 0;
        head?.Dispose();
        head = null;
    }

    string lastNote = "";
    void Note(string message)
    {
        if (message == lastNote) return;
        lastNote = message;
        log($"camera: {message}");
    }

    public void Dispose()
    {
        On = false;
        Recorder.Stop();
        Cleanup.Run(StepAside, holding.Dispose, blur.Dispose, screen.Dispose,
            () => { if (Alive(camera)) Unity.Destroy(Unity.Call(camera!.Target, "get_gameObject")); },
            () => { if (Alive(feed)) TryCall(feed!.Target, "Release"); },
            () => camera?.Dispose(), () => feed?.Dispose(), () => target?.Dispose(), () => aim?.Dispose(), () => head?.Dispose());
        camera = feed = target = aim = head = null;
    }
}
