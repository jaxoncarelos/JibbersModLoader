using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Spotify.Integration;

/// <summary>Making and filling Unity textures, whichever way this game build kept. Main thread only.</summary>
internal static unsafe class Textures
{
    static Il2CppClass Texture2D => Unity.Class("UnityEngine.Texture2D");

    /// <summary>A new RGBA32 texture, kept across maps (the caller holds a GameRef to it).</summary>
    public static nint New(int w, int h)
    {
        var tk = Texture2D;
        var tex = tk.New();
        int tw = w, th = h, format = 4; // RGBA32
        var mips = (byte)0;
        tk.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Boolean").Invoke(tex, (nint)(&tw), (nint)(&th), (nint)(&format), (nint)(&mips));
        UnityProperties.Set(tex, "set_filterMode", 1); // bilinear
        UnityProperties.Set(tex, "set_wrapMode", 1); // clamp
        Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", tex);
        return tex;
    }

    static nint ByteArray(byte[] bytes)
    {
        var array = Il2CppApi.array_new(Il2Cpp.RequireClass("mscorlib.dll", "System.Byte").Ptr, (nuint)bytes.Length);
        fixed (byte* p = bytes)
            Buffer.MemoryCopy(p, (void*)Il2Cpp.ArrayData(array), bytes.Length, bytes.Length);
        return array;
    }

    /// <summary>Puts straight RGBA bytes (bottom-up rows) into a texture and uploads it.</summary>
    public static void Fill(nint tex, byte[] rgba)
    {
        var tk = Texture2D;
        var raw = tk.Method("LoadRawTextureData", "System.IntPtr", "System.Int32");
        if (!raw.IsNull)
        {
            fixed (byte* p = rgba)
            {
                nint ptr = (nint)p;
                var len = rgba.Length;
                raw.Invoke(tex, (nint)(&ptr), (nint)(&len));
            }
        }
        else if (tk.Method("LoadRawTextureData", "System.Byte[]") is { IsNull: false } bytes)
            bytes.Invoke(tex, ByteArray(rgba));
        else if (tk.Method("SetPixels32", "UnityEngine.Color32[]") is { IsNull: false } colors)
        {
            var array = Il2CppApi.array_new(Unity.Class("UnityEngine.Color32").Ptr, (nuint)(rgba.Length / 4));
            fixed (byte* p = rgba)
                Buffer.MemoryCopy(p, (void*)Il2Cpp.ArrayData(array), rgba.Length, rgba.Length);
            colors.Invoke(tex, array);
        }
        else
            throw new InvalidOperationException("no way to fill a texture in this game build");
        Unity.Call(tex, "Apply");
    }

    /// <summary>A texture of <paramref name="p"/>'s picture.</summary>
    public static nint From(Pixels p)
    {
        var tex = New(p.W, p.H);
        Fill(tex, p.ToRgba());
        return tex;
    }

    /// <summary>
    /// Decodes an image file (JPEG / PNG) with the game's own <c>ImageConversion.LoadImage</c> and gives
    /// its straight RGBA pixels (bottom-up) and size, or null if it can't.
    /// </summary>
    public static (byte[] Rgba, int W, int H)? Decode(byte[] file)
    {
        var conversion = Il2Cpp.RequireClass("UnityEngine.ImageConversionModule.dll", "UnityEngine.ImageConversion");
        var load = conversion.Method("LoadImage", "UnityEngine.Texture2D", "System.Byte[]");
        if (load.IsNull) return null;
        var tex = New(2, 2);
        try
        {
            if (!load.Invoke<bool>(0, tex, ByteArray(file))) return null;
            int w = Unity.Call<int>(tex, "get_width"), h = Unity.Call<int>(tex, "get_height");
            var pixels = Texture2D.Method("GetPixels32", 0).Invoke(tex);
            if (pixels == 0 || Il2Cpp.ArrayLength(pixels) < w * h) return null;
            var rgba = new byte[w * h * 4];
            fixed (byte* p = rgba)
                Buffer.MemoryCopy((void*)Il2Cpp.ArrayData(pixels), p, rgba.Length, rgba.Length);
            return (rgba, w, h);
        }
        finally
        {
            Unity.Destroy(tex);
        }
    }
}
