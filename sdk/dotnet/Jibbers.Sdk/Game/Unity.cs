using System.Runtime.InteropServices;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary><c>UnityEngine.Vector2</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Vector2(float X, float Y);

/// <summary><c>UnityEngine.Vector3</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Vector3(float X, float Y, float Z)
{
    /// <summary>Length.</summary>
    public readonly float Magnitude => MathF.Sqrt(X * X + Y * Y + Z * Z);
    /// <summary>Sum.</summary>
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    /// <summary>Difference.</summary>
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    /// <summary>Scale.</summary>
    public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
}

/// <summary><c>UnityEngine.Vector4</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Vector4(float X, float Y, float Z, float W);

/// <summary><c>UnityEngine.Quaternion</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Quaternion(float X, float Y, float Z, float W)
{
    /// <summary>No rotation.</summary>
    public static Quaternion Identity => new(0, 0, 0, 1);
}

/// <summary><c>UnityEngine.Color</c> (linear RGBA, 0-1).</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Color(float R, float G, float B, float A);

/// <summary><c>UnityEngine.Rect</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Rect(float X, float Y, float Width, float Height);

/// <summary><c>UnityEngine.Bounds</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Bounds(Vector3 Center, Vector3 Extents);

/// <summary><c>UnityEngine.Matrix4x4</c> (column-major m00, m10, m20, m30, m01, ...).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Matrix4x4
{
    /// <summary>The 16 elements, column-major.</summary>
    public fixed float M[16];
}

/// <summary>
/// Common Unity operations by name, for the engine classes (the generated <c>Interop.UnityEngine.*</c>
/// bindings cover the rest). Main thread only.
/// </summary>
public static unsafe class Unity
{
    /// <summary><c>UnityEngine.CoreModule.dll</c>.</summary>
    public const string Core = "UnityEngine.CoreModule.dll";

    static Il2CppClass uobject;
    static Il2CppMethod opImplicit, findByType, findOfType, getName;

    static Il2CppClass UObject => uobject.IsNull ? (uobject = Il2Cpp.RequireClass(Core, "UnityEngine.Object")) : uobject;

    /// <summary>A class in an engine module, e.g. <c>Unity.Class("UnityEngine.Camera")</c> (searches every UnityEngine image).</summary>
    public static Il2CppClass Class(string fullName)
    {
        foreach (var (_, img) in Il2Cpp.Assemblies().Where(a => a.Name.StartsWith("UnityEngine") || a.Name.StartsWith("Unity.")))
        {
            var dot = fullName.LastIndexOf('.');
            var k = dot < 0 ? Il2Cpp.Class(img, "", fullName) : Il2Cpp.Class(img, fullName[..dot], fullName[(dot + 1)..]);
            if (!k.IsNull)
            {
                return k;
            }
        }
        throw new InvalidOperationException($"engine class {fullName} not found");
    }

    /// <summary>
    /// Unity's null check: true for null and for destroyed objects (a destroyed
    /// <c>UnityEngine.Object</c> is still a live managed object whose native side is gone).
    /// </summary>
    public static bool IsNull(nint obj)
    {
        if (obj == 0)
        {
            return true;
        }
        if (!UObject.IsAssignableFrom(Il2Cpp.ClassOf(obj)))
        {
            return false;
        }
        if (opImplicit.IsNull)
        {
            opImplicit = UObject.Method("op_Implicit", 1);
        }
        return !opImplicit.Invoke<bool>(0, obj);
    }

    /// <summary>Every loaded, active object of a component or asset class.</summary>
    public static nint[] FindObjects(Il2CppClass k)
    {
        if (findByType.IsNull && findOfType.IsNull)
        {
            findByType = UObject.Method("FindObjectsByType", "System.Type", "UnityEngine.FindObjectsSortMode");
            if (findByType.IsNull)
            {
                findOfType = UObject.Method("FindObjectsOfType", "System.Type");
            }
        }
        nint arr;
        if (!findByType.IsNull)
        {
            var mode = 0;
            arr = findByType.Invoke(0, k.SystemType, (nint)(&mode));
        }
        else
        {
            arr = findOfType.Invoke(0, k.SystemType);
        }
        return Il2Cpp.ArrayObjects(arr);
    }

    /// <summary>An object's name ("" if destroyed).</summary>
    public static string Name(nint obj)
    {
        if (IsNull(obj))
        {
            return "";
        }
        if (getName.IsNull)
        {
            getName = UObject.Method("get_name", 0);
        }
        return Il2Cpp.ReadString(getName.Invoke(obj));
    }

    /// <summary>Calls a method by name on an object's own class (virtual).</summary>
    public static nint Call(nint obj, string method, params nint[] args)
    {
        Il2Cpp.Require(obj, method);
        var m = Il2Cpp.ClassOf(obj).Method(method, args.Length);
        return m.IsNull ? throw new MissingMethodException($"{Il2Cpp.ClassOf(obj)}.{method}/{args.Length}") : m.Invoke(obj, args);
    }

