using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Fpv;

sealed unsafe class UnityCollisionWorld : ICollisionWorld
{
    readonly Il2CppMethod ray, sphere, distance, normal;
    public int Mask;
    public UnityCollisionWorld(int mask)
    {
        Mask = mask == 0 ? -5 : mask;
        var physicsClass = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.Physics");
        var hit = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.RaycastHit");
        ray = physicsClass.Method("Raycast", "UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
        sphere = physicsClass.Method("SphereCast", "UnityEngine.Vector3", "System.Single", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
        distance = hit.Method("get_distance", 0);
        normal = hit.Method("get_normal", 0);
        if (ray.IsNull || sphere.IsNull || distance.IsNull || normal.IsNull || hit.ValueSize is <= 0 or > 512)
            throw new InvalidOperationException("Unsupported Physics cast / RaycastHit layout");
    }

    public CollisionHit? Sweep(DroneVector origin, DroneVector direction, float length, float radius)
    {
        var castOrigin = new Vector3(origin.X, origin.Y, origin.Z);
        var castDirection = new Vector3(direction.X, direction.Y, direction.Z);
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        var triggers = 1;
        var mask = Mask;
        var result = radius > 0 ? sphere.Invoke(0, (nint)(&castOrigin), (nint)(&radius), (nint)(&castDirection), (nint)buffer, (nint)(&length), (nint)(&mask), (nint)(&triggers)) : ray.Invoke(0, (nint)(&castOrigin), (nint)(&castDirection), (nint)buffer, (nint)(&length), (nint)(&mask), (nint)(&triggers));
        if (result == 0)
            throw new InvalidOperationException("Physics cast returned null");
        if (!Il2Cpp.Unbox<bool>(result))
            return null;
        var hitDistance = distance.Invoke<float>((nint)buffer);
        var normalVector = normal.Invoke<Vector3>((nint)buffer);
        var surfaceNormal = new DroneVector(normalVector.X, normalVector.Y, normalVector.Z);
        if (!float.IsFinite(hitDistance) || hitDistance < 0 || !surfaceNormal.IsFinite)
            throw new InvalidOperationException("Invalid collision hit");
        return new(hitDistance, surfaceNormal);
    }
}
