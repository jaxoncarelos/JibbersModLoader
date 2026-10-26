// Atmosphere's settings: a camelCase JSON document (jibbers_atmosphere.json beside the game, the same
// file and format the native mod used, so players' settings carry over), with named partial presets. Game-free, unit tested.
using Jibbers.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atmosphere;

internal static class AtmosphereConfiguration
{
    /// <summary>Every setting with its default (the Rust Settings::default()).</summary>
    public static JsonObject Defaults() => (JsonObject)JsonNode.Parse("""
    {
      "enabled": true,
      "fogDensity": 0.004, "fogFalloff": 0.012, "fogBaseOffset": 0.0, "hazeDensity": 0.004,
      "fogStart": 5.0, "fogMax": 1.0, "skyHaze": 1.0, "skyDistance": 3000.0, "skyBand": 0.0,
      "fogTint": [1.0, 0.95, 0.86], "topTint": [1.0, 0.96, 0.9],
      "sunGlow": 0.25, "sunGlowSharpness": 10.0,
      "snow": 0.7, "snowBox": 40.0, "snowFall": 1.1, "snowSize": 0.035, "snowOpacity": 0.85,
      "wind": [1.2, 0.0, 0.5], "flutter": 0.35,
      "cloudShadows": 0.0, "cloudSize": 500.0, "cloudCoverage": 0.4, "cloudHeight": 1500.0,
      "cloudSpeed": [6.0, 2.5], "cloudSoftness": 0.12, "cloudTint": [0.62, 0.7, 0.86],
      "ao": 0.0, "aoRadius": 0.6, "aoDistance": 80.0, "aoMinAngle": 22.0, "aoTint": [0.5, 0.58, 0.72],
      "sparkle": 1.0, "sparkleDensity": 0.04, "sparkleDistance": 40.0, "sparkleSize": 0.015,
      "sunShafts": 0.0, "sunShaftReach": 0.8, "sunShaftSharpness": 6.0,
      "speedStreaks": 1.0, "speedFrost": 0.0, "speedFrostKmh": [60.0, 120.0],
      "cloudBank": 1.0, "cloudBankTop": 0.55, "cloudBankDistance": 180.0, "cloudBankMargin": 120.0,
      "cloudBankThickness": 250.0, "cloudBankSize": 450.0, "cloudBankCoverage": 0.6, "cloudBankSpeed": [1.5, 0.6],
      "snowSurface": false, "snowSurfaceScale": 2.5, "snowSurfaceNormal": 2.5, "snowSurfaceDuneScale": 60.0, "snowSurfaceDuneNormal": 0.6,
      "terrainDetailBoost": false, "anisotropic": 16, "lodBias": 2.0,
      "debugCamVel": [0.0, 0.0, 0.0], "debugSplit": false,
      "grading": true,
      "beautify": { "tonemap": 1, "tonemapExposurePre": 1.1, "contrast": 1.1, "saturate": 0.15, "colorTemp": 8000.0, "colorTempBlend": 0.6, "bloomIntensity": 0.12, "bloomThreshold": 0.9 },
      "environment": { "ambientSaturation": 0.55 },
      "hotkeys": true, "cycleKey": 121,
      "preset": "Foggy day",
      "presets": {
        "Foggy day": {},
        "Clear": { "hazeDensity": 0.00045, "fogDensity": 0.0022, "fogMax": 0.9, "skyHaze": 0.8, "skyBand": 9.0, "fogTint": [1.0, 1.0, 1.0], "topTint": [0.9, 0.95, 1.05], "snow": 0.3, "sunShafts": 0.8, "sunGlow": 0.6, "fogStart": 25.0 },
        "Snowstorm": { "hazeDensity": 0.012, "fogDensity": 0.008, "fogStart": 2.0, "snow": 1.0, "wind": [6.0, 0.0, 2.0], "flutter": 0.8 }
      }
    }
    """)!;
    /// <summary>Legacy preset ordering for configuration files; mode cycling is defined by AtmosphereModes.</summary>
    public static readonly string[] BuiltInPresets =
    {
        "Foggy day",
        "Clear",
        "Snowstorm"
    };
    public static List<string> PresetOrder(JsonObject presets)
    {
        var names = BuiltInPresets.Where(presets.ContainsKey).ToList();
        names.AddRange(presets.Select(entry => entry.Key).Where(key => !BuiltInPresets.Contains(key)).Order(StringComparer.Ordinal));
        return names;
    }

