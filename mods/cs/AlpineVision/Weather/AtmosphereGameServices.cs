using Jibbers.Sdk;
namespace JibbersAlpineVision.Weather;

/// <summary>Composition root and ownership for the independent scene/effect adapters.</summary>
internal sealed record AtmosphereGameServices(IAtmosphereScene Scene, IAtmospherePrefab Prefab, IAtmosphereSurface Surface, IAtmosphereQuality Quality, IAtmosphereGrading Grading, IAtmosphereRenderer Renderer) : IDisposable
{
    public static AtmosphereGameServices Create(Action<string> log, Func<AssetBundles.Bundle> loadBundle)
    {
        var scene = new UnityAtmosphereScene();
        return new(scene, new AtmospherePrefab(log, loadBundle), new NativeSnowSurface(), new AtmosphereQualitySettings(), new AtmosphereGradingAdapter(scene), new UnityAtmosphereRenderer());
    }

    public void Dispose() => Cleanup.Run(Scene.Dispose, Surface.Dispose, Quality.Dispose, Grading.Dispose, Renderer.Dispose, Prefab.Dispose);
}

// Native terrain textures and material properties remain entirely owned by the game.
internal sealed class NativeSnowSurface : IAtmosphereSurface
{
    public void Apply(System.Text.Json.Nodes.JsonObject settings,bool enabled) { }
    public void Invalidate() { }
    public void Restore() { }
    public void Dispose() { }
}
