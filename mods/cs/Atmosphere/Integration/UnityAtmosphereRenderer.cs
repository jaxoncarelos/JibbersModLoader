using Jibbers.Sdk;
using System.Text.Json.Nodes;

namespace Atmosphere;

/// <summary>One native rendering boundary; shader calculation remains game-independent.</summary>
internal sealed class UnityAtmosphereRenderer : IAtmosphereRenderer
{
    readonly CharacterSparkleMask characterMask = new();
    readonly AtmosphereShaderParameters parameters = new(AtmosphereShader.SetVector);
    public void Apply(AtmosphereRenderSettings settings, AtmosphereFrame frame)
    {
        var maskReady = characterMask.Update(settings.Sparkles.Strength > 0, frame.CameraPosition);
        parameters.Apply(settings, frame with { SparkleMaskReady = maskReady });
    }

    public void Disable()
    {
        characterMask.Update(false, null);
        parameters.Disable();
    }

    public void Dispose() => Cleanup.Run(Disable, characterMask.Dispose);
}
