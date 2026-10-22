namespace Jibbers.Sdk.Features.Atmosphere;

/// <summary>Composition root and ownership for the independent scene/effect adapters.</summary>
internal sealed record AtmosphereGameServices(IAtmosphereScene Scene, IAtmospherePrefab Prefab, IAtmosphereSurface Surface, IAtmosphereQuality Quality, IAtmosphereGrading Grading, IAtmosphereRenderer Renderer) : IDisposable
{
    public static AtmosphereGameServices Create(Action<string> log)
    {
        var scene = new UnityAtmosphereScene();
        return new(scene, new AtmospherePrefab(log), new AtmosphereSnowSurface(log), new AtmosphereQualitySettings(), new AtmosphereGradingAdapter(scene), new UnityAtmosphereRenderer());
    }

    public void Dispose() => Cleanup.Run(Scene.Dispose, Surface.Dispose, Quality.Dispose, Grading.Dispose, Renderer.Dispose, Prefab.Dispose);
}
