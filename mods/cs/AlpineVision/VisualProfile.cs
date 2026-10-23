using System.Text.Json.Nodes;
using Jibbers.Sdk;
using JibbersAlpineVision.Weather;

namespace JibbersAlpineVision;

public sealed record VisualProfile(string Name, Color Sun, float SunIntensity, float Elevation,
    Color SkyTop, Color Horizon, Color AmbientSky, Color AmbientGround,
    float Exposure, float Contrast, float Saturation, float Warmth, float Clouds)
{
    public static VisualProfile For(string name) => name switch
    {
        "Natural" => new(name,new(1,1,1,1),1,32,new(.09f,.22f,.40f,1),new(.76f,.83f,.89f,1),new(.5f,.5f,.5f,1),new(.3f,.3f,.3f,1),1,1,1,0,.32f),
        "Golden Hour" => new(name, new(1,.73f,.48f,1),1.35f,17,
            new(.075f,.16f,.31f,1),new(.92f,.56f,.34f,1),new(.32f,.41f,.62f,1),new(.22f,.26f,.38f,1),
            1.06f,1.10f,1.04f,.12f,.40f),
        "Bluebird" => new(name,new(1,.95f,.86f,1),1.30f,48,
            new(.045f,.20f,.46f,1),new(.65f,.81f,.96f,1),new(.45f,.58f,.78f,1),new(.25f,.31f,.43f,1),
            1.0f,1.06f,1.10f,.01f,.16f),
        _ => new("Alpine Film",new(1,.88f,.73f,1),1.18f,32,
            new(.075f,.20f,.38f,1),new(.76f,.83f,.89f,1),new(.39f,.49f,.69f,1),new(.24f,.28f,.39f,1),
            1.04f,1.10f,.98f,.045f,.32f)
    };

    public JsonObject Weather(bool performance, float amount, JsonObject? atmosphereBase=null, bool filmActive=true)
    {
        // Extend the user's Atmosphere weather; a visual-style change must not erase it.
        var v=atmosphereBase is null?AtmosphereConfiguration.Defaults():(JsonObject)atmosphereBase.DeepClone();
        // Weather Off disables weather only; it does not disable the visual renderer.
        foreach(var key in new[]{"fogDensity","hazeDensity","snow","cloudBank","cloudShadows","sunShafts","sunGlow","sparkle","speedStreaks","speedFrost"})
            v[key]=AtmosphereConfiguration.GetFloat(v,key)*amount;
        v["grading"]=true;
        // Preserve the established Alpine Vision filter: one tone mapper owns the image.
        // Natural uses the existing Atmosphere grade; film styles use the established finish.
        if(filmActive && Name!="Natural" && AtmosphereConfiguration.GetBool(v,"grading"))
        {
            v["beautify"]=new JsonObject { ["tonemap"]=0,["tonemapExposurePre"]=1.0,
                ["contrast"]=1.0,["saturate"]=0.0,["colorTempBlend"]=0.0,
                ["bloomIntensity"]=0.0,["sharpenIntensity"]=0.0 };
            v["environment"]=new JsonObject();
        }
        // Never replace native terrain maps, their scale, or the normal strength.
        v["snowSurface"]=false;
        v["cloudBank"]=0; // Valley cloud banks removed; keep fog, storms and sky clouds.
        v["ao"]=0.0; // Leave ambient occlusion/shadows to the game.
        return v;
    }

}

internal sealed class WeatherConfiguration : IAtmosphereSettings
{
    AtmosphereRenderSettings snapshot=new(VisualProfile.For("Alpine Film").Weather(false,1));
    public AtmosphereRenderSettings Capture()=>Volatile.Read(ref snapshot);
    public void Update(JsonObject values)=>Volatile.Write(ref snapshot,new AtmosphereRenderSettings(values));
}

