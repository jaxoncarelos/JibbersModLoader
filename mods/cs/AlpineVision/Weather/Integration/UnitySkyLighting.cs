using Jibbers.Sdk;
using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Map;

namespace JibbersAlpineVision.Weather;

internal static unsafe class UnitySkyLighting
{
    public static Color ReadFogColor()
    {
        var fogColor = new Color(0.8f, 0.85f, 0.95f, 1);
        var readFogColor = (delegate* unmanaged<Color*, void>)Il2Cpp.ICall("UnityEngine.RenderSettings::get_fogColor_Injected");
        if (readFogColor != null)
        {
            readFogColor(&fogColor);
        }

        return fogColor;
    }

    public static (Vector3 Dir, Vector3 Color) ReadSunlight()
    {
        var fallback = (new Vector3(0.3f, 0.8f, 0.5f), new Vector3(1, 0.95f, 0.85f));
        try
        {
            var renderSettingsClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.RenderSettings");
            var light = Unity.CallStatic(renderSettingsClass, "get_sun");
            if (Unity.IsNull(light))
            {
                return fallback;
            }

            var forward = Unity.Call<Vector3>(Unity.Transform(light), "get_forward");
            var lightColor = Unity.Call<Color>(light, "get_color");
            var intensity = Unity.Call<float>(light, "get_intensity");
            return (new Vector3(-forward.X, -forward.Y, -forward.Z), new Vector3(lightColor.R * intensity, lightColor.G * intensity, lightColor.B * intensity));
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    public static Color ReadAmbientLight()
    {
        try
        {
            return Il2Cpp.Unbox<Color>(Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.RenderSettings"), "get_ambientSkyColor"));
        }
        catch (Exception)
        {
            return new Color(0.5f, 0.52f, 0.55f, 1);
        }
    }
}
