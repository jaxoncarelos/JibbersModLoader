using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Fpv;

sealed unsafe class Physics : IWorld
{
    readonly Il2CppMethod ray, sphere, distance, normal;
    public int Mask;
    public Physics(int mask)
    {
        Mask = mask == 0 ? -5 : mask;
        var k = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.Physics");
        var hit = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.RaycastHit");
        ray = k.Method("Raycast", "UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
        sphere = k.Method("SphereCast", "UnityEngine.Vector3", "System.Single", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
        distance = hit.Method("get_distance", 0); normal = hit.Method("get_normal", 0);
        if (ray.IsNull || sphere.IsNull || distance.IsNull || normal.IsNull || hit.ValueSize is <= 0 or > 512)
            throw new InvalidOperationException("Unsupported Physics cast / RaycastHit layout");
    }
    public Hit? Sweep(V3 origin, V3 direction, float length, float radius)
    {
        var o = new Vector3(origin.X, origin.Y, origin.Z);
        var d = new Vector3(direction.X, direction.Y, direction.Z);
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        var triggers = 1; var mask = Mask;
        var result = radius > 0
            ? sphere.Invoke(0, (nint)(&o), (nint)(&radius), (nint)(&d), (nint)buffer, (nint)(&length), (nint)(&mask), (nint)(&triggers))
            : ray.Invoke(0, (nint)(&o), (nint)(&d), (nint)buffer, (nint)(&length), (nint)(&mask), (nint)(&triggers));
        if (result == 0) throw new InvalidOperationException("Physics cast returned null");
        if (!Il2Cpp.Unbox<bool>(result)) return null;
        var dist = distance.Invoke<float>((nint)buffer);
        var n = normal.Invoke<Vector3>((nint)buffer);
        var v = new V3(n.X, n.Y, n.Z);
        if (!float.IsFinite(dist) || dist < 0 || !v.Finite) throw new InvalidOperationException("Invalid collision hit");
        return new(dist, v);
    }
}

sealed unsafe class RewiredMouse
{
    readonly Il2CppMethod controllers, ready, mouse, axis, button;
    public RewiredMouse()
    {
        var re = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput");
        controllers = re.Method("get_controllers", 0); ready = re.Method("get_isReady", 0);
        // Resolve the helper through the public property's return type, not an obfuscated name.
        if (controllers.IsNull || ready.IsNull) throw new InvalidOperationException("Rewired API missing");
        var helper = new Il2CppClass(Il2CppApi.class_from_type(Il2CppApi.method_get_return_type(controllers.Ptr)));
        mouse = helper.Method("get_Mouse", 0);
        axis = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ControllerWithAxes").Method("GetAxisRaw", "System.Int32");
        button = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.Controller").Method("GetButton", "System.Int32");
        if (controllers.IsNull || ready.IsNull || mouse.IsNull || axis.IsNull || button.IsNull)
            throw new InvalidOperationException("Rewired mouse API missing");
    }
    public Mouse Read()
    {
        if (!ready.Invoke<bool>(0)) return default;
        var helper = controllers.Invoke(0);
        if (helper == 0) return default;
        var m = mouse.Invoke(helper);
        if (m == 0) return default;
        var x = 0; var y = 1;
        return new(axis.Invoke<float>(m, (nint)(&x)), axis.Invoke<float>(m, (nint)(&y)), button.Invoke<bool>(m, (nint)(&y)));
    }
}
