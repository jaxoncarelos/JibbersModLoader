using Jibbers.Interop.Game;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Follow = Jibbers.Sdk.Game.Camera.Follow;

namespace PhoneCamera.Integration;

/// <summary>What the rider does while the camera app is open (from the settings).</summary>
internal readonly record struct HoldOptions(bool MyPose, string PoseName, bool Hide, bool FirstPerson);

/// <summary>
/// Your rider while filming: holding the phone up (your saved pose, or the game's own hold-phone
/// pose, in the poser's <c>overridePose</c>), the phone's model in the hand, hidden (body and the
/// separately simulated "Cloth" outfit), looking where the phone points (the poser's
/// <c>lookAtTarget</c>), and first person (the follow camera's own POV mode). Everything is put back
/// on <see cref="Release"/>. Main thread only.
/// </summary>
internal sealed unsafe class Holding : IDisposable
{
    readonly Action<string> log;
    GameRef? poser, oldPose, setPose, myPose, phoneModel, follow, oldLook, cloth;
    OwnedGameObject? look;
    bool lookSet;
    (bool Pov, float Yaw, float Pitch)? oldPov;
    readonly List<(GameRef Renderer, bool Was)> hidden = new();
    string? poseError;

    public Holding(Action<string> log) => this.log = log;

    static bool Alive(GameRef? r) => r is not null && !Unity.IsNull(r.Target);

    /// <summary>Your rider: the one RagdollController (other players' riders are puppets).</summary>
    public static RagdollController? Rider()
    {
        foreach (var r in RagdollController.Live())
            if (!r.IsNull && !Unity.IsNull(r.Ptr)) return r;
        return null;
    }

    static nint FieldOfType(nint o, string type)
    {
        for (var k = Il2Cpp.ClassOf(o); !k.IsNull; k = k.Parent)
            foreach (var f in k.Fields)
                if (!f.IsStatic && f.TypeName == type)
                    return f.GetObject(o);
        return 0;
    }

    static RagdollPoser PoserOf(RagdollController rider)
    {
        var p = FieldOfType(rider.Ptr, "RagdollPoser");
        if (p == 0 || Unity.IsNull(p)) throw new InvalidOperationException("the rider has no poser");
        return new RagdollPoser(p);
    }

    /// <summary>The instantiated character the rider belongs to (topmost "(Clone)" ancestor).</summary>
    static nint CharacterRoot(nint component)
    {
        nint clone = 0, below = Unity.Transform(component);
        for (var t = below; t != 0 && !Unity.IsNull(t); )
        {
            if (Unity.Name(t).EndsWith("(Clone)")) clone = t;
            var parent = Unity.Call(t, "get_parent");
            if (parent != 0) below = t;
            t = parent;
        }
        return clone != 0 ? clone : below;
    }

    // ---- the pose ----

    static void FillList(nint list, IEnumerator<float> values, ref int filled)
    {
        if (list == 0) return;
        var k = Il2Cpp.ClassOf(list);
        var count = k.Method("get_Count", 0).Invoke<int>(list);
        var set = k.Method("set_Item", 2);
        for (var i = 0; i < count && values.MoveNext(); i++, filled++)
        {
            int index = i;
            float v = Math.Clamp(values.Current, 0, 1);
            set.Invoke(list, (nint)(&index), (nint)(&v));
        }
    }

    /// <summary>Your saved pose: a copy of the game's hold-phone pose (so it blends the same way) with your angles. Made once.</summary>
    RagdollPose? MyPose(RagdollPose gamePose, string name)
    {
        if (Alive(myPose)) return new RagdollPose(myPose!.Target);
        if (poseError is not null) return null;
        try
        {
            var values = PoseFile.Read(name);
            var copy = Unity.Instantiate(gamePose.Ptr);
            if (copy == 0) throw new InvalidOperationException("couldn't copy the pose");
            Unity.Call(copy, "set_name", Il2Cpp.String(name));
            Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", copy);
            var pose = new RagdollPose(copy);
            // Joint by joint through the bends, then the spreads, then the twists.
            using var e = ((IEnumerable<float>)values).GetEnumerator();
            var filled = 0;
            FillList(pose.extensions, e, ref filled);
            FillList(pose.abductions, e, ref filled);
            FillList(pose.twists, e, ref filled);
            myPose?.Dispose();
            myPose = new GameRef(copy);
            log($"your {name} pose loaded ({filled} angles)");
            return pose;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Il2CppException)
        {
            poseError = ex.Message;
            log($"using the game's phone pose: {ex.Message}");
            return null;
        }
    }

