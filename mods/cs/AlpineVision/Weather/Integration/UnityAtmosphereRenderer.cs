using Jibbers.Sdk;
using System.Text.Json.Nodes;

namespace JibbersAlpineVision.Weather;

/// <summary>One native rendering boundary; shader calculation remains game-independent.</summary>
internal sealed class UnityAtmosphereRenderer : IAtmosphereRenderer
{
    readonly AtmosphereShaderParameters parameters = new(AtmosphereShader.SetVector);

    // Snow glitter is switched off: its character mask scanned the whole scene for skinned renderers
    // every 30 frames, a 40-50 ms hitch on big maps (~100k objects). SparkleMaskReady = false makes
    // the shader draw no glitter.
    public void Apply(AtmosphereRenderSettings settings, AtmosphereFrame frame) =>
        parameters.Apply(settings, frame with { SparkleMaskReady = false });

    public void Disable() => parameters.Disable();

    public void Dispose() => Cleanup.Run(Disable);
}
