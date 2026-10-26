using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Atmosphere;

/// <summary>Loads and owns the atmosphere prefab using the shared bundle API.</summary>
internal sealed class AtmospherePrefab(Action<string> log) : IAtmospherePrefab
{
    const string PrefabName = "JibbersAtmosphere";
    const string InstanceName = PrefabName + "(Clone)";
    int respawnRequested;
    int framesUntilCheck;
    public bool Spawned { get; private set; }

    public void RequestRespawn() => Interlocked.Exchange(ref respawnRequested, 1);
    public void EnsureSpawned()
    {
        if (Interlocked.Exchange(ref respawnRequested, 0) != 0)
        {
            var previousInstance = Unity.Find(InstanceName);
            if (!Unity.IsNull(previousInstance))
                Unity.Class("UnityEngine.Object").Method("DestroyImmediate", 1).Invoke(0, previousInstance);
            framesUntilCheck = 0;
        }

        if (framesUntilCheck-- > 0)
            return;
        framesUntilCheck = 120;
        if (Unity.IsNull(Unity.Find(InstanceName)))
        {
            using var bundle = AssetBundles.Load(Path.Combine(Bootstrap.GameDir, "jibbers_atmosphere.bundle"));
            var instance = bundle.Instantiate(PrefabName);
            Unity.Class("UnityEngine.Object").Method("DontDestroyOnLoad", 1).Invoke(0, instance);
            log("spawned");
        }

        Spawned = true;
    }

    public void Dispose()
    {
        var instance = Unity.Find(InstanceName);
        if (!Unity.IsNull(instance))
            Unity.Destroy(instance);
        Spawned = false;
    }
}