    void PoseUp(RagdollController rider, HoldOptions o)
    {
        var p = Alive(poser) ? new RagdollPoser(poser!.Target) : PoserOf(rider);
        var gamePose = p.holdPhonePose;
        if (gamePose.IsNull) throw new InvalidOperationException("the rider has no hold-phone pose");
        var want = (o.MyPose ? MyPose(gamePose, o.PoseName) : null) ?? gamePose;
        if (!Alive(poser))
        {
            var old = p.overridePose;
            oldPose?.Dispose();
            oldPose = old.IsNull || Unity.IsNull(old.Ptr) ? null : new GameRef(old.Ptr);
            p.overridePose = want;
            p.overridePoseTime = 0;
            poser = new GameRef(p.Ptr);
            setPose?.Dispose();
            setPose = new GameRef(want.Ptr);
            log($"holding the phone up ({Unity.Name(want.Ptr)})");
        }
        else if (setPose?.Target != want.Ptr && p.overridePose.Ptr == setPose?.Target)
        {
            // The setting changed while filming, and the slot is still ours.
            p.overridePose = want;
            setPose?.Dispose();
            setPose = new GameRef(want.Ptr);
        }
    }

    // ---- the phone in the hand ----

    /// <summary>
    /// Shows the phone's model only (the Phone's GameObject field). Not <c>Phone.SetActive</c>: that
    /// tells the game the phone is out, and its camera zooms in on it like the pause menu.
    /// </summary>
    void PhoneOut()
    {
        if (Alive(phoneModel)) return;
        foreach (var ph in Phone.Live())
        {
            if (ph.IsNull || Unity.IsNull(ph.Ptr) || ph.IsShell) continue;
            var model = FieldOfType(ph.Ptr, "UnityEngine.GameObject");
            if (model == 0 || Unity.IsNull(model)) return;
            UnityProperties.Set(model, "SetActive", true);
            phoneModel = new GameRef(model);
            return;
        }
    }

    // ---- first person ----

    void Pov(Follow? cam, bool on)
    {
        if (on && cam is { } f && oldPov is null)
        {
            var raw = new FollowCam(f.Ptr);
            oldPov = (raw.enablePov, raw.lookMaxYaw, raw.lookMaxPitch);
            raw.enablePov = true;
            raw.lookMaxYaw = 180;
            raw.lookMaxPitch = 85;
            follow?.Dispose();
            follow = new GameRef(f.Ptr);
            log("first person on");
        }
        else if (!on && oldPov is not null)
            PovBack();
    }

    void PovBack()
    {
        if (Alive(follow) && oldPov is { } o)
        {
            var raw = new FollowCam(follow!.Target);
            raw.enablePov = o.Pov;
            raw.lookMaxYaw = o.Yaw;
            raw.lookMaxPitch = o.Pitch;
            log("first person off");
        }
        oldPov = null;
        follow?.Dispose();
        follow = null;
    }

    // ---- hiding ----

    /// <summary>
    /// Your outfit: one "Cloth" mesh of its own (simulated cloth, under the scene's Systems), not under
    /// the character. Found once as the Cloth sitting on you (its bounds drift with the simulation).
    /// </summary>
    nint Cloth(Vector3 me)
    {
        if (Alive(cloth)) return cloth!.Target;
        foreach (var r in Unity.FindObjects(Unity.Class("UnityEngine.Renderer")))
        {
            if (Unity.IsNull(r) || Unity.Name(r) != "Cloth") continue;
            var b = Unity.Call<Bounds>(r, "get_bounds");
            var size = MathF.Max(b.Extents.X, MathF.Max(b.Extents.Y, b.Extents.Z));
            if (CamMath.Length(b.Center - me) < 3 && size < 4)
            {
                cloth = new GameRef(r);
                return r;
            }
        }
        return 0;
    }

