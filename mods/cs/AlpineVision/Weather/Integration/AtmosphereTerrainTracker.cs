using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Map;

namespace JibbersAlpineVision.Weather;

/// <summary>Maintains terrain bounds across map changes; expensive sampling retries are bounded.</summary>
internal sealed class AtmosphereTerrainTracker
{
    string? measuredMap;
    bool measured;
    int framesUntilRetry;
    public float Floor { get; private set; }
    public float Ceiling { get; private set; }
    public Vector4 Rectangle { get; private set; }

    public void Refresh(string? currentMap)
    {
        if (measured && measuredMap == currentMap)
            return;
        if (framesUntilRetry-- > 0)
            return;
        if (UnityTerrain.MeasureTerrainBounds()is { } bounds)
        {
            Floor = bounds.MinimumHeight;
            Ceiling = bounds.MaximumHeight;
            Rectangle = bounds.Rectangle;
            measuredMap = currentMap;
            measured = true;
        }
        else
            framesUntilRetry = 300;
    }
}