    /// <summary>The settings from a file's text: defaults, with the file's known keys on top (an old or partial file still loads).</summary>
    public static JsonObject Load(string? text)
    {
        var settings = Defaults();
        if (text is null)
        {
            return settings;
        }

        if (JsonNode.Parse(text)is JsonObject file)
        {
            foreach (var(key, value)in file)
            {
                if (settings.ContainsKey(key) && value is not null)
                {
                    settings[key] = value.DeepClone();
                }
            }
        }

        return settings;
    }

    /// <summary>Writes the fields of <paramref name = "changes"/> into <paramref name = "current"/>. Maps merge key by key (null removes); unknown keys are ignored.</summary>
    public static void Merge(JsonObject current, JsonObject changes)
    {
        foreach (var(key, value)in changes)
        {
            if (!current.ContainsKey(key))
            {
                continue;
            }

            if (current[key] is JsonObject currentObject && value is JsonObject patchObject)
            {
                foreach (var(propertyName, propertyValue)in patchObject)
                {
                    if (propertyValue is null)
                    {
                        currentObject.Remove(propertyName);
                    }
                    else
                    {
                        currentObject[propertyName] = propertyValue.DeepClone();
                    }
                }
            }
            else
            {
                current[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>The settings with a preset applied: defaults, keeping the user's presets and the master switch, then the preset's fields.</summary>
    public static JsonObject WithPreset(JsonObject current, string name)
    {
        if ((current["presets"] as JsonObject)?[name] is not JsonObject partial)
        {
            throw new ArgumentException($"no preset named \"{name}\"");
        }

        var next = Defaults();
        next["enabled"] = current["enabled"]?.DeepClone();
        next["presets"] = current["presets"]?.DeepClone();
        next["preset"] = name;
        Merge(next, partial);
        return next;
    }

    /// <summary>The preset after the current one (the first if the current name is unknown).</summary>
    public static string NextPreset(JsonObject current)
    {
        var order = PresetOrder(current["presets"] as JsonObject ?? new JsonObject());
        if (order.Count == 0)
        {
            throw new ArgumentException("no presets defined");
        }

        var index = order.IndexOf(current["preset"]?.GetValue<string>() ?? "");
        return order[index < 0 ? 0 : (index + 1) % order.Count];
    }

    /// <summary>hazeDensity and fogDensity at fog thickness 1 (the Snowstorm preset's scale).</summary>
    public const float FogHazeFull = 0.012f, FogDensityFull = 0.008f;
    /// <summary>The fog slider value to the fields it sets: both densities scale, the sky is fully hazed from 0.15 up.</summary>
    public static JsonObject FogFields(float value) => new()
    {
        ["hazeDensity"] = value * FogHazeFull,
        ["fogDensity"] = value * FogDensityFull,
        ["skyHaze"] = value > 0.15f ? 1f : value * 5,
    };
    /// <summary>The fog slider value read back from the settings.</summary>
    public static float FogValue(JsonObject settings) => Math.Clamp(GetFloat(settings, "hazeDensity") / FogHazeFull, 0, 1);
    public static float GetFloat(JsonObject settings, string key) => settings[key] is JsonValue value ? (float)double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : 0;
    public static float[] GetArray(JsonObject settings, string key) => (settings[key] as JsonArray)?.Select(component => component is null ? 0f : (float)double.Parse(component.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)).ToArray() ?? Array.Empty<float>();
    public static bool GetBool(JsonObject settings, string key) => settings[key] is JsonValue value && value.TryGetValue(out bool booleanValue) && booleanValue;
    public static string Serialize(JsonObject settings) => settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
