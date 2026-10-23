using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;
using JibbersAlpineVision.Rendering;
using JibbersAlpineVision.Weather;
using System.Text.Json.Nodes;

namespace JibbersAlpineVision;

public sealed class AlpineVisionMod : JibbersMod
{
    public override string Id=>"alpinevision";
    public override IReadOnlyList<string> Conflicts=>new[]{"atmosphere","raytracing"};
    public override string Name=>"Alpine Vision";
    public override string Author=>"Jibbers mod community";
    public override string Description=>"Atmosphere extended with alpine sky, lighting, native snow, cinematic finish and optional DXR shadows/AO.";
    public override IEnumerable<Setting> Settings=>new[] {
        Setting.Choice("look","Visual style",new[]{"Natural","Alpine Film","Golden Hour","Bluebird"},"Natural"),
        Setting.Choice("quality","Quality",new[]{"Performance","Balanced","Ultra"},"Balanced"),
        Setting.Toggle("raytracing","Hardware ray tracing (DX12)",true),
        Setting.KeyBinding("rtKey","Toggle hardware ray tracing",(int)Key.F7),
        Setting.Slider("weather","Atmosphere strength",1,0,1.5,.05),
        Setting.Slider("bloom","Bloom",.16,0,.5,.01),
        Setting.Slider("sharpness","Detail sharpening",.18,0,.5,.01),
        Setting.Slider("exposure","Exposure compensation (EV)",0,-1,1,.05)
    }.Concat(weatherSchema??=weatherControls.Schema.ToArray());
    readonly WeatherConfiguration configuration=new();
    AtmosphereRuntime? weather;
    VisualRenderer? visuals;
    RayTracer? rays;
    bool unloaded;
    bool? hardwareReady;
    bool finishReady;
    JsonObject? atmosphereBase;
    int settingsChanged;
    int synchronizingWeather;
    readonly AtmosphereConfigurationStore weatherControls;
    Setting[]? weatherSchema;
    static readonly HashSet<string> WeatherKeys=new(StringComparer.Ordinal) {
        "preset","hotkeys","cycleKey","fog","fogStart","snow","snowSize","speedStreaks",
        "cloudBank","cloudBankDistance","sunShafts","grading","terrainDetailBoost"
    };
    public AlpineVisionMod()=>weatherControls=new(WeatherPath,Log);
    string WeatherPath()
    {
        var path=Path.Combine(Bootstrap.GameDir,"alpinevision_weather.json");
        var previous=Path.Combine(Bootstrap.GameDir,"jibbers_atmosphere.json");
        if(!File.Exists(path) && File.Exists(previous))File.Copy(previous,path);
        return path;
    }
    void SynchronizeWeatherMenu()
    {
        if(Interlocked.Exchange(ref synchronizingWeather,1)!=0)return;
        MainThread.Later(()=>
        {
            try{if(!unloaded)weatherControls.SynchronizeMenu(SetSetting);}
            finally{Volatile.Write(ref synchronizingWeather,0);}
        });
    }
    public override void OnLoad()
    {
        atmosphereBase=weatherControls.Capture().Values;
        ApplySettings();
        visuals=new VisualRenderer(Log);
        weather=new AtmosphereRuntime(configuration,AtmosphereGameServices.Create(Log,()=>visuals.Bundle),()=>Enabled,Log);
        weather.Start();
        Follow.LateUpdateMethod.Around((self,original)=>
        {
            original(); if(unloaded || !Enabled) return;
            if(Interlocked.Exchange(ref settingsChanged,0)!=0)weather?.ConfigurationChanged(false);
            var profile=VisualProfile.For(GetString("look"));
            var quality=GetString("quality");
            visuals.Frame(profile,quality,(float)GetDouble("bloom"),(float)GetDouble("sharpness"),(float)GetDouble("exposure"),GetBool("grading"));
            if(finishReady!=visuals.IsRendering)
            {
                finishReady=visuals.IsRendering;ApplySettings();weather?.ConfigurationChanged(false);
            }
            if(!visuals.Ready) return;
            rays??=new RayTracer(Log,visuals.Bundle);
            var rt=GetBool("raytracing");
            rays.Frame(rt,new(.58f,.38f,1.2f,.008f,quality=="Ultra"?.65f:quality=="Performance"?.3f:.45f,
                quality=="Ultra"?6:quality=="Performance"?1:3,false));
            var ready=rt && rays.HardwareReady;
            if(hardwareReady!=ready)
            {
                hardwareReady=ready;
                ApplySettings();
                weather?.ConfigurationChanged(false);
            }
        });
        Jibbers.Sdk.Game.Map.LoadEvent.Subscribe(_=>Cleanup.Run(()=>visuals?.SceneChanged(),()=>rays?.SceneChanged()));
        Input.OnSetting(this,"rtKey",()=>
        {
            var on=!GetBool("raytracing");
            SetSetting("raytracing",System.Text.Json.Nodes.JsonValue.Create(on)!);
            Log(on?"Hardware ray tracing requested ON; status will be reported after submission.":"Hardware ray tracing OFF; restored normal sun shadows.");
        });
        Input.OnSetting(this,"cycleKey",()=>
        {
            if(!GetBool("hotkeys"))return;
            weatherControls.ApplyCommand(new JsonObject { ["nextPreset"]=true });
            ApplySettings();Interlocked.Exchange(ref settingsChanged,1);SynchronizeWeatherMenu();
        });
        SynchronizeWeatherMenu();
        Log("Alpine Vision 1.1.2 loaded: scene-safe owned materials, native snow, Atmosphere controls/F10, no valley cloud banks.");
    }
    void ApplySettings()
    {
        atmosphereBase=weatherControls.Capture().Values;
        configuration.Update(VisualProfile.For(GetString("look")).Weather(
            GetString("quality")=="Performance",GetBool("raytracing") && hardwareReady==true,(float)GetDouble("weather"),atmosphereBase,finishReady));
    }
    public override void OnSettingChanged(string key,JsonNode value)
    {
        if(WeatherKeys.Contains(key))
        {
            var change=weatherControls.ApplyMenuSetting(key,value);
            if(!change.Changed)return;
            if(change.SynchronizeMenu)SynchronizeWeatherMenu();
        }
        ApplySettings();
        if(key is "look" or "preset")Log($"Requested {key}={value}; queued for the next rendered frame.");
        // The Mods menu may call this on the native main thread outside an SDK callback.
        // Never synchronously wait for that same thread (or wait while settings locks are held).
        Interlocked.Exchange(ref settingsChanged,1);
        if(key=="enabled")MainThread.Later(()=>
        {
            if(unloaded)return;
            if(!Enabled)Cleanup.Run(()=>rays?.Suspend(),()=>weather?.Disable(),()=>visuals?.Suspend());
        });
    }
    public override JsonNode? OnCommand(string cmd,JsonObject request)=>cmd!="alpinevision.status"?null:
        MainThread.Run(()=>(JsonNode)new JsonObject {
            ["enabled"]=Enabled,["look"]=GetString("look"),["quality"]=GetString("quality"),
            ["weather_preset"]=AtmosphereModes.Current(weatherControls.Capture().Values),
            ["visuals"]=visuals?.Status,["raytracing"]=rays?.Status,
            ["weather_spawned"]=weather?.Status.Spawned,["weather_error"]=weather?.Status.Error });
    public override void OnUnload()
    {
        unloaded=true; MainThread.Run(()=>Cleanup.Run(()=>rays?.Dispose(),()=>weather?.Dispose(),()=>visuals?.Dispose()));
    }
}

