using Jibbers.Interop.Game;
using Jibbers.Interop.Jibbers.MapTools;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace ChairLift.Integration;

/// <summary>
/// The map's lifts, as the game builds them: a <c>LiftLine</c> per lift, with a bottom and a top
/// <c>LiftStation</c> (in point, out point, wheel centre) and its carriers in the obfuscated
/// <c>List&lt;Transform&gt;</c> <c>nkm</c>, which the lift moves along the cable in <c>Update</c>.
/// Main thread only.
/// </summary>
internal static unsafe class GameLifts
{
    static readonly Dictionary<nint, bool> chairLifts = new();
    static Il2CppClass renderer;

    public static Vector3 Position(nint obj) => Unity.Position(obj);

    /// <summary>A station's point (falling back to the station itself).</summary>
    public static Vector3? Point(LiftStation station, string which)
    {
        if (station.IsNull || Unity.IsNull(station.Ptr))
            return null;
        var t = which switch
        {
            "in" => station.inPoint.Ptr,
            "out" => station.outPoint.Ptr,
            _ => station.wheelCenter.Ptr,
        };
        return t != 0 && !Unity.IsNull(t) ? Unity.Position(t) : Unity.Position(station.Ptr);
    }

    /// <summary>The lift's carriers (chairs or T-bars).</summary>
    public static List<nint> Carriers(LiftLine lift)
    {
        var list = lift.nkm;
        var result = new List<nint>();
        if (list == 0)
            return result;
        var type = Il2Cpp.ClassOf(list);
        var count = type.Method("get_Count", 0).Invoke<int>(list);
        var item = type.Method("get_Item", 1);
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var t = item.Invoke(list, (nint)(&index));
            if (t != 0)
                result.Add(t);
        }
        return result;
    }

    /// <summary>The world box around a carrier's renderers, or null if it has none.</summary>
    public static (Vector3 Lo, Vector3 Hi)? Bounds(nint carrier)
    {
        if (renderer.IsNull)
            renderer = Unity.Class("UnityEngine.Renderer");
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        var any = false;
        foreach (var r in Unity.GetComponentsInChildren(carrier, renderer))
        {
            var boxed = Unity.Call(r, "get_bounds");
            if (boxed == 0)
                continue;
            var b = Il2Cpp.Unbox<Bounds>(boxed);
            lo = new(MathF.Min(lo.X, b.Center.X - b.Extents.X), MathF.Min(lo.Y, b.Center.Y - b.Extents.Y), MathF.Min(lo.Z, b.Center.Z - b.Extents.Z));
            hi = new(MathF.Max(hi.X, b.Center.X + b.Extents.X), MathF.Max(hi.Y, b.Center.Y + b.Extents.Y), MathF.Max(hi.Z, b.Center.Z + b.Extents.Z));
            any = true;
        }
        return any ? (lo, hi) : null;
    }

    /// <summary>Whether a lift is a chair lift (not a T-bar or rope tow). Checked once per lift.</summary>
    public static bool IsChairLift(LiftLine lift)
    {
        if (chairLifts.TryGetValue(lift.Ptr, out var known))
            return known;
        var carriers = Carriers(lift);
        var name = carriers.Count > 0 ? Unity.Name(carriers[0]) : "";
        var width = carriers.Count > 0 && Bounds(carriers[0]) is { } b ? MathF.Max(b.Hi.X - b.Lo.X, b.Hi.Z - b.Lo.Z) : 0;
        var chair = LiftKinds.IsChairLift(name, width, carriers.Count);
        Log.Info("ChairLift", $"lift with {carriers.Count} carriers (\"{name}\", {width:0.0} m wide): {(chair ? "chair lift" : "not a chair lift, skipped")}");
        chairLifts[lift.Ptr] = chair;
        return chair;
    }

    /// <summary>The chair lift whose bottom station is nearest to <paramref name="me"/>, and how far.</summary>
    public static (LiftLine Lift, float Distance)? Nearest(Vector3 me)
    {
        (LiftLine, float)? best = null;
        foreach (var lift in LiftLine.Live())
        {
            if (lift.IsNull || Unity.IsNull(lift.Ptr) || !IsChairLift(lift))
                continue;
            if (Point(lift.baseStation, "wheel") is not { } wheel)
                continue;
            var d = Vec.Distance(me, wheel);
            foreach (var p in new[] { Point(lift.baseStation, "out"), Point(lift.baseStation, "in") })
                if (p is { } q)
                    d = MathF.Min(d, Vec.Distance(me, q));
            if (best is not { } b || d < b.Item2)
                best = (lift, d);
        }
        return best;
    }

    /// <summary>Forgets what it knew about lifts (a new map).</summary>
    public static void Reset() => chairLifts.Clear();

    // ---- transforms ----

    public static Vector3 TransformPoint(nint tr, Vector3 v) => Il2Cpp.Unbox<Vector3>(Unity.Call(tr, "TransformPoint", (nint)(&v)));
    public static Vector3 InverseTransformPoint(nint tr, Vector3 v) => Il2Cpp.Unbox<Vector3>(Unity.Call(tr, "InverseTransformPoint", (nint)(&v)));
    public static Vector3 InverseTransformDirection(nint tr, Vector3 v) => Il2Cpp.Unbox<Vector3>(Unity.Call(tr, "InverseTransformDirection", (nint)(&v)));
    public static bool IsChildOf(nint tr, nint parent) => Unity.Call<bool>(tr, "IsChildOf", parent);
}
