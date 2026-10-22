// Atmosphere (C#): height fog, horizon haze, sun glow, falling snow, cloud shadows and banks, ice
// glints, sun shafts and colour grading, so maps feel less empty. Port of the Rust Atmosphere.
//
// The game's build strips Unity's fog shader variants, so the effects come from our own shaders in
// jibbers_atmosphere.bundle (mods/JibbersAtmosphere/unity). The bundle holds one prefab whose meshes
// the shaders place around whichever camera renders them; it is spawned once (DontDestroyOnLoad) and
// driven through shader globals from a pre-hook on EnvironmentManager.Update, with colours from the
// game's own day/night sky. Settings: jibbers_atmosphere.json beside the game (as the Rust mod), live.

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Map;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Atmosphere;

public sealed unsafe class AtmosphereEffects : ModFeature
{
    public override string Id => "atmosphere";
    public override string Name => "Atmosphere";
    public override string Author => "Jaxon";
    public override string Description => "Fog, snowfall, valley clouds and colour grading.";

    const string BundleFile = "jibbers_atmosphere.bundle";
    const string SettingsFile = "jibbers_atmosphere.json";
    const string Prefab = "JibbersAtmosphere";

    JsonObject configuration = AtmoConfig.Defaults();
    readonly object gate = new();
    bool syncing;

    string SettingsPath => Path.Combine(Bootstrap.GameDir.Length > 0 ? Bootstrap.GameDir : AppContext.BaseDirectory, SettingsFile);

    void LoadConfig()
    {
        try
        {
            var path = SettingsPath;
            configuration = AtmoConfig.Load(File.Exists(path) ? File.ReadAllText(path) : null);
            if (!File.Exists(path))
            {
                Save();
            }
        }
        catch (Exception e)
        {
            Log($"{SettingsFile} is invalid ({e.Message}); using defaults");
            configuration = AtmoConfig.Defaults();
        }
    }

