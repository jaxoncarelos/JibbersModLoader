using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace JibbersAlpineVision.Rendering;

/// <summary>Terrain shadow properties whose managed Unity wrappers can be stripped from the player.</summary>
internal sealed class TerrainShadowProperties
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int ReadShadowMode(nint nativeTerrain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void WriteShadowMode(nint nativeTerrain, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate byte ReadVisibility(nint nativeTerrain);

    readonly ReadShadowMode readMode;
    readonly WriteShadowMode writeMode;
    readonly ReadVisibility? readVisibility;
    readonly Func<nint, nint> nativePointer;

    public TerrainShadowProperties() : this(Il2Cpp.ICall, CreatePointerReader()) { }

    internal TerrainShadowProperties(Func<string, nint> resolve, Func<nint, nint> nativePointer)
    {
        this.nativePointer = nativePointer;
        // These are Unity's native binding signatures: IntPtr self, then the enum value.
        // An engine internal call has no IL2CPP MethodInfo parameter.
        readMode = Resolve<ReadShadowMode>(resolve, "get_shadowCastingMode_Injected");
        writeMode = Resolve<WriteShadowMode>(resolve, "set_shadowCastingMode_Injected");
        var visibility = resolve("UnityEngine.Terrain::get_drawHeightmap_Injected");
        if (visibility != 0)
            readVisibility = Marshal.GetDelegateForFunctionPointer<ReadVisibility>(visibility);
    }

    static T Resolve<T>(Func<string, nint> resolve, string name) where T : Delegate
    {
        var address = resolve("UnityEngine.Terrain::" + name);
        if (address == 0)
            throw new NotSupportedException("Terrain shadow binding unavailable: " + name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    static Func<nint, nint> CreatePointerReader()
    {
        var field = Unity.Class("UnityEngine.Object").Field("m_CachedPtr");
        if (field.IsNull || field.TypeName != "System.IntPtr")
            throw new NotSupportedException("Unity native-object pointer metadata unavailable");
        return instance => field.Get<nint>(instance);
    }

    nint RequireNativePointer(nint terrain)
    {
        var pointer = terrain == 0 ? 0 : nativePointer(terrain);
        return pointer != 0 ? pointer : throw new ObjectDisposedException("Terrain");
    }

    public int GetShadowMode(nint terrain) => readMode(RequireNativePointer(terrain));
    public void SetShadowMode(nint terrain, int mode) => writeMode(RequireNativePointer(terrain), mode);
    public bool DrawsHeightmap(nint terrain) => readVisibility is null || readVisibility(RequireNativePointer(terrain)) != 0;
}
