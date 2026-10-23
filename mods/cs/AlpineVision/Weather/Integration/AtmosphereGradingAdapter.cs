using Jibbers.Sdk;
using System.Text.Json.Nodes;

namespace JibbersAlpineVision.Weather;

internal sealed class AtmosphereGradingAdapter(UnityAtmosphereScene scene) : IAtmosphereGrading
{
    readonly AtmosphereColorGrading grading = new();
    public void Apply(JsonObject settings, bool enabled) => grading.Apply(scene.EnvironmentManager, enabled, settings);
    public void Restore() => grading.Restore();
    public void Dispose() => grading.Dispose();
}
