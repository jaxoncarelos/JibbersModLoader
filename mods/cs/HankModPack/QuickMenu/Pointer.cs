using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>The mouse: position (screen pixels, origin bottom left), buttons, and the cursor shown while one of the mod's windows is open. Never throws.</summary>
internal static unsafe class Pointer
{
    static bool broken;
    static bool cursorSaved; static bool oldVisible; static int oldLock;

    public static bool Position(out float x, out float y)
    {
        x = y = 0;
        if (broken) return false;
        try
        {
            var v = Il2Cpp.Unbox<Vector3>(Unity.CallStatic(Unity.Class("UnityEngine.Input"), "get_mousePosition"));
            x = v.X; y = v.Y;
            return true;
        }
        catch { broken = true; return false; }
    }

    public static bool Down(int button = 0)
    {
        if (broken) return false;
        try { var b = button; return Il2Cpp.Unbox<byte>(Unity.CallStatic(Unity.Class("UnityEngine.Input"), "GetMouseButtonDown", (nint)(&b))) != 0; }
        catch { broken = true; return false; }
    }

    public static bool Held(int button = 0)
    {
        if (broken) return false;
        try { var b = button; return Il2Cpp.Unbox<byte>(Unity.CallStatic(Unity.Class("UnityEngine.Input"), "GetMouseButton", (nint)(&b))) != 0; }
        catch { broken = true; return false; }
    }

    public static bool Screen(out float w, out float h)
    {
        w = h = 0;
        try
        {
            var s = Unity.Class("UnityEngine.Screen");
            w = Il2Cpp.Unbox<int>(Unity.CallStatic(s, "get_width")); h = Il2Cpp.Unbox<int>(Unity.CallStatic(s, "get_height"));
            return w > 0 && h > 0;
        }
        catch { return false; }
    }

    /// <summary>The scale of the mod's canvases (reference 1920 x 1080, match 0.5): screen pixels per canvas unit.</summary>
    public static float CanvasScale()
    {
        if (!Screen(out var w, out var h)) return 1;
        return MathF.Pow(2, 0.5f * MathF.Log2(w / 1920f) + 0.5f * MathF.Log2(h / 1080f));
    }

    /// <summary>Shows the system cursor (free) while a window is open; <see cref="Restore"/> puts it back as it was.</summary>
    public static void Show()
    {
        try
        {
            var k = Unity.Class("UnityEngine.Cursor");
            if (!cursorSaved)
            {
                oldVisible = Il2Cpp.Unbox<byte>(Unity.CallStatic(k, "get_visible")) != 0;
                oldLock = Il2Cpp.Unbox<int>(Unity.CallStatic(k, "get_lockState"));
                cursorSaved = true;
            }
            var v = (byte)1; var l = 0;
            Unity.CallStatic(k, "set_lockState", (nint)(&l));
            Unity.CallStatic(k, "set_visible", (nint)(&v));
        }
        catch { }
    }

    public static void Restore()
    {
        if (!cursorSaved) return;
        cursorSaved = false;
        try
        {
            var k = Unity.Class("UnityEngine.Cursor");
            var v = (byte)(oldVisible ? 1 : 0); var l = oldLock;
            Unity.CallStatic(k, "set_visible", (nint)(&v));
            Unity.CallStatic(k, "set_lockState", (nint)(&l));
        }
        catch { }
    }
}
