// The Rust Atmosphere's tests (cloud bank, presets, fog slider), run against the C# port.

using System.Text.Json.Nodes;
using Jibbers.Sdk.Features.Atmosphere;

static class AtmosphereTests
{
    [Test]
    static void CloudsKeepTheirDistanceOnElevatedMapsAndAfterRespawn()
    {
        var settings = AtmosphereConfiguration.Defaults();
        foreach (var elevation in new[] { 100f, 5500f, -300f, 12000f })
        {
            var (baseHeight, topHeight) = new AtmosphereRenderSettings(settings).CloudBank.Bounds(elevation, 0, 150);
            Assert.Equal(180f, elevation - topHeight);
            Assert.Equal(250f, topHeight - baseHeight);
        }
    }

    [Test]
    static void CloudDistanceAndThicknessAreIndependentOfTerrain()
    {
        var settings = AtmosphereConfiguration.Defaults();
        settings["cloudBankDistance"] = 350.0;
        settings["cloudBankThickness"] = 120.0;
        Assert.Equal((5530f, 5650f), new AtmosphereRenderSettings(settings).CloudBank.Bounds(6000, 0, 100));
        Assert.Equal((5530f, 5650f), new AtmosphereRenderSettings(settings).CloudBank.Bounds(6000, 5900, 6500));
    }

    [Test]
    static void OldSettingsGainPlayerRelativeCloudsAndMissingCameraHasAFallback()
    {
        var settings = AtmosphereConfiguration.Load("""{"cloudBankTop": 0.5, "cloudBankThickness": 100.0, "unknownKey": 3}""");
        Assert.Equal(180f, AtmosphereConfiguration.GetFloat(settings, "cloudBankDistance"));
        Assert.Equal((200f, 300f), new AtmosphereRenderSettings(settings).CloudBank.Bounds(null, 100, 500));
        Assert.Equal((200f, 300f), new AtmosphereRenderSettings(settings).CloudBank.Bounds(float.NaN, 100, 500));
        Assert.True(!settings.ContainsKey("unknownKey"));
    }

    [Test]
    static void PresetsCycleBuiltInsFirstThenAlphabetically()
    {
        var settings = AtmosphereConfiguration.Defaults();
        ((JsonObject)settings["presets"]!)["Zebra"] = new JsonObject();
        ((JsonObject)settings["presets"]!)["Alpine"] = new JsonObject { ["snow"] = 0.1 };
        Assert.True(AtmosphereConfiguration.PresetOrder((JsonObject)settings["presets"]!).SequenceEqual(new[] { "Foggy day", "Clear", "Snowstorm", "Alpine", "Zebra" }));
        Assert.Equal("Clear", AtmosphereConfiguration.NextPreset(settings));
        settings["preset"] = "Zebra";
        Assert.Equal("Foggy day", AtmosphereConfiguration.NextPreset(settings));
    }

    [Test]
    static void APresetStartsFromDefaultsAndKeepsTheSwitchAndPresets()
    {
        var settings = AtmosphereConfiguration.Defaults();
        settings["enabled"] = false;
        settings["snow"] = 0.01;
        ((JsonObject)settings["presets"]!)["Mine"] = new JsonObject { ["fogStart"] = 42.0 };
        var nextSettings = AtmosphereConfiguration.WithPreset(settings, "Mine");
        Assert.Equal(false, AtmosphereConfiguration.GetBool(nextSettings, "enabled"));
        Assert.Equal(0.7f, AtmosphereConfiguration.GetFloat(nextSettings, "snow"));
        Assert.Equal(42f, AtmosphereConfiguration.GetFloat(nextSettings, "fogStart"));
        Assert.Equal("Mine", nextSettings["preset"]!.GetValue<string>());
        Assert.True(((JsonObject)nextSettings["presets"]!).ContainsKey("Mine"));
        Assert.Throws<ArgumentException>(() => AtmosphereConfiguration.WithPreset(settings, "Nope"));
    }

    [Test]
    static void MapsMergeKeyByKeyAndNullRemoves()
    {
        var settings = AtmosphereConfiguration.Defaults();
        AtmosphereConfiguration.Merge(settings, (JsonObject)JsonNode.Parse("""{"beautify": {"contrast": 1.5, "saturate": null, "bloomIntensity": 0.3}, "snow": 0.2, "notASetting": 1}""")!);
        var beautifySettings = (JsonObject)settings["beautify"]!;
        Assert.Equal(1.5, beautifySettings["contrast"]!.GetValue<double>());
        Assert.True(!beautifySettings.ContainsKey("saturate"));
        Assert.True(beautifySettings.ContainsKey("tonemap"));
        Assert.Equal(0.2f, AtmosphereConfiguration.GetFloat(settings, "snow"));
        Assert.True(!settings.ContainsKey("notASetting"));
    }

