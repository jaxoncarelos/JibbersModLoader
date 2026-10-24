using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

// Unity input is read on the main thread, then published to the existing input poller.
// Keeps bindings portable without requiring macOS Accessibility permission.
internal static class PlatformInput
{
    static readonly int[] keys = new int[256];
    static int focused, mouseX, mouseY;
    internal static bool Focused => Volatile.Read(ref focused) != 0;
    internal static (int, int) Mouse => (Volatile.Read(ref mouseX), Volatile.Read(ref mouseY));
    internal static bool Down(int key) => Volatile.Read(ref keys[key]) != 0;
    internal static int? UnityKey(int key) => key switch
    {
        0x10 => 304, 0x11 => 306, 0x12 => 308,
        0xA0 => 304, 0xA1 => 303, 0xA2 => 306, 0xA3 => 305, 0xA4 => 308, 0xA5 => 307,
        _ => Features.Rewind.RewiredPolicy.KeyboardCode(key)
    };
    internal static unsafe void Sample()
    {
        var input = Il2Cpp.RequireClass("UnityEngine.InputLegacyModule.dll", "UnityEngine.Input");
        var app = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Application");
        var hasFocus = app.Method("get_isFocused", 0).Invoke<bool>(0);
        var getKey = input.Method("GetKeyInt", 1);
        var getMouse = input.Method("GetMouseButton", 1);
        for (var vk = 1; vk < keys.Length; vk++)
        {
            var down = false;
            var mouse = vk switch { 1 => 0, 2 => 1, 4 => 2, 5 => 3, 6 => 4, _ => -1 };
            if (hasFocus && mouse >= 0) down = getMouse.Invoke<bool>(0, (nint)(&mouse));
            else if (hasFocus && UnityKey(vk) is int code)
            {
                down = getKey.Invoke<bool>(0, (nint)(&code));
                if (vk is 0x10 or 0x11 or 0x12)
                {
                    code--;
                    down |= getKey.Invoke<bool>(0, (nint)(&code));
                }
            }
            Volatile.Write(ref keys[vk], down ? 1 : 0);
        }
        var position = input.Method("get_mousePosition", 0).Invoke<Vector3>(0);
        Volatile.Write(ref mouseX, (int)position.X);
        Volatile.Write(ref mouseY, -(int)position.Y); // Match Win32's downward-positive screen Y.
        Volatile.Write(ref focused, hasFocus ? 1 : 0);
    }
}
