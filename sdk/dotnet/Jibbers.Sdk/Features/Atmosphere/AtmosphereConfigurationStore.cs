using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Features.Atmosphere;

internal readonly record struct AtmosphereConfigurationChange(bool Changed, bool SynchronizeMenu, bool Respawn = false);
/// <summary>Owns persistence, presets, menu translation and typed settings snapshots. No game APIs.</summary>
internal sealed class AtmosphereConfigurationStore(Func<string> settingsPath, Action<string> log) : IAtmosphereSettings
{
    JsonObject configuration = AtmosphereConfiguration.Defaults();
    AtmosphereRenderSettings? renderSettings;
    readonly object synchronization = new();
    readonly AsyncLocal<bool> synchronizingMenu = new();
    void LoadConfiguration()
    {
        lock (synchronization)
        {
            renderSettings = null;
            try
            {
                var path = settingsPath();
                configuration = AtmosphereConfiguration.Load(File.Exists(path) ? File.ReadAllText(path) : null);
                if (!File.Exists(path))
                    SaveConfiguration();
            }
            catch (Exception exception)
            {
                log($"Invalid atmosphere configuration ({exception.Message}); using defaults.");
                configuration = AtmosphereConfiguration.Defaults();
            }
        }
    }

    void SaveConfiguration()
    {
        try
        {
            File.WriteAllText(settingsPath(), AtmosphereConfiguration.Serialize(configuration));
        }
        catch (Exception exception)
        {
            log("Could not save atmosphere settings: " + exception.Message);
        }
    }

    public IEnumerable<Setting> Schema
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

    public void SynchronizeMenu(Action<string, JsonNode> setSetting)
    {
        var snapshot = Capture().Values;
        synchronizingMenu.Value = true;
        try
        {
            setSetting("enabled", AtmosphereConfiguration.GetBool(snapshot, "enabled"));
            if (snapshot["preset"] is JsonValue presetValue)
            {
                try
                {
                    setSetting("preset", presetValue.DeepClone());
                }
                catch (ArgumentException)
                {
                // a preset the tab does not list yet
                }
            }

            foreach (var settingKey in new[]
            {
                "hotkeys",
                "grading"
            }

            )
            {
                setSetting(settingKey, AtmosphereConfiguration.GetBool(snapshot, settingKey));
            }

            foreach (var settingKey in new[]
            {
                "toggleKey",
                "presetKey"
            }

            )
            {
                setSetting(settingKey, (int)AtmosphereConfiguration.GetFloat(snapshot, settingKey));
            }

            setSetting("fog", Math.Round(AtmosphereConfiguration.FogValue(snapshot), 4));
            foreach (var settingKey in new[]
            {
                "fogStart",
                "snow",
                "snowSize",
                "speedStreaks",
                "cloudBank",
                "cloudBankDistance",
                "sunShafts"
            }

            )
            {
                setSetting(settingKey, Math.Round(AtmosphereConfiguration.GetFloat(snapshot, settingKey), 4));
            }
        }
        finally
        {
            synchronizingMenu.Value = false;
        }
    }

    public AtmosphereRenderSettings Capture()
    {
        lock (synchronization)
            return renderSettings ??= new(configuration);
    }

    public AtmosphereConfigurationChange ApplyMenuSetting(string key, JsonNode value)
    {
        if (synchronizingMenu.Value)
            return default;
        lock (synchronization)
        {
            if (key == "preset")
                configuration = AtmosphereConfiguration.WithPreset(configuration, value.GetValue<string>());
            else if (key == "fog")
                AtmosphereConfiguration.Merge(configuration, AtmosphereConfiguration.FogFields((float)value.GetValue<double>()));
            else
                configuration[key] = value.DeepClone();
            renderSettings = null;
            SaveConfiguration();
        }

        return new(true, key == "preset");
    }

    public AtmosphereConfigurationChange ApplyCommand(JsonObject request)
    {
        lock (synchronization)
        {
            var changes = (JsonObject)request.DeepClone();
            changes.Remove("cmd");
            var preset = changes["nextPreset"]?.GetValue<bool>() == true ? AtmosphereConfiguration.NextPreset(configuration) : changes["preset"]?.GetValue<string>();
            changes.Remove("preset");
            if (preset is not null)
                configuration = AtmosphereConfiguration.WithPreset(configuration, preset);
            AtmosphereConfiguration.Merge(configuration, changes);
            if (changes["toggle"]?.GetValue<bool>() == true)
                configuration["enabled"] = !AtmosphereConfiguration.GetBool(configuration, "enabled");
            renderSettings = null;
            SaveConfiguration();
            return new(true, true, changes["respawn"]?.GetValue<bool>() == true);
        }
    }
}
