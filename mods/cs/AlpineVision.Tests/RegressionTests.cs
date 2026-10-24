using Jibbers.Sdk;
using JibbersAlpineVision;
using JibbersAlpineVision.Weather;
using JibbersAlpineVision.Rendering;
using System.Text.Json.Nodes;

static class RegressionTests
{
    public static void Run()
    {
        if(NightCycle.CalculateNightBlend(.9f,.6f,.1f)!=1 || NightCycle.CalculateNightBlend(.2f,.6f,.1f)!=0)
            throw new Exception("Native night time is interpreted as daylight");
        var twilight=NightCycle.CalculateNightBlend(.65f,.6f,.1f);
        if(twilight<.49f || twilight>.51f)throw new Exception("Twilight does not blend smoothly");
        foreach(var look in new[]{"Natural","Alpine Film","Golden Hour","Bluebird"})
        foreach(var nightStyle in new[]{"Moonlit","Night Skiing"})
        {
            var day=VisualProfile.For(look);
            var night=day.AtNight(1,nightStyle);
            if(night.NightAmount!=1 || night.Exposure>day.Exposure || Math.Max(night.SkyTop.R,Math.Max(night.SkyTop.G,night.SkyTop.B))>.05f)
                throw new Exception("Night palette restores daytime sky brightness: "+look+" / "+nightStyle);
            if(day.NightAmount!=0 || day.SkyTop==night.SkyTop)throw new Exception("Day profile mutated by night adaptation");
            var daylight=day.AtNight(0,nightStyle);
            if(daylight.SkyTop!=day.SkyTop || daylight.Horizon!=day.Horizon || daylight.Exposure!=day.Exposure)
                throw new Exception("Night palette changed daylight rendering");
        }
        Console.WriteLine("PASS native night/twilight blending and both dark night palettes across all visual styles; daylight preserved");
        var ramp=Enumerable.Range(0,25).Select(index=>(float)(index%5)/4).ToArray();
        var shadowMesh=TerrainShadowMesh.Build(ramp,5,5,4,4,4,1);
        if(shadowMesh.Vertices.Length!=25 || shadowMesh.Triangles.Length!=96 || shadowMesh.Triangles.Any(index=>index<0 || index>=25))
            throw new Exception("Terrain shadow topology is invalid");
        for(var index=0;index<shadowMesh.Vertices.Length;index++)
        {
            var vertex=shadowMesh.Vertices[index];
            if(Math.Abs(vertex.Y-vertex.X)>.00001f || Math.Abs(shadowMesh.Normals[index].X+.7071068f)>.0001f)
                throw new Exception("Terrain shadow geometry or normals differ from the source ramp");
        }
        var coarseMesh=TerrainShadowMesh.Build(ramp,5,5,4,4,4,2);
        if(coarseMesh.Vertices.Length!=9 || coarseMesh.Vertices[^1]!=shadowMesh.Vertices[^1])
            throw new Exception("Terrain shadow simplification loses the boundary");
        Console.WriteLine("PASS shadow tile topology, ramp heights/normals, simplified boundaries and valid index ranges");
        var alignedSun=SunAlignment.FromLightForward(new(0,-3,4));
        if(Math.Abs(alignedSun.Y-.6f)>.00001f || Math.Abs(alignedSun.Z+.8f)>.00001f || alignedSun.W!=1)
            throw new Exception("Sun enhancement does not follow the native light direction");
        foreach(var invalidForward in new[]{default(Vector3),new Vector3(float.NaN,0,0),new Vector3(0,float.PositiveInfinity,0)})
            if(SunAlignment.FromLightForward(invalidForward).W!=0)
                throw new Exception("An unavailable sun creates an invented disc");
        Console.WriteLine("PASS sun enhancement follows the native light direction; unavailable/invalid directions disable the disc");
        var mod=new AlpineVisionMod();
        // No running SDK/main-thread dispatcher: preset changes must publish data and return.
        var changes=Task.Run(()=>{for(int i=0;i<100;i++)mod.OnSettingChanged("look",JsonValue.Create("Golden Hour")!);});
        if(!changes.Wait(TimeSpan.FromSeconds(1)))throw new Exception("Preset handler blocks its caller");
        Console.WriteLine("PASS 100 preset changes return without native/main-thread dispatch");
        var original=AtmosphereConfiguration.Defaults();original["snow"]=.63;original["sunShafts"]=.8;
        var before=original.ToJsonString();
        foreach(var name in new[]{"Alpine Film","Golden Hour","Bluebird"})
        {
            var extended=VisualProfile.For(name).Weather(false,1,original,false);
            foreach(var key in new[]{"snow","snowSize","snowOpacity","fogDensity","hazeDensity","cloudBank","sparkle","sunShafts","speedStreaks","wind","beautify"})
                if(extended[key]!.ToJsonString()!=original[key]!.ToJsonString() && key is "wind" or "beautify")throw new Exception("Old weather/grade replaced: "+key);
            var settings=new AtmosphereRenderSettings(extended);
            if(Math.Abs(settings.Snow.Amount-.63f)>.001f || Math.Abs(settings.SunShafts.Strength-.8f)>.001f || settings.CloudBank.Strength!=0 || settings.Sparkles.Strength!=1)
                throw new Exception("Visual style thinned out prior Atmosphere effects");
        }
        if(original.ToJsonString()!=before)throw new Exception("Imported settings were mutated");
        Console.WriteLine("PASS every visual style extends existing snowfall, clouds, glitter and shafts; original configuration remains intact");
        new NativeSnowSurface().Apply(original,true); // Must work without Unity: never replaces any terrain property.
        foreach(var name in new[]{"Natural","Alpine Film","Golden Hour","Bluebird"})
            if(VisualProfile.For(name).Weather(false,1,original)["snowSurface"]!.GetValue<bool>())
                throw new Exception("A visual preset re-enabled replacement snow");
        Console.WriteLine("PASS native snow surface makes no engine calls and every style keeps replacement maps disabled");
        var testDir=Path.Combine(Path.GetTempPath(),"AlpineVision-weather-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var file=Path.Combine(testDir,"weather.json");
        var controls=new AtmosphereConfigurationStore(()=>file,_=>{});
        var keys=controls.Schema.Select(x=>x.Key).ToHashSet();
        foreach(var key in new[]{"preset","hotkeys","cycleKey","fog","fogStart","snow","snowSize","speedStreaks","sunShafts","grading","terrainDetailBoost"})
            if(!keys.Contains(key))throw new Exception("Missing original Atmosphere control: "+key);
        controls.ApplyMenuSetting("preset",JsonValue.Create("Clear")!);var clear=controls.Capture();
        controls.ApplyMenuSetting("preset",JsonValue.Create("Foggy")!);var foggy=controls.Capture();
        controls.ApplyMenuSetting("preset",JsonValue.Create("Snowstorm")!);var storm=controls.Capture();
        if(foggy.Fog.HazeDensity<=clear.Fog.HazeDensity || storm.Snow.Amount<.99f || storm.Snow.Wind.X<5.9f)
            throw new Exception("Weather modes do not produce distinct fog/storm conditions");
        foreach(var name in new[]{"Natural","Alpine Film","Golden Hour","Bluebird"})
        {
            var combined=new AtmosphereRenderSettings(VisualProfile.For(name).Weather(false,1,storm.Values));
            if(combined.Snow.Amount!=storm.Snow.Amount || combined.Fog.Density!=storm.Fog.Density || combined.Snow.Wind!=storm.Snow.Wind)
                throw new Exception("Visual style replaced storm weather");
        }
        controls.ApplyMenuSetting("preset",JsonValue.Create("Off")!);
        if(VisualProfile.For("Alpine Film").Weather(false,1,controls.Capture().Values)["enabled"]!.GetValue<bool>())
            throw new Exception("Weather Off was overridden by visual profile");
        controls.ApplyMenuSetting("preset",JsonValue.Create("Foggy")!);
        controls.ApplyMenuSetting("fog",JsonValue.Create(.8)!);
        var reloaded=new AtmosphereConfigurationStore(()=>file,_=>{});_=reloaded.Schema.ToArray();
        if(Math.Abs(reloaded.Capture().Fog.HazeDensity-.0096f)>.00001f)throw new Exception("Weather settings did not persist");
        Console.WriteLine("PASS all 11 retained Atmosphere controls (valley banks removed), distinct Clear/Foggy/Snowstorm, Off, independent look/weather combinations, and persistence");
        var scene=new Scene();var renderer=new Renderer();var prefab=new Prefab();var quality=new Quality();var surface=new Surface();
        using var weather=new AtmosphereRuntime(new Settings(),new(scene,prefab,surface,quality,new Grade(),renderer),()=>true,_=>{});
        weather.Start();scene.Tick!();scene.Tick!();
        if(renderer.Frames!=2 || renderer.Disabled || !prefab.Spawned || quality.Attempts!=1 || surface.Attempts!=1 || weather.Status.Error is not null)
            throw new Exception("Optional engine error stopped the weather");
        Console.WriteLine("PASS stripped quality/surface APIs do not stop weather prefab, snowfall or later frames");
    }
    sealed class Settings:IAtmosphereSettings {public AtmosphereRenderSettings Capture()=>new(AtmosphereConfiguration.Defaults());}
    sealed class Scene:IAtmosphereScene {public Action? Tick;public void Start(Action frame)=>Tick=frame;public AtmosphereFrame Capture()=>default;public void Dispose(){} }
    sealed class Prefab:IAtmospherePrefab {public bool Spawned{get;private set;}public void EnsureSpawned()=>Spawned=true;public void RequestRespawn(){}public void Dispose(){} }
    sealed class Quality:IAtmosphereQuality {public int Attempts;public void Apply(JsonObject settings){Attempts++;throw new NotSupportedException("stripped anisotropic getter");}public void Restore(){}public void Dispose(){} }
    sealed class Surface:IAtmosphereSurface {public int Attempts;public void Apply(JsonObject settings,bool enabled){Attempts++;throw new NotSupportedException("optional material setter unavailable");}public void Invalidate(){}public void Restore(){}public void Dispose(){} }
    sealed class Grade:IAtmosphereGrading {public void Apply(JsonObject settings,bool enabled){}public void Restore(){}public void Dispose(){} }
    sealed class Renderer:IAtmosphereRenderer {public int Frames;public bool Disabled;public void Apply(AtmosphereRenderSettings settings,AtmosphereFrame frame){if(settings.Snow.Amount<=0)throw new Exception("No snowfall");Frames++;}public void Disable()=>Disabled=true;public void Dispose(){} }
}
