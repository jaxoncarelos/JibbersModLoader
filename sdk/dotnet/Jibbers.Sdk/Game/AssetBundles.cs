using System.Runtime.InteropServices;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;
/// <summary>
/// Unity asset bundles built for the game's Unity version (6000.3): your prefabs, meshes, materials,
/// shaders and audio. The game's build strips the managed AssetBundle loaders, so this goes through
/// the player's internal calls. Main thread.
/// </summary>
/// <example><code>
/// using var bundle = AssetBundles.Load(Path.Combine(Bootstrap.ModsDir, "my_mod.bundle"));
/// var instance = bundle.Instantiate("MyPrefab");   // kept after the bundle is unloaded
/// </code></example>
public static unsafe class AssetBundles
{
    [StructLayout(LayoutKind.Sequential)]
    struct SpanWrapper
    {
        public char* Begin;
        public int Length;
    }

    /// <summary>The managed object behind the GC handle a Unity 6 <c>_Injected</c> binding returns (as the game's Unmarshal does).</summary>
    public static nint Unmarshal(nint handle)
    {
        if (handle == 0)
        {
            return 0;
        }

        var unmarshalClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Bindings.Unmarshal");
        var nativeHandle = handle;
        var boxedHandle = unmarshalClass.Method("FromIntPtrUnsafe", 1).Invoke(0, (nint)(&nativeHandle));
        var handleClass = Il2Cpp.ClassOf(boxedHandle);
        var handleValue = (nint)Il2CppApi.object_unbox(boxedHandle);
        try
        {
            return handleClass.Method("get_Target", 0).Invoke(handleValue);
        }
        finally
        {
            handleClass.Method("Free", 0).Invoke(handleValue);
        }
    }

    /// <summary>Loads a bundle file. Dispose it (unload, keeping instantiated objects) when you have what you need.</summary>
    public static Bundle Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{Path.GetFileName(path)} is missing", path);
        }

        var load = (delegate* unmanaged<SpanWrapper*, uint, ulong, nint> )Il2Cpp.ICall("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected");
        if (load == null)
        {
            throw new InvalidOperationException("AssetBundle loading isn't available");
        }

        nint bundle;
        fixed (char* chars = path)
        {
            var span = new SpanWrapper
            {
                Begin = chars,
                Length = path.Length
            };
            bundle = Unmarshal(load(&span, 0, 0));
        }

        return Unity.IsNull(bundle) ? throw new InvalidOperationException($"could not load {path} (built for another Unity version?)") : new Bundle(bundle);
    }

    /// <summary>A loaded asset bundle.</summary>
    public sealed class Bundle : IDisposable
    {
        GameRef? handle;
        internal Bundle(nint bundle) => handle = new GameRef(bundle);
        /// <summary>The AssetBundle object.</summary>
        public nint Ptr => handle?.Target ?? 0;

        /// <summary>An asset by name and class (e.g. <c>UnityEngine.GameObject</c>), or 0.</summary>
        public nint LoadAsset(string name, Il2CppClass type) => Unity.Call(Ptr, "LoadAsset", Il2Cpp.String(name), type.SystemType);
        /// <summary>Instantiates a prefab from the bundle, under <paramref name = "parent"/> (a Transform) when given.</summary>
        public nint Instantiate(string prefab, nint parent = 0)
        {
            var prefabAsset = LoadAsset(prefab, Il2Cpp.RequireClass(Unity.Core, "UnityEngine.GameObject"));
            if (Unity.IsNull(prefabAsset))
            {
                throw new InvalidOperationException($"prefab {prefab} is not in the bundle");
            }

            var unityObjectClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object");
            return parent == 0 ? unityObjectClass.Method("Instantiate", "UnityEngine.Object").Invoke(0, prefabAsset) : unityObjectClass.Method("Instantiate", "UnityEngine.Object", "UnityEngine.Transform").Invoke(0, prefabAsset, parent);
        }

        /// <summary>Unloads the bundle; objects instantiated from it keep their assets.</summary>
        public void Dispose()
        {
            if (handle is null)
            {
                return;
            }

            var ownedHandle = handle;
            handle = null;
            try
            {
                var bundle = ownedHandle.Target;
                var unload = (delegate* unmanaged<nint, byte, void> )Il2Cpp.ICall("UnityEngine.AssetBundle::Unload_Injected");
                var native = bundle == 0 ? 0 : Il2Cpp.ClassOf(bundle).Field("m_CachedPtr").Get<nint>(bundle);
                if (unload != null && native != 0)
                {
                    unload(native, 0);
                }
            }
            finally
            {
                ownedHandle.Dispose();
            }
        }
    }
}
