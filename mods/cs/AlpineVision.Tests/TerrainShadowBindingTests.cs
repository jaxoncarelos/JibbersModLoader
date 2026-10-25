using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JibbersAlpineVision.Rendering;

static unsafe class TerrainShadowBindingTests
{
    // Opaque identities for the stubs, never memory addresses to dereference.
    static readonly nint ManagedTerrain = 17, NativeTerrain = 42;
    static nint receivedPointer;
    static int shadowMode;
    static byte visibility;

    public static void Run()
    {
        shadowMode = 2;
        visibility = 1;
        var properties = new TerrainShadowProperties(Resolve, terrain => terrain == ManagedTerrain ? NativeTerrain : 0);
        var original = properties.GetShadowMode(ManagedTerrain);
        if (original != 2 || receivedPointer != NativeTerrain || !properties.DrawsHeightmap(ManagedTerrain))
            throw new Exception("Terrain bindings must receive the native object pointer, not its managed wrapper");
        properties.SetShadowMode(ManagedTerrain, 0);
        if (shadowMode != 0 || receivedPointer != NativeTerrain)
            throw new Exception("Terrain shadow suppression did not reach the native setter");
        properties.SetShadowMode(ManagedTerrain, original);
        if (shadowMode != 2)
            throw new Exception("Original two-sided terrain shadow mode was not restored");
        visibility = 0;
        if (properties.DrawsHeightmap(ManagedTerrain))
            throw new Exception("Native terrain visibility was ignored");

        try { properties.GetShadowMode(0); throw new Exception("Destroyed terrain reached a native binding"); }
        catch (ObjectDisposedException) { }
        try
        {
            _ = new TerrainShadowProperties(name => name.Contains("set_shadowCastingMode") ? 0 : Resolve(name), _ => NativeTerrain);
            throw new Exception("Missing restore setter did not reject the shadow replacement");
        }
        catch (NotSupportedException) { }
        Console.WriteLine("PASS terrain engine bindings without managed wrappers: native-pointer ABI, visibility, suppression, exact restoration and missing-binding rejection");
    }

    static nint Resolve(string name) => name switch {
        "UnityEngine.Terrain::get_shadowCastingMode_Injected" => (nint)(delegate* unmanaged[Cdecl]<nint, int>)&ReadMode,
        "UnityEngine.Terrain::set_shadowCastingMode_Injected" => (nint)(delegate* unmanaged[Cdecl]<nint, int, void>)&WriteMode,
        "UnityEngine.Terrain::get_drawHeightmap_Injected" => (nint)(delegate* unmanaged[Cdecl]<nint, byte>)&ReadVisibility,
        _ => 0
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int ReadMode(nint terrain) { receivedPointer = terrain; return shadowMode; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void WriteMode(nint terrain, int mode) { receivedPointer = terrain; shadowMode = mode; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static byte ReadVisibility(nint terrain) { receivedPointer = terrain; return visibility; }
}
