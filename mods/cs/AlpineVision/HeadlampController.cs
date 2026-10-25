using Jibbers.Sdk;
using Jibbers.Sdk.Game.Player;
using JibbersAlpineVision.Weather;

namespace JibbersAlpineVision;

/// <summary>Suppresses only the local rider's headlamp, leaving native day/night activation intact.</summary>
internal sealed unsafe class HeadlampController(Action<string> log) : IDisposable
{
    sealed record OwnedLight(GameRef Reference, bool WasEnabled);

    readonly List<OwnedLight> lights = new();
    GameRef? spawner;
    GameRef? rider;
    int discoveryDelay;
    bool reportedFailure;

    public void Update(bool headlampEnabled)
    {
        try
        {
            if (headlampEnabled)
            {
                if (spawner is not null || rider is not null || lights.Count > 0)
                    Dispose();
                return;
            }
            if (spawner is not null && (Unity.IsNull(spawner.Target)
                || !Unity.Call<bool>(Unity.Call(spawner.Target, "get_gameObject"), "get_activeInHierarchy")))
            {
                Dispose();
                discoveryDelay = 0;
            }
            if (spawner is null)
            {
                if (discoveryDelay-- > 0)
                    return;
                discoveryDelay = 60;
                var localSpawner = RagdollSpawner.First();
                if (localSpawner is null)
                    return;
                spawner = new GameRef(localSpawner.Value.Ptr);
            }

            var localRider = new RagdollSpawner(spawner.Target).Rider.Ptr;
            if (Unity.IsNull(localRider))
            {
                RestoreLights();
                return;
            }
            if (rider?.Target != localRider)
            {
                RestoreLights();
                rider = new GameRef(localRider);
                discoveryDelay = 0;
            }
            if (lights.Count == 0 && discoveryDelay-- <= 0)
            {
                discoveryDelay = 60;
                foreach (var light in Unity.GetComponentsInChildren(localRider, Unity.Class("UnityEngine.Light"), true))
                {
                    var mount = Unity.Call(Unity.Transform(light), "get_parent");
                    if (Unity.IsNull(mount) || Unity.Name(mount) != "Lamp")
                        continue;
                    var head = Unity.Call(mount, "get_parent");
                    if (Unity.IsNull(head) || Unity.Name(head) != "QuickRigCharacter_Head")
                        continue;
                    var wasEnabled = Unity.Call<bool>(light, "get_enabled");
                    lights.Add(new OwnedLight(new GameRef(light), wasEnabled));
                }
            }

            // The game can enable the light again during Update. Apply after its LateUpdate.
            foreach (var light in lights)
                if (!Unity.IsNull(light.Reference.Target))
                    SetEnabled(light.Reference.Target, false);
            if (lights.Any(light => Unity.IsNull(light.Reference.Target)))
                RestoreLights();
            reportedFailure = false;
        }
        catch (Exception exception)
        {
            var message = exception.Message;
            try { Dispose(); }
            catch (Exception cleanupFailure) { message += "; restoring headlamp: " + cleanupFailure.Message; }
            discoveryDelay = 60;
            if (!reportedFailure)
                log("Headlamp toggle unavailable: " + message);
            reportedFailure = true;
        }
    }

    static void SetEnabled(nint light, bool enabled) => Unity.Call(light, "set_enabled", (nint)(&enabled));

    void RestoreLights()
    {
        try
        {
            Cleanup.Run(lights.Select(light => (Action)(() =>
            {
                try
                {
                    if (!Unity.IsNull(light.Reference.Target))
                        SetEnabled(light.Reference.Target, light.WasEnabled);
                }
                finally { light.Reference.Dispose(); }
            })).ToArray());
        }
        finally
        {
            lights.Clear();
            rider?.Dispose();
            rider = null;
        }
    }

    public void Dispose()
    {
        try { RestoreLights(); }
        finally { spawner?.Dispose(); spawner = null; }
    }
}
