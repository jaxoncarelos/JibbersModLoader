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
    public override string Description=>"Atmosphere extended with alpine sky, lighting, native snow and a URP cinematic finish. Game shadows are retained.";
    public override IEnumerable<Setting> Settings=>new[] {
        Setting.Choice("look","Visual style",new[]{"Natural","Alpine Film","Golden Hour","Bluebird"},"Natural"),
        Setting.Choice("quality","Quality",new[]{"Performance","Balanced","Ultra"},"Balanced"),
        Setting.Slider("weather","Atmosphere strength",1,0,1.5,.05),
        Setting.Slider("bloom","Bloom",.16,0,.5,.01),
        Setting.Slider("sharpness","Detail sharpening",.18,0,.5,.01),
        Setting.Slider("exposure","Exposure compensation (EV)",0,-1,1,.05)
    }.Concat(weatherSchema??=weatherControls.Schema.ToArray());
    readonly WeatherConfiguration configuration=new();
    AtmosphereRuntime? weather;
    VisualRenderer? visuals;
    bool unloaded;
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
        });
        Jibbers.Sdk.Game.Map.LoadEvent.Subscribe(_=>visuals?.SceneChanged());
        Input.OnSetting(this,"cycleKey",()=>
        {
            if(!GetBool("hotkeys"))return;
            weatherControls.ApplyCommand(new JsonObject { ["nextPreset"]=true });
            ApplySettings();Interlocked.Exchange(ref settingsChanged,1);SynchronizeWeatherMenu();
        });
        SynchronizeWeatherMenu();
        Log("Alpine Vision 1.2.1 loaded: URP finish, native snow, Atmosphere controls/F10; game shadows retained, no ray tracing or valley cloud banks.");
    }
    void ApplySettings()
    {
        atmosphereBase=weatherControls.Capture().Values;
        configuration.Update(VisualProfile.For(GetString("look")).Weather(
            GetString("quality")=="Performance",(float)GetDouble("weather"),atmosphereBase,finishReady));
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
            if(!Enabled)Cleanup.Run(()=>weather?.Disable(),()=>visuals?.Suspend());
        });
    }
    public override JsonNode? OnCommand(string cmd,JsonObject request)=>cmd!="alpinevision.status"?null:
        MainThread.Run(()=>(JsonNode)new JsonObject {
            ["enabled"]=Enabled,["look"]=GetString("look"),["quality"]=GetString("quality"),
            ["weather_preset"]=AtmosphereModes.Current(weatherControls.Capture().Values),
            ["visuals"]=visuals?.Status,
            ["weather_spawned"]=weather?.Status.Spawned,["weather_error"]=weather?.Status.Error });
    public override void OnUnload()
    {
        unloaded=true; MainThread.Run(()=>Cleanup.Run(()=>weather?.Dispose(),()=>visuals?.Dispose()));
    }
}