    void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, AtmoConfig.Serialize(configuration));
        }
        catch (Exception e)
        {
            Log($"could not save {SettingsFile}: {e.Message}");
        }
    }

    // ---- Mods tab: the same 14 controls as the Rust mod ----------------------------------------

    public override IEnumerable<Setting> Settings
    {
        get
        {
            LoadConfig();
            var presets = AtmoConfig.PresetOrder(configuration["presets"] as JsonObject ?? new JsonObject());
            if (presets.Count == 0)
            {
                presets.Add("Foggy day");
            }
            return new[]
            {
                Setting.Choice("preset", "Preset", presets, presets.Contains(configuration["preset"]?.GetValue<string>() ?? "") ? configuration["preset"]!.GetValue<string>() : presets[0]).WithTooltip("A named set of fog, snow and sky values."),
                Setting.Toggle("hotkeys", "Hotkeys", true).WithTooltip("Keyboard shortcuts, while the game has focus."),
                Setting.KeyBinding("toggleKey", "Toggle key", (int)Key.F3).WithTooltip("Key that turns the atmosphere on or off."),
                Setting.KeyBinding("presetKey", "Preset key", (int)Key.F4).WithTooltip("Key that goes to the next preset."),
                Setting.Slider("fog", "Fog thickness", 0.33, 0, 1, 0.05).InSection("Fog").WithTooltip("How thick the haze is, near and far."),
                Setting.Slider("fogStart", "Fog start", 5, 0, 60, 1).InSection("Fog").WithTooltip("Distance (m) before any fog, so nearby ground stays crisp."),
                Setting.Slider("snow", "Snowfall", 0.7, 0, 1, 0.05).InSection("Snow").WithTooltip("Fraction of the snowflakes shown."),
                Setting.Slider("snowSize", "Flake size", 0.035, 0.01, 0.08, 0.005).InSection("Snow").WithTooltip("Radius of a snowflake (m)."),
                Setting.Slider("speedStreaks", "Speed streaks", 1, 0, 2, 0.1).InSection("Snow").WithTooltip("How much falling snow streaks with your speed."),
                Setting.Slider("cloudBank", "Valley clouds", 1, 0, 2, 0.1).InSection("Sky").WithTooltip("Cloud sea below the rider, following your elevation."),
                Setting.Slider("cloudBankDistance", "Cloud distance below", 180, 60, 1000, 10).InSection("Sky").WithTooltip("Metres from your camera down to the cloud tops."),
                Setting.Slider("sunShafts", "Sun shafts", 0, 0, 2, 0.1).InSection("Sky").WithTooltip("Light shafts around the sun."),
                Setting.Toggle("grading", "Colour grading", true).InSection("Colour").WithTooltip("Filmic tonemapping, white balance and bloom."),
            };
        }
    }

    /// <summary>The Mods tab shows the JSON file's values (it is the source of truth).</summary>
    void SyncTab()
    {
        syncing = true;
        try
        {
            SetSetting("enabled", AtmoConfig.B(configuration, "enabled"));
            if (configuration["preset"] is JsonValue p)
            {
                try
                {
                    SetSetting("preset", p.DeepClone());
                }
                catch (ArgumentException)
                {
                    // a preset the tab does not list yet
                }
            }
            foreach (var k in new[] { "hotkeys", "grading" })
            {
                SetSetting(k, AtmoConfig.B(configuration, k));
            }
            foreach (var k in new[] { "toggleKey", "presetKey" })
            {
                SetSetting(k, (int)AtmoConfig.F(configuration, k));
            }
            SetSetting("fog", Math.Round(AtmoConfig.FogValue(configuration), 4));
            foreach (var k in new[] { "fogStart", "snow", "snowSize", "speedStreaks", "cloudBank", "cloudBankDistance", "sunShafts" })
            {
                SetSetting(k, Math.Round(AtmoConfig.F(configuration, k), 4));
            }
        }
        finally
        {
            syncing = false;
        }
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (syncing)
        {
            return;
        }
        lock (gate)
        {
            if (key == "preset")
            {
                configuration = AtmoConfig.WithPreset(configuration, value.GetValue<string>());
            }
            else if (key == "fog")
            {
                AtmoConfig.Merge(configuration, AtmoConfig.FogFields((float)value.GetValue<double>()));
            }
            else
            {
                configuration[key] = value.DeepClone();
            }
            gradingDirty = true;
            surfaceCheckIn = 0;
            loadError = null;
            Save();
        }
        if (key == "preset")
        {
            MainThread.Later(SyncTab);
        }
        if (key == "enabled" && !value.GetValue<bool>())
        {
            // The SDK skips callbacks for disabled mods; reset effects before those callbacks stop.
            MainThread.Run(DisableEffects);
        }
    }

    /// <summary><c>{"cmd":"atmosphere.set", ...any settings, "preset": name, "nextPreset": true, "toggle": true, "respawn": true}</c>.</summary>
    public override JsonNode? OnCommand(string cmd, JsonObject req)
    {
        switch (cmd)
        {
            case "atmosphere.status":
                return new JsonObject { ["preset"] = configuration["preset"]?.DeepClone(), ["spawned"] = spawned, ["floor"] = floor, ["cameraKmh"] = camVel.Magnitude * 3.6f, ["error"] = loadError };
            case "atmosphere.set":
                Apply(req);
                return new JsonObject { ["settings"] = configuration.DeepClone() };
            default:
                return null;
        }
    }

    void Apply(JsonObject req)
    {
        lock (gate)
        {
            var fields = (JsonObject)req.DeepClone();
            fields.Remove("cmd");
            string? chosen = null;
            if (fields["nextPreset"] is JsonValue np && np.GetValue<bool>())
            {
                chosen = AtmoConfig.NextPreset(configuration);
            }
            else if (fields["preset"] is JsonValue p)
            {
                chosen = p.GetValue<string>();
            }
            fields.Remove("preset");
            if (chosen is not null)
            {
                configuration = AtmoConfig.WithPreset(configuration, chosen);
            }
            AtmoConfig.Merge(configuration, fields);
            if (fields["toggle"] is JsonValue t && t.GetValue<bool>())
            {
                configuration["enabled"] = !AtmoConfig.B(configuration, "enabled");
            }
            if (fields["respawn"] is JsonValue r && r.GetValue<bool>())
            {
                respawn = true;
            }
            gradingDirty = true;
            surfaceCheckIn = 0;
            loadError = null;
            Save();
        }
        MainThread.Later(SyncTab);
    }

    // ---- per frame ---------------------------------------------------------------------------

    bool spawned, respawn, gradingDirty;
    int checkIn, floorRetry, qualityIn, surfaceCheckIn, camCheckIn;
    string? loadError;
    string? floorMap;
    float? floor;
    float terrainTop;
    float[] rect = new float[4];
    string? mapId;
    GameRef? cam;
    Vector3? lastCam;
    Vector3 camVel;
    float? gameLodBias;

    public override void OnLoad()
    {
        LoadEvent.Subscribe(e =>
        {
            try
            {
                mapId = e.Map.Id;
            }
            catch (Exception)
            {
                mapId = null;
            }
        });
        Manager.UpdateMethod.Before((env, _) => Frame(env));
        Input.OnSetting(Owner, "toggleKey", () =>
        {
            if (GetBool("hotkeys"))
            {
                Apply(new JsonObject { ["toggle"] = true });
                Log($"hotkey: enabled {AtmoConfig.B(configuration, "enabled")}");
            }
        });
        Input.OnSetting(Owner, "presetKey", () =>
        {
            if (GetBool("hotkeys"))
            {
                Apply(new JsonObject { ["nextPreset"] = true });
                Log($"hotkey: preset {configuration["preset"]}");
            }
        });
        MainThread.Later(SyncTab);
    }

    void Frame(nint env)
    {
        JsonObject s;
        lock (gate)
        {
            s = (JsonObject)configuration.DeepClone();
        }
        var on = Enabled && AtmoConfig.B(s, "enabled");
        if (gradingDirty || !(on && AtmoConfig.B(s, "grading")))
        {
            Grading.Apply(env, false, s);
            gradingDirty = false;
        }
        SnowSurface(s, on);
        if (!on)
        {
            Sparkle.Update(false, null);
            foreach (var name in new[] { "_JA_FogColor", "_JA_Snow", "_JA_Cloud", "_JA_AO", "_JA_Sparkle", "_JA_Shafts", "_JA_Speed", "_JA_Bank" })
            {
                Shader.Vec(name, new Vector4(0, 0, 0, 0));
            }
            return;
        }
        if (AtmoConfig.B(s, "grading"))
        {
            Grading.Apply(env, true, s);
        }
        if (qualityIn-- <= 0)
        {
            Quality(s);
            qualityIn = 300;
        }
        if (loadError is null)
        {
            try
            {
                EnsureSpawned();
            }
            catch (Exception e)
            {
                loadError = e.Message;
                Log(e.Message);
            }
        }
        if (floor is null || floorMap != mapId)
        {
            if (floorRetry-- <= 0)
            {
                if (TerrainFloor() is var (lo, hi, r))
                {
                    floor = lo;
                    terrainTop = hi;
                    rect = r;
                    floorMap = mapId;
                }
                else
                {
                    floorRetry = 300;
                }
            }
        }
        var fl = floor ?? 0;
        var camera = CameraPosition();
        var sparkleReady = Sparkle.Update(AtmoConfig.F(s, "sparkle") > 0, camera);

        var fc = GameFogColor();
        var (sunDir, sunCol) = Sun();
        var amb = Ambient();
        var fogTint = AtmoConfig.A(s, "fogTint");
        var topTint = AtmoConfig.A(s, "topTint");
        var f = new[] { fc.R * fogTint[0], fc.G * fogTint[1], fc.B * fogTint[2] };
        var top = new[] { f[0] * topTint[0], f[1] * topTint[1], f[2] * topTint[2] };
        var glow = new[] { MathF.Min(sunCol.X, 4), MathF.Min(sunCol.Y, 4), MathF.Min(sunCol.Z, 4) };
        static float C01(float v) => Math.Clamp(v, 0, 1);
        float F(string k) => AtmoConfig.F(s, k);
        float[] Arr(string k) => AtmoConfig.A(s, k);
        Shader.Vec("_JA_FogColor", new(f[0], f[1], f[2], 1));
        Shader.Vec("_JA_FogTop", new(top[0], top[1], top[2], F("skyBand")));
        Shader.Vec("_JA_Fog", new(F("fogDensity"), F("fogFalloff"), fl + F("fogBaseOffset"), C01(F("fogMax"))));
        Shader.Vec("_JA_Fog2", new(F("fogStart"), C01(F("skyHaze")), F("skyDistance"), F("hazeDensity")));
        Shader.Vec("_JA_SunDir", new(sunDir.X, sunDir.Y, sunDir.Z, F("sunGlow")));
        Shader.Vec("_JA_SunColor", new(glow[0] * 0.5f, glow[1] * 0.5f, glow[2] * 0.5f, F("sunGlowSharpness")));
        Shader.Vec("_JA_Snow", new(C01(F("snow")), F("snowBox"), F("snowFall"), F("snowSize")));
        var wind = Arr("wind");
        Shader.Vec("_JA_Wind", new(wind[0], wind[1], wind[2], F("flutter")));
        // Flakes are lit by the sky plus a little sun, never brighter than white.
        float Lit(float a, float g) => MathF.Min(a * 1.25f + g * 0.25f, 1);
        Shader.Vec("_JA_SnowColor", new(Lit(amb.R, glow[0]), Lit(amb.G, glow[1]), Lit(amb.B, glow[2]), C01(F("snowOpacity"))));
        Shader.Vec("_JA_Cloud", new(MathF.Max(F("cloudShadows"), 0), 1 / MathF.Max(F("cloudSize"), 1), C01(F("cloudCoverage")), (camera?.Y ?? fl) + F("cloudHeight")));
        var cs = Arr("cloudSpeed");
        Shader.Vec("_JA_Cloud2", new(cs[0], cs[1], F("cloudSoftness"), AtmoConfig.B(s, "debugSplit") ? 1 : 0));
        var ct = Arr("cloudTint");
        Shader.Vec("_JA_CloudTint", new(ct[0], ct[1], ct[2], 1));
        Shader.Vec("_JA_AO", new(MathF.Max(F("ao"), 0), MathF.Max(F("aoRadius"), 0.05f), F("aoDistance"), MathF.Sin(F("aoMinAngle") * MathF.PI / 180)));
        var at = Arr("aoTint");
        Shader.Vec("_JA_AOTint", new(at[0], at[1], at[2], 1));
        Shader.Vec("_JA_Sparkle", new(sparkleReady ? MathF.Max(F("sparkle"), 0) : 0, C01(F("sparkleDensity")), F("sparkleDistance"), MathF.Max(F("sparkleSize"), 0.002f)));
        Shader.Vec("_JA_Shafts", new(MathF.Max(F("sunShafts"), 0), Math.Clamp(F("sunShaftReach"), 0.05f, 1), 0.96f, F("sunShaftSharpness")));
        var v = CameraVelocity(camera);
        var dbg = Arr("debugCamVel");
        v = new Vector3(v.X + dbg[0], v.Y + dbg[1], v.Z + dbg[2]);
        var kmh = v.Magnitude * 3.6f;
        var frost = Arr("speedFrostKmh");
        var t = C01((kmh - frost[0]) / MathF.Max(frost[1] - frost[0], 1));
        Shader.Vec("_JA_CamVel", new(v.X, v.Y, v.Z, kmh / 3.6f));
        Shader.Vec("_JA_Speed", new(MathF.Max(F("speedStreaks"), 0), MathF.Max(F("speedFrost"), 0) * t * t * (3 - 2 * t), 0, 0));
        var (bankBase, bankTop) = AtmoConfig.CloudBankBounds(s, camera?.Y, fl, terrainTop);
        Shader.Vec("_JA_Bank", new(MathF.Max(F("cloudBank"), 0), bankBase, bankTop, 1 / MathF.Max(F("cloudBankSize"), 1)));
        var bs = Arr("cloudBankSpeed");
        Shader.Vec("_JA_Bank2", new(bs[0], bs[1], C01(F("cloudBankCoverage")), F("cloudBankMargin")));
        Shader.Vec("_JA_MapRect", new(rect[0], rect[1], rect[2], rect[3]));
    }

    // ---- the effects prefab ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct SpanWrapper
    {
        public char* Begin;
        public int Length;
    }

    /// <summary>The managed object behind the GC handle a Unity 6 _Injected binding returns (as the game's Unmarshal does).</summary>
    static nint Unmarshal(nint handle)
    {
        if (handle == 0)
        {
            return 0;
        }
        var unm = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Bindings.Unmarshal");
        var h = handle;
        var boxed = unm.Method("FromIntPtrUnsafe", 1).Invoke(0, (nint)(&h));
        var gk = Il2Cpp.ClassOf(boxed);
        var p = (nint)Il2CppApi.object_unbox(boxed);
        var target = gk.Method("get_Target", 0).Invoke(p);
        gk.Method("Free", 0).Invoke(p);
        return target;
    }

    /// <summary>Loads the bundle, spawns the prefab (kept across scenes) and unloads the bundle, keeping the instance's assets.</summary>
    void Spawn()
    {
        var path = Path.Combine(Bootstrap.GameDir, BundleFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{BundleFile} is missing from the game folder");
        }
        var load = (delegate* unmanaged<SpanWrapper*, uint, ulong, nint>)Il2Cpp.ICall("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected");
        if (load == null)
        {
            throw new InvalidOperationException("AssetBundle loading isn't available");
        }
        nint bundle;
        fixed (char* chars = path)
        {
            var span = new SpanWrapper { Begin = chars, Length = path.Length };
            bundle = Unmarshal(load(&span, 0, 0));
        }
        if (Unity.IsNull(bundle))
        {
            throw new InvalidOperationException($"could not load {path} (built for another Unity version?)");
        }
        void Unload()
        {
            var unload = (delegate* unmanaged<nint, byte, void>)Il2Cpp.ICall("UnityEngine.AssetBundle::Unload_Injected");
            var native = Il2Cpp.ClassOf(bundle).Field("m_CachedPtr").Get<nint>(bundle);
            if (unload != null && native != 0)
            {
                unload(native, 0);
            }
        }
        var goClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.GameObject");
        nint prefab;
        try
        {
            prefab = Unity.Call(bundle, "LoadAsset", Il2Cpp.String(Prefab), goClass.SystemType);
        }
        catch (Exception)
        {
            Unload();
            throw;
        }
        if (Unity.IsNull(prefab))
        {
            Unload();
            throw new InvalidOperationException($"prefab {Prefab} not in the bundle");
        }
        var uobj = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object");
        var go = uobj.Method("Instantiate", "UnityEngine.Object").Invoke(0, prefab);
        uobj.Method("DontDestroyOnLoad", 1).Invoke(0, go);
        Unload();
        Log("spawned");
    }

    void EnsureSpawned()
    {
        var name = Prefab + "(Clone)";
        if (respawn)
        {
            respawn = false;
            var old = Unity.Find(name);
            if (!Unity.IsNull(old))
            {
                Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object").Method("DestroyImmediate", 1).Invoke(0, old);
            }
            checkIn = 0;
        }
        if (checkIn-- > 0)
        {
            return;
        }
        checkIn = 120;
        if (Unity.IsNull(Unity.Find(name)))
        {
            Spawn();
        }
        spawned = true;
    }

    // ---- the game's sky ----------------------------------------------------------------------

    static Color GameFogColor()
    {
        var c = new Color(0.8f, 0.85f, 0.95f, 1);
        var f = (delegate* unmanaged<Color*, void>)Il2Cpp.ICall("UnityEngine.RenderSettings::get_fogColor_Injected");
        if (f != null)
        {
            f(&c);
        }
        return c;
    }

    static (Vector3 Dir, Vector3 Color) Sun()
    {
        var fallback = (new Vector3(0.3f, 0.8f, 0.5f), new Vector3(1, 0.95f, 0.85f));
        try
        {
            var rs = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.RenderSettings");
            var light = Unity.CallStatic(rs, "get_sun");
            if (Unity.IsNull(light))
            {
                return fallback;
            }
            var fw = Unity.Call<Vector3>(Unity.Transform(light), "get_forward");
            var c = Unity.Call<Color>(light, "get_color");
            var i = Unity.Call<float>(light, "get_intensity");
            return (new Vector3(-fw.X, -fw.Y, -fw.Z), new Vector3(c.R * i, c.G * i, c.B * i));
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    static Color Ambient()
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

    /// <summary>The loaded map's terrains (the importer's), falling back to every terrain.</summary>
    static List<nint> MapTerrains()
    {
        var list = new List<nint>();
        foreach (var imp in Importer.All())
        {
            list.AddRange(Il2Cpp.ArrayObjects(imp.Terrains));
        }
        if (list.Count == 0)
        {
            list.AddRange(Unity.FindObjects(Il2Cpp.RequireClass("UnityEngine.TerrainModule.dll", "UnityEngine.Terrain")));
        }
        return list.Where(t => !Unity.IsNull(t)).ToList();
    }

    /// <summary>Lowest and highest terrain heights of the map (a coarse grid of samples) and its xz rectangle.</summary>
    static (float, float, float[])? TerrainFloor()
    {
        float lo = float.MaxValue, hi = float.MinValue;
        var r = new[] { float.MaxValue, float.MaxValue, float.MinValue, float.MinValue };
        foreach (var t in MapTerrains())
        {
            var pos = Unity.Position(t);
            var size = Unity.Call<Vector3>(Unity.Call(t, "get_terrainData"), "get_size");
            r = new[] { MathF.Min(r[0], pos.X), MathF.Min(r[1], pos.Z), MathF.Max(r[2], pos.X + size.X), MathF.Max(r[3], pos.Z + size.Z) };
            for (var i = 0; i <= 24; i++)
            {
                for (var j = 0; j <= 24; j++)
                {
                    var p = new Vector3(pos.X + size.X * i / 24, 0, pos.Z + size.Z * j / 24);
                    try
                    {
                        var y = Unity.Call<float>(t, "SampleHeight", (nint)(&p)) + pos.Y;
                        lo = MathF.Min(lo, y);
                        hi = MathF.Max(hi, y);
                    }
                    catch (Exception)
                    {
                        // a terrain without height data
                    }
                }
            }
        }
        return lo < float.MaxValue ? (lo, hi, r) : null;
    }

    Vector3? CameraPosition()
    {
        if (camCheckIn-- <= 0 || cam is null)
        {
            camCheckIn = 300;
            cam?.Dispose();
            cam = null;
            var go = Unity.Find("WorldCamera");
            if (!Unity.IsNull(go))
            {
                cam = new GameRef(Unity.Transform(go));
            }
        }
        var tr = cam?.Target ?? 0;
        if (Unity.IsNull(tr))
        {
            camCheckIn = 0;
            lastCam = null;
            return null;
        }
        var p = Unity.Call<Vector3>(tr, "get_position");
        return float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) ? p : null;
    }

    /// <summary>Smoothed camera velocity (m/s). Teleports and respawns don't count as movement.</summary>
    Vector3 CameraVelocity(Vector3? position)
    {
        if (position is not { } p)
        {
            return camVel;
        }
        var dt = Unity.DeltaTime;
        if (lastCam is { } last && dt > 1e-4f)
        {
            var raw = (p - last) * (1 / dt);
            if (raw.Magnitude > 90)
            {
                camVel = default; // a jump, not motion
            }
            else
            {
                var k = 1 - MathF.Exp(-dt * 6);
                camVel += (raw - camVel) * k;
            }
        }
        if (dt > 1e-4f)
        {
            lastCam = p;
        }
        return camVel;
    }

    /// <summary>Texture filtering and LOD bias (through engine calls: the build strips some wrappers).</summary>
    void Quality(JsonObject s)
    {
        var aniso = (int)AtmoConfig.F(s, "anisotropic");
        if (aniso > 0)
        {
            var set = (delegate* unmanaged<int, void>)Il2Cpp.ICall("UnityEngine.QualitySettings::set_anisotropicFiltering");
            if (set != null)
            {
                set(2); // ForceEnable
            }
            var limits = (delegate* unmanaged<int, int, void>)Il2Cpp.ICall("UnityEngine.Texture::SetGlobalAnisotropicFilteringLimits");
            if (limits != null)
            {
                var a = Math.Clamp(aniso, 1, 16);
                limits(a, a);
            }
        }
        var qs = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.QualitySettings");
        var cur = Il2Cpp.Unbox<float>(Unity.CallStatic(qs, "get_lodBias"));
        var lod = AtmoConfig.F(s, "lodBias");
        if (lod > 0 && gameLodBias is null)
        {
            gameLodBias = cur;
        }
        float? want = lod > 0 ? lod : gameLodBias;
        if (want is { } w && MathF.Abs(w - cur) > 1e-4f)
        {
            Unity.CallStatic(qs, "set_lodBias", (nint)(&w));
        }
    }

    // ---- the mod's snow textures on the map terrain ------------------------------------------

    static readonly string[] SurfaceTextures = { "_SurfaceTex", "_SnowNormal", "_SnowDetailNormal" };
    static readonly string[] SurfaceFloats = { "_SnowTexTiling", "_SnowNormalStrength", "_SnowDetailTiling", "_SnowDetailNormalStrength" };

    sealed record Surface(GameRef Mat, List<(string Name, GameRef? Tex)> Textures, List<(string Name, float Value)> Floats);

    Surface? surface;

    static nint Material(string method, nint m, params nint[] args) => Unity.Call(m, method, args);

    static nint GetTex(nint m, string n) => Material("GetTexture", m, Il2Cpp.String(n));
    static void SetTex(nint m, string n, nint t) => Material("SetTexture", m, Il2Cpp.String(n), t);

    static float? GetFloat(nint m, string n)
    {
        try
        {
            return Il2Cpp.Unbox<float>(Material("GetFloat", m, Il2Cpp.String(n)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void SetFloat(nint m, string n, float v) => Material("SetFloat", m, Il2Cpp.String(n), (nint)(&v));

    void SnowSurface(JsonObject s, bool on)
    {
        if (surfaceCheckIn-- > 0)
        {
            return;
        }
        surfaceCheckIn = 120;
        var want = on && AtmoConfig.B(s, "snowSurface");
        var current = surface?.Mat.Target is { } cm && !Unity.IsNull(cm) ? cm : (nint?)null;
        nint? target = null;
        if (want && MapTerrains().FirstOrDefault() is var t && t != 0)
        {
            var m = Unity.Call(t, "get_materialTemplate");
            target = Unity.IsNull(m) ? null : m;
        }
        if (surface is { } old && !(current is { } a && target is { } b && a == b))
        {
            // Off, or the material is gone or no longer the map's: restore what we can.
            if (current is { } m)
            {
                foreach (var (n, h) in old.Textures)
                {
                    SetTex(m, n, h?.Target ?? 0);
                }
                foreach (var (n, v) in old.Floats)
                {
                    SetFloat(m, n, v);
                }
            }
            foreach (var (_, h) in old.Textures)
            {
                h?.Dispose();
            }
            old.Mat.Dispose();
            surface = null;
        }
        if (target is not { } mat)
        {
            return;
        }
        var holderGo = Unity.Find(Prefab + "(Clone)/SnowSurface");
        if (Unity.IsNull(holderGo))
        {
            return;
        }
        var src = Unity.Call(Unity.GetComponent(holderGo, Unity.Class("UnityEngine.Renderer")), "get_sharedMaterial");
        if (surface is null)
        {
            surface = new Surface(new GameRef(mat),
                SurfaceTextures.Select(n => (n, GetTex(mat, n) is var x && x != 0 ? new GameRef(x) : null)).ToList(),
                SurfaceFloats.Select(n => (n, GetFloat(mat, n))).Where(p => p.Item2 is not null).Select(p => (p.n, p.Item2!.Value)).ToList());
            foreach (var n in SurfaceTextures)
            {
                var tex = GetTex(src, n);
                if (!Unity.IsNull(tex))
                {
                    SetTex(mat, n, tex);
                }
            }
            Log("snow surface applied");
        }
        // uv = world x _WorldScale x tiling, so one repeat is 1 / (scale x tiling) metres.
        var ws = GetFloat(mat, "_WorldScale") is { } w && w > 0 ? w : 0.01f;
        SetFloat(mat, "_SnowTexTiling", 1 / (ws * MathF.Max(AtmoConfig.F(s, "snowSurfaceScale"), 0.1f)));
        SetFloat(mat, "_SnowNormalStrength", AtmoConfig.F(s, "snowSurfaceNormal"));
        SetFloat(mat, "_SnowDetailTiling", 1 / (ws * MathF.Max(AtmoConfig.F(s, "snowSurfaceDuneScale"), 0.1f)));
        SetFloat(mat, "_SnowDetailNormalStrength", AtmoConfig.F(s, "snowSurfaceDuneNormal"));
    }

    void DisableEffects()
    {
        JsonObject snapshot;
        lock (gate) { snapshot = (JsonObject)configuration.DeepClone(); }
        surfaceCheckIn = 0;
        SnowSurface(snapshot, false);
        Sparkle.Update(false, null);
        Grading.Restore();
        foreach (var name in new[] { "_JA_FogColor", "_JA_Snow", "_JA_Cloud", "_JA_AO", "_JA_Sparkle", "_JA_Shafts", "_JA_Speed", "_JA_Bank" })
            Shader.Vec(name, new Vector4(0, 0, 0, 0));
        if (gameLodBias is { } lod)
        {
            Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.QualitySettings"), "set_lodBias", (nint)(&lod));
            gameLodBias = null;
        }
    }

    public override void OnUnload()
    {
        try
        {
            MainThread.Run(() =>
            {
                DisableEffects();
                Sparkle.Teardown();
                var go = Unity.Find(Prefab + "(Clone)");
                if (!Unity.IsNull(go))
                {
                    Unity.Destroy(go);
                }
                cam?.Dispose();
            });
        }
        catch (Exception e)
        {
            Log("cleanup: " + e.Message);
        }
    }
}

/// <summary>Shader globals by name (ids cached).</summary>
static unsafe class Shader
{
    static readonly Dictionary<string, int> ids = new();
    static Il2CppMethod setVector, setFloat, toId;

    static void Init()
    {
        if (!setVector.IsNull)
        {
            return;
        }
        var k = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Shader");
        setVector = k.Method("SetGlobalVector", "System.Int32", "UnityEngine.Vector4");
        setFloat = k.Method("SetGlobalFloat", "System.Int32", "System.Single");
        toId = k.Method("PropertyToID", "System.String");
        if (setVector.IsNull || setFloat.IsNull || toId.IsNull)
        {
            throw new InvalidOperationException("Shader.SetGlobalVector / SetGlobalFloat / PropertyToID missing");
        }
    }

    public static int Id(string name)
    {
        Init();
        if (!ids.TryGetValue(name, out var id))
        {
            ids[name] = id = toId.Invoke<int>(0, Il2Cpp.String(name));
        }
        return id;
    }

    public static void Vec(string name, Vector4 v)
    {
        var id = Id(name);
        setVector.Invoke(0, (nint)(&id), (nint)(&v));
    }

    public static void Float(string name, float v)
    {
        var id = Id(name);
        setFloat.Invoke(0, (nint)(&id), (nint)(&v));
    }
}

/// <summary>Colour grading through the game's own Beautify volume and EnvironmentManager lighting fields; originals restored when off.</summary>
static unsafe class Grading
{
    static readonly Dictionary<(string Kind, string Name), JsonNode?> originals = new();
    static nint lastEnv;

    static nint Beautify(nint env)
    {
        var f = Manager.Beautify_Field;
        return f.IsNull ? 0 : f.GetObject(env);
    }

    static JsonNode? Read(nint o, string name)
    {
        var f = Il2Cpp.ClassOf(o).Field(name);
        if (f.IsNull)
        {
            return null;
        }
        return f.TypeName switch
        {
            "System.Single" => f.Get<float>(o),
            "System.Boolean" => f.Get<bool>(o),
            "System.Int32" => f.Get<int>(o),
            "UnityEngine.Color" => new JsonArray(f.Get<Color>(o) is var c ? new JsonNode?[] { c.R, c.G, c.B, c.A } : null!),
            _ when f.TypeClass.IsEnum => f.Get<int>(o),
            _ => null,
        };
    }

    static void Write(nint o, string name, JsonNode? v)
    {
        var f = Il2Cpp.ClassOf(o).Field(name);
        if (f.IsNull || v is null)
        {
            return;
        }
        double Num(JsonNode n) => double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        switch (f.TypeName)
        {
            case "System.Single":
                f.Set(o, (float)Num(v));
                break;
            case "System.Boolean":
                f.Set(o, v.GetValue<bool>());
                break;
            case "UnityEngine.Color" when v is JsonArray a:
                f.Set(o, new Color(a.Count > 0 ? (float)Num(a[0]!) : 1, a.Count > 1 ? (float)Num(a[1]!) : 1, a.Count > 2 ? (float)Num(a[2]!) : 1, a.Count > 3 ? (float)Num(a[3]!) : 1));
                break;
            default:
                f.Set(o, (int)Num(v));
                break;
        }
    }

    public static void Apply(nint env, bool on, JsonObject s)
    {
        lastEnv = env;
        var b = Beautify(env);
        if (!on)
        {
            Restore();
            return;
        }
        if (b != 0 && s["beautify"] is JsonObject bv)
        {
            foreach (var (name, v) in bv)
            {
                var param = Il2Cpp.ClassOf(b).Field(name);
                var p = param.IsNull ? 0 : param.GetObject(b);
                if (p == 0)
                {
                    continue;
                }
                if (!originals.ContainsKey(("beautify", name)))
                {
                    originals[("beautify", name)] = Read(p, "m_Value");
                    originals[("override", name)] = Read(p, "m_OverrideState");
                }
                Write(p, "m_Value", v);
                Write(p, "m_OverrideState", true);
            }
        }
        if (s["environment"] is JsonObject ev)
        {
            foreach (var (name, v) in ev)
            {
                if (!originals.ContainsKey(("env", name)))
                {
                    originals[("env", name)] = Read(env, name);
                }
                Write(env, name, v);
            }
        }
    }

    public static void Restore()
    {
        if (lastEnv == 0 || originals.Count == 0)
        {
            return;
        }
        var b = Beautify(lastEnv);
        foreach (var ((kind, name), v) in originals)
        {
            try
            {
                if (kind == "env")
                {
                    Write(lastEnv, name, v);
                }
                else if (b != 0 && Il2Cpp.ClassOf(b).Field(name) is var param && !param.IsNull && param.GetObject(b) is var p && p != 0)
                {
                    Write(p, kind == "beautify" ? "m_Value" : "m_OverrideState", v);
                }
            }
            catch (Exception)
            {
                // the environment was replaced
            }
        }
        originals.Clear();
    }
}

/// <summary>Keeps the snow glitter off characters: world-space boxes around the nearest riders, which the glitter shader rejects.</summary>
static class Sparkle
{
    const int MaxBoxes = 8;
    const float Margin = 0.15f, Near = 120, MaxExtent = 3;
    const int Refresh = 30;
    static int ticks;
    static readonly List<List<GameRef>> groups = new();
    static readonly List<(Vector4 C, Vector4 E)?> last = new();

    static void Free()
    {
        foreach (var g in groups)
        {
            g.ForEach(r => r.Dispose());
        }
        groups.Clear();
        last.Clear();
    }

    static void Collect()
    {
        var skinned = Unity.Class("UnityEngine.SkinnedMeshRenderer");
        var rigid = Unity.Class("UnityEngine.MeshRenderer");
        var sets = new List<nint[]>();
        foreach (var rider in Jibbers.Sdk.Game.Player.Ragdoll.All())
        {
            sets.Add(Unity.GetComponentsInChildren(rider.Ptr, Unity.Class("UnityEngine.Renderer")));
        }
        // Other skinned meshes (remote riders and clothing) grouped by their scene root.
        foreach (var g in Unity.FindObjects(skinned).Where(r => !Unity.IsNull(r)).GroupBy(r => Unity.Call<int>(Unity.Call(Unity.Transform(r), "get_root"), "GetInstanceID")))
        {
            sets.Add(g.ToArray());
        }
        Free();
        var seen = new HashSet<nint>();
        foreach (var set in sets)
        {
            var group = new List<GameRef>();
            foreach (var r in set)
            {
                if (Unity.IsNull(r) || !seen.Add(r))
                {
                    continue;
                }
                var k = Jibbers.Sdk.Interop.Il2Cpp.ClassOf(r);
                if (skinned.IsAssignableFrom(k) || rigid.IsAssignableFrom(k))
                {
                    group.Add(new GameRef(r));
                }
            }
            if (group.Count > 0)
            {
                groups.Add(group);
                last.Add(null);
            }
        }
    }

    /// <summary>Refreshes the boxes for this frame; whether glitter may be drawn.</summary>
    public static bool Update(bool enabled, Vector3? camera)
    {
        try
        {
            if (!enabled)
            {
                Free();
                Push(camera);
                ticks = 0;
                return false;
            }
            if (ticks-- <= 0)
            {
                Collect();
                ticks = Refresh;
            }
            Push(camera);
            return true;
        }
        catch (Exception)
        {
            ticks = Refresh;
            return false;
        }
    }

    static void Push(Vector3? camera)
    {
        var boxes = new List<(Vector4 C, Vector4 E)>();
        bool Far((Vector4 C, Vector4 E) b) => camera is { } p && (new Vector3(b.C.X, b.C.Y, b.C.Z) - p).Magnitude > Near;
        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (last[gi] is { } kept && Far(kept))
            {
                boxes.Add(kept);
                continue;
            }
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var h in groups[gi])
            {
                var r = h.Target;
                if (Unity.IsNull(r) || !Unity.Call<bool>(r, "get_enabled"))
                {
                    continue;
                }
                var b = Unity.Call<Bounds>(r, "get_bounds");
                var (c, e) = (b.Center, b.Extents);
                lo = new Vector3(MathF.Min(lo.X, c.X - e.X), MathF.Min(lo.Y, c.Y - e.Y), MathF.Min(lo.Z, c.Z - e.Z));
                hi = new Vector3(MathF.Max(hi.X, c.X + e.X), MathF.Max(hi.Y, c.Y + e.Y), MathF.Max(hi.Z, c.Z + e.Z));
            }
            if (lo.X > hi.X || hi.X - lo.X > 2 * MaxExtent || hi.Y - lo.Y > 2 * MaxExtent || hi.Z - lo.Z > 2 * MaxExtent)
            {
                last[gi] = null;
                continue;
            }
            var box = (new Vector4((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (lo.Z + hi.Z) / 2, 0), new Vector4((hi.X - lo.X) / 2 + Margin, (hi.Y - lo.Y) / 2 + Margin, (hi.Z - lo.Z) / 2 + Margin, 0));
            last[gi] = box;
            boxes.Add(box);
        }
        if (camera is { } cam)
        {
            boxes.Sort((a, b) => (new Vector3(a.C.X, a.C.Y, a.C.Z) - cam).Magnitude.CompareTo((new Vector3(b.C.X, b.C.Y, b.C.Z) - cam).Magnitude));
        }
        var n = Math.Min(boxes.Count, MaxBoxes);
        for (var i = 0; i < n; i++)
        {
            Shader.Vec($"_JA_CharBox{i}C", boxes[i].C);
            Shader.Vec($"_JA_CharBox{i}E", boxes[i].E);
        }
        Shader.Float("_JA_CharBoxCount", n);
    }

    public static void Teardown()
    {
        Free();
        try
        {
            Shader.Float("_JA_CharBoxCount", 0);
        }
        catch (Exception)
        {
            // the scene is going away
        }
    }
}