    /// <summary>Calls a method by name and unboxes its result.</summary>
    public static T Call<T>(nint obj, string method, params nint[] args) where T : unmanaged
    {
        var r = Call(obj, method, args);
        return r == 0 ? throw new InvalidOperationException($"{method} returned null") : Il2Cpp.Unbox<T>(r);
    }

    /// <summary>Calls a static method by name.</summary>
    public static nint CallStatic(Il2CppClass k, string method, params nint[] args)
    {
        var m = k.Method(method, args.Length);
        return m.IsNull ? throw new MissingMethodException($"{k}.{method}/{args.Length}") : m.Invoke(0, args);
    }

    /// <summary>A component's (or GameObject's) transform.</summary>
    public static nint Transform(nint obj) => Call(obj, "get_transform");

    /// <summary>World position of a component or transform.</summary>
    public static Vector3 Position(nint obj)
    {
        var t = Il2Cpp.ClassOf(obj).Name == "Transform" ? obj : Transform(obj);
        return Call<Vector3>(t, "get_position");
    }

    /// <summary>The first component of type <paramref name="k"/> on a GameObject or component, or 0.</summary>
    public static nint GetComponent(nint obj, Il2CppClass k)
    {
        var m = Il2Cpp.ClassOf(obj).Method("GetComponent", "System.Type");
        return m.IsNull ? 0 : m.Invoke(obj, k.SystemType);
    }

    /// <summary>All components of type <paramref name="k"/> in children.</summary>
    public static nint[] GetComponentsInChildren(nint obj, Il2CppClass k, bool includeInactive = false)
    {
        var m = Il2Cpp.ClassOf(obj).Method("GetComponentsInChildren", "System.Type", "System.Boolean");
        var inc = (byte)(includeInactive ? 1 : 0);
        return m.IsNull ? Array.Empty<nint>() : Il2Cpp.ArrayObjects(m.Invoke(obj, k.SystemType, (nint)(&inc)));
    }

    /// <summary>Finds an active GameObject by name or path (<c>GameObject.Find</c>), or 0.</summary>
    public static nint Find(string pathOrName) => CallStatic(Il2Cpp.RequireClass(Core, "UnityEngine.GameObject"), "Find", Il2Cpp.String(pathOrName));

    /// <summary>Destroys an object (<c>Object.Destroy</c>).</summary>
    public static void Destroy(nint obj)
    {
        if (!IsNull(obj))
        {
            UObject.Method("Destroy", 1).Invoke(0, obj);
        }
    }

    /// <summary>Clones an object (<c>Object.Instantiate</c>).</summary>
    public static nint Instantiate(nint original) => UObject.Method("Instantiate", 1).Invoke(0, original);

    /// <summary>Seconds since the last frame.</summary>
    public static float DeltaTime => Il2Cpp.Unbox<float>(CallStatic(Il2Cpp.RequireClass(Core, "UnityEngine.Time"), "get_deltaTime"));

    /// <summary>Seconds since the game started.</summary>
    public static float Time => Il2Cpp.Unbox<float>(CallStatic(Il2Cpp.RequireClass(Core, "UnityEngine.Time"), "get_time"));

    /// <summary>The main camera (tagged MainCamera), or 0.</summary>
    public static nint MainCamera => CallStatic(Il2Cpp.RequireClass(Core, "UnityEngine.Camera"), "get_main");
}

/// <summary>
/// Keeps a game object alive and findable across frames: the IL2CPP garbage collector can free
/// objects you only hold as raw pointers. Dispose when done.
/// </summary>
public sealed class GameRef : IDisposable
{
    nint handle;
    static Il2CppMethod getTargetHandle, getTarget, freeHandle;

    static void Init()
    {
        if (!getTargetHandle.IsNull)
        {
            return;
        }
        var k = Il2Cpp.RequireClass("mscorlib.dll", "System.Runtime.InteropServices.GCHandle");
        getTargetHandle = k.Method("GetTargetHandle", 3);
        getTarget = k.Method("GetTarget", 1);
        freeHandle = k.Method("FreeHandle", 1);
    }

    /// <summary>Roots <paramref name="obj"/> (Unity 6's pointer-sized GCHandle; the legacy il2cpp_gchandle_* exports truncate in this game).</summary>
    public unsafe GameRef(nint obj)
    {
        Init();
        nint zero = 0;
        var normal = 2;
        var r = getTargetHandle.Invoke(0, obj, (nint)(&zero), (nint)(&normal));
        handle = r == 0 ? 0 : Il2Cpp.Unbox<nint>(r);
        if (handle == 0)
        {
            throw new InvalidOperationException("GCHandle.GetTargetHandle failed");
        }
    }

    /// <summary>The object (0 once disposed). Check <see cref="Unity.IsNull"/> for destroyed Unity objects.</summary>
    public unsafe nint Target
    {
        get
        {
            if (handle == 0)
            {
                return 0;
            }
            var h = handle;
            return getTarget.Invoke(0, (nint)(&h));
        }
    }

    /// <summary>Frees the handle. Main thread.</summary>
    public unsafe void Dispose()
    {
        if (handle != 0)
        {
            var h = handle;
            freeHandle.Invoke(0, (nint)(&h));
            handle = 0;
        }
    }
}
