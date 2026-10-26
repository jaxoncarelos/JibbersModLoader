using Jibbers.Sdk;
using System.Text.Json.Nodes;

namespace Atmosphere;

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
            return new[]
            {
                Setting.Choice("preset", "Atmosphere", AtmosphereModes.Choices(configuration), AtmosphereModes.Current(configuration)).WithTooltip("Off, Clear, Foggy or Snowstorm. F10 cycles through the same modes."),
                Setting.Toggle("hotkeys", "Hotkeys", true).WithTooltip("Keyboard shortcuts, while the game has focus."),
                Setting.KeyBinding("cycleKey", "Cycle atmosphere", (int)Key.F10).WithTooltip("Cycle Off, Clear, Foggy and Snowstorm."),
                Setting.Slider("fog", "Fog thickness", 0.33, 0, 1, 0.05).InSection("Fog").WithTooltip("How thick the haze is, near and far."),
                Setting.Slider("fogStart", "Fog start", 5, 0, 60, 1).InSection("Fog").WithTooltip("Distance (m) before any fog, so nearby ground stays crisp."),
                Setting.Slider("snow", "Snowfall", 0.7, 0, 1, 0.05).InSection("Snow").WithTooltip("Fraction of the snowflakes shown."),
                Setting.Slider("snowSize", "Flake size", 0.035, 0.01, 0.08, 0.005).InSection("Snow").WithTooltip("Radius of a snowflake (m)."),
                Setting.Slider("speedStreaks", "Speed streaks", 1, 0, 2, 0.1).InSection("Snow").WithTooltip("How much falling snow streaks with your speed."),
                Setting.Slider("cloudBank", "Valley clouds", 1, 0, 2, 0.1).InSection("Sky").WithTooltip("Cloud sea below the rider, following your elevation."),
                Setting.Slider("cloudBankDistance", "Cloud distance below", 180, 60, 1000, 10).InSection("Sky").WithTooltip("Metres from your camera down to the cloud tops."),
                Setting.Slider("sunShafts", "Sun shafts", 0, 0, 2, 0.1).InSection("Sky").WithTooltip("Light shafts around the sun."),
                Setting.Toggle("grading", "Colour grading", true).InSection("Colour").WithTooltip("Filmic tonemapping, white balance and bloom."),
                Setting.Toggle("terrainDetailBoost", "Terrain detail boost", false).InSection("Performance").WithTooltip("Optional extra terrain detail and anisotropic filtering. Leave off to retain the game's quality settings."),
            };
        }
    }

    public void SynchronizeMenu(Action<string, JsonNode> setSetting)
    {
        var snapshot = Capture().Values;
        synchronizingMenu.Value = true;
        try
        {
            if (snapshot["preset"] is JsonValue)
            {
                try
                {
                    setSetting("preset", JsonValue.Create(AtmosphereModes.Current(snapshot))!);
                }
                catch (ArgumentException)
                {
                // a preset the tab does not list yet
                }
            }

            foreach (var settingKey in new[]
            {
                "hotkeys",
                "grading",
                "terrainDetailBoost"
            }

            )
            {
                setSetting(settingKey, AtmosphereConfiguration.GetBool(snapshot, settingKey));
            }

            foreach (var settingKey in new[]
            {
                "cycleKey"
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
                configuration = AtmosphereModes.Select(configuration, value.GetValue<string>());
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
            var preset = changes["nextPreset"]?.GetValue<bool>() == true ? AtmosphereModes.Next(configuration) : changes["preset"]?.GetValue<string>();
            changes.Remove("preset");
            if (preset is not null)
                configuration = AtmosphereModes.Select(configuration, preset);
            AtmosphereConfiguration.Merge(configuration, changes);
            if (changes["toggle"]?.GetValue<bool>() == true)
                configuration["enabled"] = !AtmosphereConfiguration.GetBool(configuration, "enabled");
            renderSettings = null;
            SaveConfiguration();
            return new(true, true, changes["respawn"]?.GetValue<bool>() == true);
        }
    }
}
