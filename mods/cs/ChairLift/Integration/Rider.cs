using Jibbers.Interop.Game;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;


namespace ChairLift.Integration;

/// <summary>
/// Your rider (the one <c>RagdollController</c>: other players are puppets) and what the ride does
/// to it: putting it on the seat, letting the phone open off the ground, sitting it down (a pose of
/// our own in the poser's <c>overridePose</c>), and turning its head straight ahead (the poser's
/// <c>lookAtTarget</c>, which otherwise follows the camera). Main thread only.
/// </summary>
internal sealed unsafe class Rider : IDisposable
{
    GameRef? sitting;
    OwnedGameObject? look;
    GameRef? previousLook;
    bool lookingAhead;

    public static RagdollController? Find()
    {
        foreach (var r in RagdollController.Live())
            if (!r.IsNull && !Unity.IsNull(r.Ptr))
                return r;
        return null;
    }

    /// <summary>Where the rider's body is (its root transform).</summary>
    public static Vector3 Centre(RagdollController r) => Unity.Position(r.Ptr);

    /// <summary>Puts the whole body at a position, facing and speed (in its default stance).</summary>
    public static void Place(RagdollController r, Vector3 at, Quaternion facing, Vector3 velocity) => r.daw(at, facing, velocity);

    /// <summary>The game won't open the phone off the ground otherwise.</summary>
    public static void AllowPhone(RagdollController r) => r.canOpenPhone = true;

    /// <summary>Where the other riders' bodies are (the game draws them as "shell" ragdoll parts).</summary>
    public static List<Vector3> Others()
    {
        var list = new List<Vector3>();
        foreach (var part in RagdollPart.Live())
            if (!part.IsNull && !Unity.IsNull(part.Ptr) && part.IsShell)
                list.Add(Unity.Position(part.Ptr));
        return list;
    }

    // ---- sitting ----

    static RagdollPose[] AllPoses()
    {
        var resources = Unity.Class("UnityEngine.Resources");
        var all = Unity.CallStatic(resources, "FindObjectsOfTypeAll", RagdollPose.Class.Class.SystemType);
        return Il2Cpp.ArrayObjects(all).Select(p => new RagdollPose(p)).ToArray();
    }

    /// <summary>Our sitting pose: a copy of the game's relaxed "Casual" pose (upright back, arms down) with the legs bent.</summary>
    RagdollPose? SittingPose(double hipPercent, double kneePercent)
    {
        if (sitting is null || Unity.IsNull(sitting.Target))
        {
            sitting?.Dispose();
            var poses = AllPoses();
            var source = poses.FirstOrDefault(p => Unity.Name(p.Ptr).Equals(SitPose.Base, StringComparison.OrdinalIgnoreCase));
            if (source.IsNull)
                source = poses.FirstOrDefault(p => Unity.Name(p.Ptr).Equals("Stand", StringComparison.OrdinalIgnoreCase));
            if (source.IsNull)
                return null;
            var copy = Unity.Instantiate(source.Ptr);
            if (copy == 0)
                return null;
            Unity.Call(copy, "set_name", Il2Cpp.String("ChairLiftSit"));
            sitting = new GameRef(copy);
            Log.Info("ChairLift", $"sitting with a copy of pose {Unity.Name(source.Ptr)}");
        }
        var pose = new RagdollPose(sitting.Target);
        // Bend the legs (the sliders can change while riding).
        var list = pose.extensions;
        if (list != 0)
        {
            var type = Il2Cpp.ClassOf(list);
            var count = type.Method("get_Count", 0).Invoke<int>(list);
            var set = type.Method("set_Item", 2);
            foreach (var (part, value) in SitPose.Legs(hipPercent, kneePercent))
            {
                if (part >= count)
                    continue;
                int index = part;
                float v = value;
                set.Invoke(list, (nint)(&index), (nint)(&v));
            }
        }
        return pose;
    }

    /// <summary>Sits the rider down (cheap to call again: it only re-bends the legs).</summary>
    public void Sit(RagdollController r, double hipPercent, double kneePercent)
    {
        var poser = r._ozk_k__BackingField;
        if (poser.IsNull || SittingPose(hipPercent, kneePercent) is not { } pose)
            return;
        if (poser.overridePose.Ptr != pose.Ptr)
        {
            poser.overridePose = pose;
            poser.overridePoseTime = 0.35f;
        }
    }

    /// <summary>Back to the rider's own posing and looking.</summary>
    public void Stand(RagdollController? r)
    {
        if (r is not { } rider || rider.IsNull || Unity.IsNull(rider.Ptr))
            return;
        var poser = rider._ozk_k__BackingField;
        if (poser.IsNull)
            return;
        if (sitting is not null && poser.overridePose.Ptr == sitting.Target)
        {
            poser.overridePose = default;
            poser.overridePoseTime = 0;
        }
        if (lookingAhead)
        {
            var previous = previousLook?.Target ?? 0;
            poser.lookAtTarget = new(previous != 0 && !Unity.IsNull(previous) ? previous : 0);
            previousLook?.Dispose();
            previousLook = null;
            lookingAhead = false;
        }
    }

    /// <summary>Makes the rider look at <paramref name="at"/> (straight ahead up the lift).</summary>
    public void LookAt(RagdollController r, Vector3 at)
    {
        var poser = r._ozk_k__BackingField;
        if (poser.IsNull)
            return;
        if (look is null || Unity.IsNull(look.Target))
        {
            look?.Dispose();
            look = OwnedGameObject.Create("ChairLiftLook");
        }
        var tr = look.Transform;
        Unity.Call(tr, "set_position", (nint)(&at));
        if (poser.lookAtTarget.Ptr != tr)
        {
            if (!lookingAhead)
            {
                var current = poser.lookAtTarget.Ptr;
                previousLook = current != 0 ? new GameRef(current) : null;
                lookingAhead = true;
            }
            poser.lookAtTarget = new(tr);
        }
    }

    public void Dispose()
    {
        Stand(Find());
        look?.Dispose();
        sitting?.Dispose();
    }
}