    [Test]
    static void FogSliderRoundTrips()
    {
        var settings = AtmosphereConfiguration.Defaults();
        AtmosphereConfiguration.Merge(settings, AtmosphereConfiguration.FogFields(0.5f));
        Assert.True(MathF.Abs(AtmosphereConfiguration.FogValue(settings) - 0.5f) < 1e-6f);
        Assert.Equal(1f, AtmosphereConfiguration.GetFloat(settings, "skyHaze"));
        AtmosphereConfiguration.Merge(settings, AtmosphereConfiguration.FogFields(0.1f));
        Assert.True(MathF.Abs(AtmosphereConfiguration.GetFloat(settings, "skyHaze") - 0.5f) < 1e-6f);
    }

    [Test]
    static void TheModsTabKeepsExistingControlsInOrder()
    {
        var mod = new Atmosphere.AtmosphereMod();
        var keys = mod.Settings.Select(x => x.Key).ToArray();
        Assert.True(keys.SequenceEqual(new[] { "preset", "hotkeys", "cycleKey", "fog", "fogStart", "snow", "snowSize", "speedStreaks", "cloudBank", "cloudBankDistance", "sunShafts", "grading", "terrainDetailBoost" }), string.Join(",", keys));
        Assert.True(!mod.EnabledToggle);
    }

    [Test]
    static void OneShortcutCyclesOffClearFoggySnowstorm()
    {
        var settings = AtmosphereModes.Select(AtmosphereConfiguration.Defaults(), "Off");
        foreach (var expected in new[] { "Clear", "Foggy", "Snowstorm", "Off", "Clear" })
        {
            settings = AtmosphereModes.Select(settings, AtmosphereModes.Next(settings));
            Assert.Equal(expected, AtmosphereModes.Current(settings));
            Assert.Equal(expected != "Off", AtmosphereConfiguration.GetBool(settings, "enabled"));
        }
    }

    [Test]
    static void ChangingWeatherKeepsShortcutPreferences()
    {
        var settings = AtmosphereConfiguration.Defaults();
        settings["hotkeys"] = false;
        settings["cycleKey"] = 122;
        settings["terrainDetailBoost"] = true;
        var selected = AtmosphereModes.Select(settings, "Clear");
        Assert.True(!AtmosphereConfiguration.GetBool(selected, "hotkeys"));
        Assert.Equal(122f, AtmosphereConfiguration.GetFloat(selected, "cycleKey"));
        Assert.True(AtmosphereConfiguration.GetBool(selected, "terrainDetailBoost"));
    }

    [Test]
    static void LegacyWeatherDoesNotImplicitlyRaiseGameQuality()
    {
        var settings = AtmosphereConfiguration.Load("""{"enabled":true,"lodBias":2.0,"anisotropic":16}""");
        Assert.True(!AtmosphereConfiguration.GetBool(settings, "terrainDetailBoost"));
        foreach (var mode in new[] { "Clear", "Foggy", "Snowstorm" })
            Assert.True(!AtmosphereConfiguration.GetBool(AtmosphereModes.Select(settings, mode), "terrainDetailBoost"));
    }

    [Test]
    static void OldSettingsMigrateWithoutReusingReservedKeys()
    {
        var settings = AtmosphereConfiguration.Load("""{"enabled":true,"preset":"Foggy day","toggleKey":114,"presetKey":115,"hotkeys":false}""");
        Assert.Equal("Foggy", AtmosphereModes.Current(settings));
        Assert.Equal(121f, AtmosphereConfiguration.GetFloat(settings, "cycleKey"));
        Assert.True(!settings.ContainsKey("toggleKey"));
        Assert.True(!settings.ContainsKey("presetKey"));
        Assert.True(!AtmosphereConfiguration.GetBool(settings, "hotkeys"));
    }

    [Test]
    static void MenuModeSelectionEnablesPresetAndOffDisablesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new AtmosphereConfigurationStore(() => path, _ => { });
            foreach (var mode in new[] { "Off", "Clear", "Foggy", "Snowstorm" })
            {
                Assert.True(store.ApplyMenuSetting("preset", JsonValue.Create(mode)!).SynchronizeMenu);
                Assert.Equal(mode != "Off", store.Capture().Enabled);
                string? shownMode = null;
                store.SynchronizeMenu((key, value) => { if (key == "preset") shownMode = value.GetValue<string>(); });
                Assert.Equal(mode, shownMode);
            }
        }
        finally { File.Delete(path); }
    }
}
