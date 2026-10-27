using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Interop;
using N = System.Numerics;

namespace Rumble;

/// <summary>
/// Measures the followed rider for <see cref="RumbleDirector"/>: hips, how high the lowest part of the
/// body is above what's under it, how upright the spine is, and whether the feet are on terrain or an
/// object (a rail, a box). Bones are found once per rider, on the head's chain of parents.
/// </summary>
internal sealed unsafe class RiderProbe : IDisposable
{
    const string Prefix = "QuickRigCharacter_";
    readonly Il2CppMethod ray, distance, collider, normal;
    GameRef? hips, neck, leftFoot, rightFoot;
    nint followTarget;
    float searchCooldown;

    /// <summary>The name of the object under the feet, when it isn't terrain, for the log.</summary>
    public string? ObjectUnderFeet { get; private set; }

    public RiderProbe()
    {
        var physics = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.Physics");
        var hit = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.RaycastHit");
        ray = physics.Method("Raycast", "UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
        distance = hit.Method("get_distance", 0);
        collider = hit.Method("get_collider", 0);
        normal = hit.Method("get_normal", 0);
        if (ray.IsNull || distance.IsNull || collider.IsNull || normal.IsNull || hit.ValueSize is <= 0 or > 512)
            throw new InvalidOperationException("Unsupported Physics.Raycast / RaycastHit layout");
    }

    public RumbleFrame? Read(Follow follow, float seconds)
    {
        var target = follow.Target.Ptr;
        if (target != followTarget || hips is null || Unity.IsNull(hips.Target))
        {
            Release();
            followTarget = target;
            if ((searchCooldown -= seconds) > 0 || Unity.IsNull(target))
                return null;
            searchCooldown = 1;
            if (!Find(target))
                return null;
        }
        var mask = follow.GroundLayer == 0 ? -5 : follow.GroundLayer;
        var center = Point(hips)!.Value;
        var neckPoint = Point(neck) ?? center + N.Vector3.UnitY * 0.55f;
        var feet = new[] { Point(leftFoot), Point(rightFoot) }.Where(p => p is not null).Select(p => p!.Value).ToArray();
        if (feet.Length == 0)
            feet = new[] { center - N.Vector3.UnitY * 0.9f };
        var lowestFoot = feet.MinBy(p => p.Y);

        // How high the lowest part of the body is: lying on the snow with your feet up is not flying.
        var underFeet = Cast(lowestFoot, mask);
        var clearance = new[] { underFeet.Distance, Cast(center, mask).Distance, Cast(neckPoint, mask).Distance }.Min();
        var onObject = underFeet is { Distance: < 0.35f, Terrain: false };
        ObjectUnderFeet = onObject ? underFeet.Name : null;
        var upright = N.Vector3.Normalize(neckPoint - center).Y;
        return new(center, clearance, float.IsFinite(upright) ? upright : 1, onObject, false) { GroundNormal = underFeet.Normal };
    }

    /// <summary>Straight down from just above <paramref name="point"/>: the distance, the surface, and what was hit.</summary>
    (float Distance, bool Terrain, string Name, N.Vector3 Normal) Cast(N.Vector3 point, int mask)
    {
        const float probe = 0.5f;
        var from = new Vector3(point.X, point.Y + probe, point.Z);
        var down = new Vector3(0, -1, 0);
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        var length = 30f;
        var ignoreTriggers = 1;
        var result = ray.Invoke(0, (nint)(&from), (nint)(&down), (nint)buffer, (nint)(&length), (nint)(&mask), (nint)(&ignoreTriggers));
        if (result == 0 || !Il2Cpp.Unbox<bool>(result))
            return (30, true, "", N.Vector3.UnitY);
        var hitDistance = distance.Invoke<float>((nint)buffer) - probe;
        var hitCollider = collider.Invoke((nint)buffer);
        var terrain = hitCollider == 0 || Il2Cpp.ClassOf(hitCollider).Name == "TerrainCollider";
        var surface = normal.Invoke<Vector3>((nint)buffer);
        return (float.IsFinite(hitDistance) ? hitDistance : 30, terrain, terrain ? "" : Unity.Name(hitCollider), new N.Vector3(surface.X, surface.Y, surface.Z));
    }

    bool Find(nint target)
    {
        var bones = Unity.GetComponentsInChildren(target, Unity.Class("UnityEngine.Transform"), false);
        nint Bone(string name) => bones.FirstOrDefault(bone => Unity.Name(bone) == Prefix + name);
        var hipsBone = Unity.Name(target) == Prefix + "Hips" ? target : Bone("Hips");
        if (hipsBone == 0)
            return false;
        GameRef? Own(nint bone) => bone == 0 ? null : new GameRef(bone);
        (hips, neck, leftFoot, rightFoot) = (new GameRef(hipsBone), Own(Bone("Neck")), Own(Bone("LeftFoot")), Own(Bone("RightFoot")));
        return true;
    }

    static N.Vector3? Point(GameRef? bone)
    {
        if (bone is null || Unity.IsNull(bone.Target))
            return null;
        var p = Unity.Call<Vector3>(bone.Target, "get_position");
        return float.IsFinite(p.X + p.Y + p.Z) ? new N.Vector3(p.X, p.Y, p.Z) : null;
    }

    void Release()
    {
        foreach (var bone in new[] { hips, neck, leftFoot, rightFoot })
            bone?.Dispose();
        hips = neck = leftFoot = rightFoot = null;
    }

    public void Dispose() => Release();
}
