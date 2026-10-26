using Jibbers.Sdk;
using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Map;

namespace Atmosphere;

internal sealed unsafe class AtmosphereQualitySettings : IAtmosphereQuality
{
    float? originalLodBias;
    int? originalFiltering;
    public void Apply(JsonObject settings)
    {
        if (!AtmosphereConfiguration.GetBool(settings, "terrainDetailBoost"))
        {
            Restore();
            return;
        }
        var qualitySettingsClass = Unity.Class("UnityEngine.QualitySettings");
        var anisotropicLevel = (int)AtmosphereConfiguration.GetFloat(settings, "anisotropic");
        if (anisotropicLevel > 0)
        {
            originalFiltering ??= Il2Cpp.Unbox<int>(Unity.CallStatic(qualitySettingsClass, "get_anisotropicFiltering"));
            var forceEnabled = 2;
            Unity.CallStatic(qualitySettingsClass, "set_anisotropicFiltering", (nint)(&forceEnabled));
        }

        var currentLodBias = Il2Cpp.Unbox<float>(Unity.CallStatic(qualitySettingsClass, "get_lodBias"));
        var requestedLodBias = AtmosphereConfiguration.GetFloat(settings, "lodBias");
        if (requestedLodBias > 0 && originalLodBias is null)
        {
            originalLodBias = currentLodBias;
        }

        float? targetLodBias = requestedLodBias > 0 ? requestedLodBias : originalLodBias;
        if (targetLodBias is { } nextLodBias && MathF.Abs(nextLodBias - currentLodBias) > 1e-4f)
        {
            Unity.CallStatic(qualitySettingsClass, "set_lodBias", (nint)(&nextLodBias));
        }
    }

    public void Restore()
        => Cleanup.Run(RestoreLod, RestoreFiltering);

    void RestoreLod()
    {
        if (originalLodBias is not { } value)
            return;
        Unity.CallStatic(Unity.Class("UnityEngine.QualitySettings"), "set_lodBias", (nint)(&value));
        originalLodBias = null;
    }

    void RestoreFiltering()
    {
        if (originalFiltering is not { } filtering) return;
        Unity.CallStatic(Unity.Class("UnityEngine.QualitySettings"), "set_anisotropicFiltering", (nint)(&filtering));
        originalFiltering = null;
    }

    public void Dispose() => Restore();
}
