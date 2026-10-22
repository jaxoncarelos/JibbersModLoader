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

    JsonObject configuration = AtmosphereConfiguration.Defaults();
    AtmosphereRenderSettings? renderSettings;
    readonly AtmosphereShaderParameters shaderParameters = new(AtmosphereShader.SetVector);
    readonly object configurationLock = new();
    bool synchronizingMenu;

    string SettingsPath => Path.Combine(Bootstrap.GameDir.Length > 0 ? Bootstrap.GameDir : AppContext.BaseDirectory, SettingsFile);

    void LoadConfiguration()
    {
        renderSettings = null;
        try
        {
            var path = SettingsPath;
            configuration = AtmosphereConfiguration.Load(File.Exists(path) ? File.ReadAllText(path) : null);
            if (!File.Exists(path))
            {
                SaveConfiguration();
            }
        }
        catch (Exception exception)
        {
            Log($"{SettingsFile} is invalid ({exception.Message}); using defaults");
            configuration = AtmosphereConfiguration.Defaults();
        }
    }

    void SaveConfiguration()
    {
        try
        {
            File.WriteAllText(SettingsPath, AtmosphereConfiguration.Serialize(configuration));
        }
        catch (Exception exception)
        {
            Log($"could not save {SettingsFile}: {exception.Message}");
        }
    }

    // ---- Mods tab: the same 14 controls as the Rust mod ----------------------------------------

    public override IEnumerable<Setting> Settings
    {
        get
        {
            LoadConfiguration();
            var presets = AtmosphereConfiguration.PresetOrder(configuration["presets"] as JsonObject ?? new JsonObject());
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
    void SynchronizeMenu()
    {
        synchronizingMenu = true;
        try
        {
            SetSetting("enabled", AtmosphereConfiguration.GetBool(configuration, "enabled"));
            if (configuration["preset"] is JsonValue presetValue)
            {
                try
                {
                    SetSetting("preset", presetValue.DeepClone());
                }
                catch (ArgumentException)
                {
                    // a preset the tab does not list yet
                }
            }
            foreach (var settingKey in new[] { "hotkeys", "grading" })
            {
                SetSetting(settingKey, AtmosphereConfiguration.GetBool(configuration, settingKey));
            }
            foreach (var settingKey in new[] { "toggleKey", "presetKey" })
            {
                SetSetting(settingKey, (int)AtmosphereConfiguration.GetFloat(configuration, settingKey));
            }
            SetSetting("fog", Math.Round(AtmosphereConfiguration.FogValue(configuration), 4));
            foreach (var settingKey in new[] { "fogStart", "snow", "snowSize", "speedStreaks", "cloudBank", "cloudBankDistance", "sunShafts" })
            {
                SetSetting(settingKey, Math.Round(AtmosphereConfiguration.GetFloat(configuration, settingKey), 4));
            }
        }
        finally
        {
            synchronizingMenu = false;
        }
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (synchronizingMenu)
        {
            return;
        }
        lock (configurationLock)
        {
            if (key == "preset")
            {
                configuration = AtmosphereConfiguration.WithPreset(configuration, value.GetValue<string>());
            }
            else if (key == "fog")
            {
                AtmosphereConfiguration.Merge(configuration, AtmosphereConfiguration.FogFields((float)value.GetValue<double>()));
            }
            else
            {
                configuration[key] = value.DeepClone();
            }
            renderSettings = null;
            framesUntilSurfaceCheck = 0;
            loadError = null;
            SaveConfiguration();
        }
        if (key == "preset")
        {
            MainThread.Later(SynchronizeMenu);
        }
        if (key == "enabled" && !value.GetValue<bool>())
        {
            // The SDK skips callbacks for disabled mods; reset effects before those callbacks stop.
            MainThread.Run(DisableEffects);
        }
    }

    /// <summary><c>{"cmd":"atmosphere.set", ...any settings, "preset": name, "nextPreset": true, "toggle": true, "respawn": true}</c>.</summary>
    public override JsonNode? OnCommand(string command, JsonObject request)
    {
        switch (command)
        {
            case "atmosphere.status":
                return new JsonObject { ["preset"] = configuration["preset"]?.DeepClone(), ["spawned"] = prefabSpawned, ["floor"] = terrainFloor, ["cameraKmh"] = cameraVelocity.Magnitude * 3.6f, ["error"] = loadError };
            case "atmosphere.set":
                ApplyConfiguration(request);
                return new JsonObject { ["settings"] = configuration.DeepClone() };
            default:
                return null;
        }
    }

    void ApplyConfiguration(JsonObject request)
    {
        lock (configurationLock)
        {
            var fields = (JsonObject)request.DeepClone();
            fields.Remove("cmd");
            string? selectedPreset = null;
            if (fields["nextPreset"] is JsonValue advancePreset && advancePreset.GetValue<bool>())
            {
                selectedPreset = AtmosphereConfiguration.NextPreset(configuration);
            }
            else if (fields["preset"] is JsonValue presetValue)
            {
                selectedPreset = presetValue.GetValue<string>();
            }
            fields.Remove("preset");
            if (selectedPreset is not null)
            {
                configuration = AtmosphereConfiguration.WithPreset(configuration, selectedPreset);
            }
            AtmosphereConfiguration.Merge(configuration, fields);
            if (fields["toggle"] is JsonValue toggleValue && toggleValue.GetValue<bool>())
            {
                configuration["enabled"] = !AtmosphereConfiguration.GetBool(configuration, "enabled");
            }
            if (fields["respawn"] is JsonValue respawnValue && respawnValue.GetValue<bool>())
            {
                respawnRequested = true;
            }
            renderSettings = null;
            framesUntilSurfaceCheck = 0;
            loadError = null;
            SaveConfiguration();
        }
        MainThread.Later(SynchronizeMenu);
    }

    // ---- per frame ---------------------------------------------------------------------------

    bool prefabSpawned, respawnRequested;
    AtmosphereRenderSettings? previousFrameSettings;
    int framesUntilPrefabCheck, framesUntilTerrainRetry, framesUntilQualityRefresh, framesUntilSurfaceCheck, framesUntilCameraCheck;
    string? loadError;
    string? terrainMapId;
    float? terrainFloor;
    float terrainCeiling;
    Vector4 mapRectangle;
    string? currentMapId;
    GameRef? cameraTransform;
    Vector3? previousCameraPosition;
    Vector3 cameraVelocity;
    float? originalLodBias;

    public override void OnLoad()
    {
        LoadEvent.Subscribe(loadEvent =>
        {
            try
            {
                currentMapId = loadEvent.Map.Id;
            }
            catch (Exception)
            {
                currentMapId = null;
            }
        });
        Manager.UpdateMethod.Before((environmentManager, _) => Frame(environmentManager));
        Input.OnSetting(Owner, "toggleKey", () =>
        {
            if (GetBool("hotkeys"))
            {
                ApplyConfiguration(new JsonObject { ["toggle"] = true });
                Log($"hotkey: enabled {AtmosphereConfiguration.GetBool(configuration, "enabled")}");
            }
        });
        Input.OnSetting(Owner, "presetKey", () =>
        {
            if (GetBool("hotkeys"))
            {
                ApplyConfiguration(new JsonObject { ["nextPreset"] = true });
                Log($"hotkey: preset {configuration["preset"]}");
            }
        });
        MainThread.Later(SynchronizeMenu);
    }

    internal AtmosphereRenderSettings CaptureSettings()
    {
        lock (configurationLock)
        {
            return renderSettings ??= new AtmosphereRenderSettings(configuration);
        }
    }

    void Frame(nint environmentManager)
    {
        var settings = CaptureSettings();
        var effectsEnabled = Enabled && settings.Enabled;
        UpdateColorGrading(environmentManager, settings, effectsEnabled);
        UpdateSnowSurface(settings.Values, effectsEnabled);
        if (!effectsEnabled)
        {
            ClearShaderEffects();
            return;
        }

        RefreshQuality(settings.Values);
        EnsurePrefabAvailable();
        RefreshTerrainBounds();
        var cameraPosition = FindCameraPosition();
        var (sunDirection, sunColor) = ReadSunlight();
        var frame = new AtmosphereFrame
        {
            FogColor = ReadFogColor(),
            SunDirection = sunDirection,
            SunColor = sunColor,
            AmbientLight = ReadAmbientLight(),
            CameraPosition = cameraPosition,
            CameraVelocity = MeasureCameraVelocity(cameraPosition),
            TerrainFloor = terrainFloor ?? 0,
            TerrainCeiling = terrainCeiling,
            MapRectangle = mapRectangle,
            SparkleMaskReady = CharacterSparkleMask.Update(settings.Sparkles.Strength > 0, cameraPosition),
        };
        shaderParameters.Apply(settings, frame);
    }

    void UpdateColorGrading(nint environmentManager, AtmosphereRenderSettings settings, bool effectsEnabled)
    {
        var gradingEnabled = effectsEnabled && settings.GradingEnabled;
        if (!ReferenceEquals(previousFrameSettings, settings) || !gradingEnabled)
        {
            AtmosphereColorGrading.Apply(environmentManager, false, settings.Values);
        }
        if (gradingEnabled) AtmosphereColorGrading.Apply(environmentManager, true, settings.Values);
        previousFrameSettings = settings;
    }

    void RefreshQuality(JsonObject settings)
    {
        if (framesUntilQualityRefresh-- > 0) return;
        ApplyQualitySettings(settings);
        framesUntilQualityRefresh = 300;
    }

    void EnsurePrefabAvailable()
    {
        if (loadError is not null) return;
        try { EnsurePrefabSpawned(); }
        catch (Exception exception)
        {
            loadError = exception.Message;
            Log(exception.Message);
        }
    }

    void RefreshTerrainBounds()
    {
        if (terrainFloor is not null && terrainMapId == currentMapId) return;
        if (framesUntilTerrainRetry-- > 0) return;
        if (MeasureTerrainBounds() is { } bounds)
        {
            terrainFloor = bounds.MinimumHeight;
            terrainCeiling = bounds.MaximumHeight;
            mapRectangle = bounds.Rectangle;
            terrainMapId = currentMapId;
        }
        else framesUntilTerrainRetry = 300;
    }

    void ClearShaderEffects()
    {
        CharacterSparkleMask.Update(false, null);
        shaderParameters.Disable();
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
        var unmarshallerClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Bindings.Unmarshal");
        var nativeHandle = handle;
        var boxedHandle = unmarshallerClass.Method("FromIntPtrUnsafe", 1).Invoke(0, (nint)(&nativeHandle));
        var handleClass = Il2Cpp.ClassOf(boxedHandle);
        var handleData = (nint)Il2CppApi.object_unbox(boxedHandle);
        var target = handleClass.Method("get_Target", 0).Invoke(handleData);
        handleClass.Method("Free", 0).Invoke(handleData);
        return target;
    }

    /// <summary>Loads the bundle, spawns the prefab (kept across scenes) and unloads the bundle, keeping the instance's assets.</summary>
    void SpawnPrefab()
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
        var gameObjectClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.GameObject");
        nint prefab;
        try
        {
            prefab = Unity.Call(bundle, "LoadAsset", Il2Cpp.String(Prefab), gameObjectClass.SystemType);
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
        var unityObjectClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object");
        var instance = unityObjectClass.Method("Instantiate", "UnityEngine.Object").Invoke(0, prefab);
        unityObjectClass.Method("DontDestroyOnLoad", 1).Invoke(0, instance);
        Unload();
        Log("spawned");
    }

    void EnsurePrefabSpawned()
    {
        var instanceName = Prefab + "(Clone)";
        if (respawnRequested)
        {
            respawnRequested = false;
            var previousInstance = Unity.Find(instanceName);
            if (!Unity.IsNull(previousInstance))
            {
                Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object").Method("DestroyImmediate", 1).Invoke(0, previousInstance);
            }
            framesUntilPrefabCheck = 0;
        }
        if (framesUntilPrefabCheck-- > 0)
        {
            return;
        }
        framesUntilPrefabCheck = 120;
        if (Unity.IsNull(Unity.Find(instanceName)))
        {
            SpawnPrefab();
        }
        prefabSpawned = true;
    }

    // ---- the game's sky ----------------------------------------------------------------------

    static Color ReadFogColor()
    {
        var fogColor = new Color(0.8f, 0.85f, 0.95f, 1);
        var readFogColor = (delegate* unmanaged<Color*, void>)Il2Cpp.ICall("UnityEngine.RenderSettings::get_fogColor_Injected");
        if (readFogColor != null)
        {
            readFogColor(&fogColor);
        }
        return fogColor;
    }

    static (Vector3 Dir, Vector3 Color) ReadSunlight()
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

    static Color ReadAmbientLight()
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
        var terrains = new List<nint>();
        foreach (var importer in Importer.All())
        {
            terrains.AddRange(Il2Cpp.ArrayObjects(importer.Terrains));
        }
        if (terrains.Count == 0)
        {
            terrains.AddRange(Unity.FindObjects(Il2Cpp.RequireClass("UnityEngine.TerrainModule.dll", "UnityEngine.Terrain")));
        }
        return terrains.Where(terrain => !Unity.IsNull(terrain)).ToList();
    }

    /// <summary>Lowest and highest terrain heights of the map (a coarse grid of samples) and its xz rectangle.</summary>
    static (float MinimumHeight, float MaximumHeight, Vector4 Rectangle)? MeasureTerrainBounds()
    {
        float minimumHeight = float.MaxValue, maximumHeight = float.MinValue;
        var rectangle = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        foreach (var terrain in MapTerrains())
        {
            var terrainPosition = Unity.Position(terrain);
            var size = Unity.Call<Vector3>(Unity.Call(terrain, "get_terrainData"), "get_size");
            rectangle = new(
                MathF.Min(rectangle.X, terrainPosition.X), MathF.Min(rectangle.Y, terrainPosition.Z),
                MathF.Max(rectangle.Z, terrainPosition.X + size.X), MathF.Max(rectangle.W, terrainPosition.Z + size.Z));
            for (var xIndex = 0; xIndex <= 24; xIndex++)
            {
                for (var zIndex = 0; zIndex <= 24; zIndex++)
                {
                    var samplePosition = new Vector3(terrainPosition.X + size.X * xIndex / 24, 0, terrainPosition.Z + size.Z * zIndex / 24);
                    try
                    {
                        var height = Unity.Call<float>(terrain, "SampleHeight", (nint)(&samplePosition)) + terrainPosition.Y;
                        minimumHeight = MathF.Min(minimumHeight, height);
                        maximumHeight = MathF.Max(maximumHeight, height);
                    }
                    catch (Exception)
                    {
                        // a terrain without height data
                    }
                }
            }
        }
        return minimumHeight < float.MaxValue ? (minimumHeight, maximumHeight, rectangle) : null;
    }

    Vector3? FindCameraPosition()
    {
        if (framesUntilCameraCheck-- <= 0 || cameraTransform is null)
        {
            framesUntilCameraCheck = 300;
            cameraTransform?.Dispose();
            cameraTransform = null;
            var cameraObject = Unity.Find("WorldCamera");
            if (!Unity.IsNull(cameraObject))
            {
                cameraTransform = new GameRef(Unity.Transform(cameraObject));
            }
        }
        var transform = cameraTransform?.Target ?? 0;
        if (Unity.IsNull(transform))
        {
            framesUntilCameraCheck = 0;
            previousCameraPosition = null;
            return null;
        }
        var position = Unity.Call<Vector3>(transform, "get_position");
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) ? position : null;
    }

    /// <summary>Smoothed camera velocity (m/s). Teleports and respawns don't count as movement.</summary>
    Vector3 MeasureCameraVelocity(Vector3? position)
    {
        if (position is not { } currentPosition)
        {
            return cameraVelocity;
        }
        var deltaTime = Unity.DeltaTime;
        if (previousCameraPosition is { } lastPosition && deltaTime > 1e-4f)
        {
            var measuredVelocity = (currentPosition - lastPosition) * (1 / deltaTime);
            if (measuredVelocity.Magnitude > 90)
            {
                cameraVelocity = default; // a jump, not motion
            }
            else
            {
                var blend = 1 - MathF.Exp(-deltaTime * 6);
                cameraVelocity += (measuredVelocity - cameraVelocity) * blend;
            }
        }
        if (deltaTime > 1e-4f)
        {
            previousCameraPosition = currentPosition;
        }
        return cameraVelocity;
    }

    /// <summary>Texture filtering and LOD bias (through engine calls: the build strips some wrappers).</summary>
    void ApplyQualitySettings(JsonObject settings)
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

    // ---- the mod's snow textures on the map terrain ------------------------------------------

    static readonly string[] SurfaceTextureProperties = { "_SurfaceTex", "_SnowNormal", "_SnowDetailNormal" };
    static readonly string[] SurfaceFloatProperties = { "_SnowTexTiling", "_SnowNormalStrength", "_SnowDetailTiling", "_SnowDetailNormalStrength" };

    sealed record SnowSurfaceState(GameRef Material, List<(string PropertyName, GameRef? Texture)> Textures, List<(string PropertyName, float Value)> Floats);

    SnowSurfaceState? snowSurfaceState;

    static nint CallMaterial(string method, nint material, params nint[] args) => Unity.Call(material, method, args);

    static nint GetTexture(nint material, string propertyName) => CallMaterial("GetTexture", material, Il2Cpp.String(propertyName));
    static void SetTexture(nint material, string propertyName, nint texture) => CallMaterial("SetTexture", material, Il2Cpp.String(propertyName), texture);

    static float? GetFloat(nint material, string propertyName)
    {
        try
        {
            return Il2Cpp.Unbox<float>(CallMaterial("GetFloat", material, Il2Cpp.String(propertyName)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void SetFloat(nint material, string propertyName, float value) => CallMaterial("SetFloat", material, Il2Cpp.String(propertyName), (nint)(&value));

    void UpdateSnowSurface(JsonObject settings, bool effectsEnabled)
    {
        if (framesUntilSurfaceCheck-- > 0)
        {
            return;
        }
        framesUntilSurfaceCheck = 120;
        var needsSnowSurface = effectsEnabled && AtmosphereConfiguration.GetBool(settings, "snowSurface");
        var currentMaterial = snowSurfaceState?.Material.Target is { } savedMaterial && !Unity.IsNull(savedMaterial) ? savedMaterial : (nint?)null;
        nint? desiredMaterial = null;
        if (needsSnowSurface && MapTerrains().FirstOrDefault() is var terrain && terrain != 0)
        {
            var material = Unity.Call(terrain, "get_materialTemplate");
            desiredMaterial = Unity.IsNull(material) ? null : material;
        }
        if (snowSurfaceState is { } previousSurface && !(currentMaterial is { } oldMaterial && desiredMaterial is { } newMaterial && oldMaterial == newMaterial))
        {
            // Off, or the material is gone or no longer the map's: restore what we can.
            if (currentMaterial is { } material)
            {
                foreach (var (propertyName, textureHandle) in previousSurface.Textures)
                {
                    SetTexture(material, propertyName, textureHandle?.Target ?? 0);
                }
                foreach (var (propertyName, value) in previousSurface.Floats)
                {
                    SetFloat(material, propertyName, value);
                }
            }
            foreach (var (_, textureHandle) in previousSurface.Textures)
            {
                textureHandle?.Dispose();
            }
            previousSurface.Material.Dispose();
            snowSurfaceState = null;
        }
        if (desiredMaterial is not { } targetMaterial)
        {
            return;
        }
        var textureHolder = Unity.Find(Prefab + "(Clone)/SnowSurface");
        if (Unity.IsNull(textureHolder))
        {
            return;
        }
        var sourceMaterial = Unity.Call(Unity.GetComponent(textureHolder, Unity.Class("UnityEngine.Renderer")), "get_sharedMaterial");
        if (snowSurfaceState is null)
        {
            snowSurfaceState = new SnowSurfaceState(new GameRef(targetMaterial),
                SurfaceTextureProperties.Select(propertyName => (propertyName, GetTexture(targetMaterial, propertyName) is var texturePointer && texturePointer != 0 ? new GameRef(texturePointer) : null)).ToList(),
                SurfaceFloatProperties
                    .Select(propertyName => (PropertyName: propertyName, Value: GetFloat(targetMaterial, propertyName)))
                    .Where(property => property.Value is not null)
                    .Select(property => (property.PropertyName, property.Value.GetValueOrDefault()))
                    .ToList());
            foreach (var propertyName in SurfaceTextureProperties)
            {
                var texture = GetTexture(sourceMaterial, propertyName);
                if (!Unity.IsNull(texture))
                {
                    SetTexture(targetMaterial, propertyName, texture);
                }
            }
            Log("snow surface applied");
        }
        // uv = world x _WorldScale x tiling, so one repeat is 1 / (scale x tiling) metres.
        var worldScale = GetFloat(targetMaterial, "_WorldScale") is { } scale && scale > 0 ? scale : 0.01f;
        SetFloat(targetMaterial, "_SnowTexTiling", 1 / (worldScale * MathF.Max(AtmosphereConfiguration.GetFloat(settings, "snowSurfaceScale"), 0.1f)));
        SetFloat(targetMaterial, "_SnowNormalStrength", AtmosphereConfiguration.GetFloat(settings, "snowSurfaceNormal"));
        SetFloat(targetMaterial, "_SnowDetailTiling", 1 / (worldScale * MathF.Max(AtmosphereConfiguration.GetFloat(settings, "snowSurfaceDuneScale"), 0.1f)));
        SetFloat(targetMaterial, "_SnowDetailNormalStrength", AtmosphereConfiguration.GetFloat(settings, "snowSurfaceDuneNormal"));
    }

    void DisableEffects()
    {
        framesUntilSurfaceCheck = 0;
        UpdateSnowSurface(CaptureSettings().Values, false);
        ClearShaderEffects();
        AtmosphereColorGrading.Restore();
        if (originalLodBias is { } lodBias)
        {
            Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.QualitySettings"), "set_lodBias", (nint)(&lodBias));
            originalLodBias = null;
        }
    }

    public override void OnUnload()
    {
        try
        {
            MainThread.Run(() =>
            {
                DisableEffects();
                CharacterSparkleMask.Teardown();
                var effectsObject = Unity.Find(Prefab + "(Clone)");
                if (!Unity.IsNull(effectsObject))
                {
                    Unity.Destroy(effectsObject);
                }
                cameraTransform?.Dispose();
            });
        }
        catch (Exception exception)
        {
            Log("cleanup: " + exception.Message);
        }
    }
}
