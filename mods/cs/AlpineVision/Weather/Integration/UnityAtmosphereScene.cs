using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Map;

namespace JibbersAlpineVision.Weather;

/// <summary>Adapts environment updates and scene measurements to immutable frame data.</summary>
internal sealed class UnityAtmosphereScene : IAtmosphereScene
{
    readonly AtmosphereCameraTracker camera = new();
    readonly AtmosphereTerrainTracker terrain = new();
    Subscription? mapSubscription;
    string? currentMap;
    bool active;
    public nint EnvironmentManager { get; private set; }

    public void Start(Action frame)
    {
        mapSubscription = LoadEvent.Subscribe(loadEvent =>
        {
            try
            {
                currentMap = loadEvent.Map.Id;
            }
            catch
            {
                currentMap = null;
            }
        });
        Manager.UpdateMethod.Before((environmentManager, _) =>
        {
            if (!active)
                return;
            EnvironmentManager = environmentManager;
            NightCycle.Observe(environmentManager);
            frame();
        });
        active = true;
    }

    public AtmosphereFrame Capture()
    {
        terrain.Refresh(currentMap);
        var position = camera.ReadPosition();
        var(sunDirection, sunColor) = UnitySkyLighting.ReadSunlight();
        return new()
        {
            FogColor = UnitySkyLighting.ReadFogColor(),
            SunDirection = sunDirection,
            SunColor = sunColor,
            AmbientLight = UnitySkyLighting.ReadAmbientLight(),
            CameraPosition = position,
            CameraVelocity = camera.MeasureVelocity(position),
            TerrainFloor = terrain.Floor,
            TerrainCeiling = terrain.Ceiling,
            MapRectangle = terrain.Rectangle,
        };
    }

    public void Dispose()
    {
        active = false;
        var subscription = mapSubscription;
        mapSubscription = null;
        Cleanup.Run(() => subscription?.Dispose(), camera.Dispose, UnitySkyLighting.ResetSun, NightCycle.Reset);
    }
}