    void HideRider(RagdollController rider)
    {
        if (hidden.Count > 0) return;
        var root = CharacterRoot(rider.Ptr);
        var parts = Unity.GetComponentsInChildren(root, Unity.Class("UnityEngine.Renderer")).Where(r => !Unity.IsNull(r)).ToList();
        // A character is tens of parts: hundreds means the "character" found holds other things too.
        if (parts.Count > 200) throw new InvalidOperationException($"'{Unity.Name(root)}' has {parts.Count} parts, not hiding it");
        var me = Unity.Position(rider.Ptr);
        if (parts.Count > 0)
        {
            var sum = new Vector3(0, 0, 0);
            foreach (var r in parts) sum = sum + Unity.Call<Bounds>(r, "get_bounds").Center;
            me = sum * (1f / parts.Count);
        }
        var outfit = Cloth(me);
        if (outfit != 0) parts.Add(outfit);
        foreach (var r in parts)
        {
            var was = Unity.Call<bool>(r, "get_enabled");
            UnityProperties.Set(r, "set_enabled", false);
            hidden.Add((new GameRef(r), was));
        }
        log($"hid your rider ({parts.Count - (outfit != 0 ? 1 : 0)} parts, {(outfit != 0 ? 1 : 0)} clothes)");
    }

    void ShowRider()
    {
        foreach (var (r, was) in hidden)
        {
            if (!Unity.IsNull(r.Target)) UnityProperties.Set(r.Target, "set_enabled", was);
            r.Dispose();
        }
        hidden.Clear();
    }

    // ---- looking ----

    /// <summary>Makes your rider look at <paramref name="at"/>, or back to the game's own look target (null).</summary>
    public void LookAt(Vector3? at)
    {
        if (!Alive(poser)) return;
        var p = new RagdollPoser(poser!.Target);
        if (at is not { } point)
        {
            if (lookSet)
            {
                if (look is not null && p.lookAtTarget.Ptr == Unity.Transform(look.Target))
                    p.lookAtTarget = new(Alive(oldLook) ? oldLook!.Target : 0);
                oldLook?.Dispose();
                oldLook = null;
                lookSet = false;
            }
            return;
        }
        if (look is null || Unity.IsNull(look.Target))
        {
            look?.Dispose();
            look = OwnedGameObject.Create("Mod_PhoneCamera_Look");
            Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", look.Target);
        }
        var tr = look.Transform;
        Unity.Call(tr, "set_position", (nint)(&point));
        if (p.lookAtTarget.Ptr != tr)
        {
            if (!lookSet)
            {
                var cur = p.lookAtTarget.Ptr;
                oldLook = cur == 0 || Unity.IsNull(cur) ? null : new GameRef(cur);
                lookSet = true;
            }
            p.lookAtTarget = new(tr);
        }
    }

    /// <summary>Holds the phone up (every frame while filming; cheap once done).</summary>
    public void Hold(RagdollController rider, Follow? cam, HoldOptions o)
    {
        PoseUp(rider, o);
        PhoneOut();
        Pov(cam, o.FirstPerson);
        if (o.Hide) HideRider(rider);
        else ShowRider();
    }

    /// <summary>Puts everything back (camera closed, the phone menu opened, or the mod unloading).</summary>
    public void Release()
    {
        ShowRider();
        LookAt(null);
        if (Alive(poser))
        {
            var p = new RagdollPoser(poser!.Target);
            // Only if the slot is still ours (the game may have set its own since).
            if (setPose is not null && p.overridePose.Ptr == setPose.Target)
                p.overridePose = new(Alive(oldPose) ? oldPose!.Target : 0);
        }
        if (Alive(phoneModel)) UnityProperties.Set(phoneModel!.Target, "SetActive", false);
        Cleanup.Run(() => poser?.Dispose(), () => oldPose?.Dispose(), () => setPose?.Dispose(), () => phoneModel?.Dispose());
        poser = oldPose = setPose = phoneModel = null;
        PovBack();
    }

    public void Dispose()
    {
        Release();
        Cleanup.Run(() => look?.Dispose(), () => myPose?.Dispose(), () => cloth?.Dispose());
        look = null;
        myPose = cloth = null;
    }
}

/// <summary>UnityEngine.Bounds: centre and extents.</summary>
internal record struct Bounds(Vector3 Center, Vector3 Extents);
