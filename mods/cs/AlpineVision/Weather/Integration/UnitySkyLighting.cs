using Jibbers.Sdk;
using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Map;

namespace JibbersAlpineVision.Weather;

internal static unsafe class UnitySkyLighting
{
    static GameRef? fallbackSun;

    /// <summary>The scene's sun, shared by the sky disc and weather glow/shafts. Main thread.</summary>
    public static nint FindSun()
    {
        var renderSettingsClass=Il2Cpp.RequireClass(Unity.Core,"UnityEngine.RenderSettings");
        var light=Unity.CallStatic(renderSettingsClass,"get_sun");
        if(!Unity.IsNull(light))return light;
        if(fallbackSun is not null && !Unity.IsNull(fallbackSun.Target))return fallbackSun.Target;
        ResetSun();
        light=Unity.FindObjects(Unity.Class("UnityEngine.Light"))
            .FirstOrDefault(candidate=>Unity.Call<int>(candidate,"get_type")==1 && Unity.Call<bool>(candidate,"get_enabled"));
        if(!Unity.IsNull(light))fallbackSun=new GameRef(light);
        return light;
    }

    public static void ResetSun()
    {
        fallbackSun?.Dispose();
        fallbackSun=null;
    }

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
        // Without a scene sun, ambient light remains but no directed glow is invented.
        var fallback = (new Vector3(0, 1, 0), default(Vector3));
        try
        {
            var light = FindSun();
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
