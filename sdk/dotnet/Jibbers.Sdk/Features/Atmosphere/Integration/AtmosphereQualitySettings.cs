using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Map;

namespace Jibbers.Sdk.Features.Atmosphere;

internal sealed unsafe class AtmosphereQualitySettings : IAtmosphereQuality
{
    float? originalLodBias;
    public void Apply(JsonObject settings)
    {
        var anisotropicLevel = (int)AtmosphereConfiguration.GetFloat(settings, "anisotropic");
        if (anisotropicLevel > 0)
        {
            var setAnisotropicFiltering = (delegate* unmanaged<int, void>)Il2Cpp.ICall("UnityEngine.QualitySettings::set_anisotropicFiltering");
            if (setAnisotropicFiltering != null)
            {
                setAnisotropicFiltering(2); // ForceEnable
            }

            var setAnisotropicLimits = (delegate* unmanaged<int, int, void>)Il2Cpp.ICall("UnityEngine.Texture::SetGlobalAnisotropicFilteringLimits");
            if (setAnisotropicLimits != null)
            {
                var anisotropicLimit = Math.Clamp(anisotropicLevel, 1, 16);
                setAnisotropicLimits(anisotropicLimit, anisotropicLimit);
            }
        }

        var qualitySettingsClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.QualitySettings");
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
    {
        if (originalLodBias is not { } value)
            return;
        Unity.CallStatic(Unity.Class("UnityEngine.QualitySettings"), "set_lodBias", (nint)(&value));
        originalLodBias = null;
    }

    public void Dispose() => Restore();
}
