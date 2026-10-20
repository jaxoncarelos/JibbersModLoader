// The Rust Atmosphere's tests (cloud bank, presets, fog slider), run against the C# port.

using System.Text.Json.Nodes;
using Atmosphere;

static class AtmosphereTests
{
    [Test]
    static void CloudsKeepTheirDistanceOnElevatedMapsAndAfterRespawn()
    {
        var s = AtmoConfig.Defaults();
        foreach (var elevation in new[] { 100f, 5500f, -300f, 12000f })
        {
            var (b, top) = AtmoConfig.CloudBankBounds(s, elevation, 0, 150);
            Assert.Equal(180f, elevation - top);
            Assert.Equal(250f, top - b);
        }
    }

    [Test]
    static void CloudDistanceAndThicknessAreIndependentOfTerrain()
    {
        var s = AtmoConfig.Defaults();
        s["cloudBankDistance"] = 350.0;
        s["cloudBankThickness"] = 120.0;
        Assert.Equal((5530f, 5650f), AtmoConfig.CloudBankBounds(s, 6000, 0, 100));
        Assert.Equal((5530f, 5650f), AtmoConfig.CloudBankBounds(s, 6000, 5900, 6500));
    }

    [Test]
    static void OldSettingsGainPlayerRelativeCloudsAndMissingCameraHasAFallback()
    {
        var s = AtmoConfig.Load("""{"cloudBankTop": 0.5, "cloudBankThickness": 100.0, "unknownKey": 3}""");
        Assert.Equal(180f, AtmoConfig.F(s, "cloudBankDistance"));
        Assert.Equal((200f, 300f), AtmoConfig.CloudBankBounds(s, null, 100, 500));
        Assert.Equal((200f, 300f), AtmoConfig.CloudBankBounds(s, float.NaN, 100, 500));
        Assert.True(!s.ContainsKey("unknownKey"));
    }

    [Test]
    static void PresetsCycleBuiltInsFirstThenAlphabetically()
    {
        var s = AtmoConfig.Defaults();
        ((JsonObject)s["presets"]!)["Zebra"] = new JsonObject();
        ((JsonObject)s["presets"]!)["Alpine"] = new JsonObject { ["snow"] = 0.1 };
        Assert.True(AtmoConfig.PresetOrder((JsonObject)s["presets"]!).SequenceEqual(new[] { "Foggy day", "Clear", "Snowstorm", "Alpine", "Zebra" }));
        Assert.Equal("Clear", AtmoConfig.NextPreset(s));
        s["preset"] = "Zebra";
        Assert.Equal("Foggy day", AtmoConfig.NextPreset(s));
    }

    [Test]
    static void APresetStartsFromDefaultsAndKeepsTheSwitchAndPresets()
    {
        var s = AtmoConfig.Defaults();
        s["enabled"] = false;
        s["snow"] = 0.01;
        ((JsonObject)s["presets"]!)["Mine"] = new JsonObject { ["fogStart"] = 42.0 };
        var n = AtmoConfig.WithPreset(s, "Mine");
        Assert.Equal(false, AtmoConfig.B(n, "enabled"));
        Assert.Equal(0.7f, AtmoConfig.F(n, "snow"));
        Assert.Equal(42f, AtmoConfig.F(n, "fogStart"));
        Assert.Equal("Mine", n["preset"]!.GetValue<string>());
        Assert.True(((JsonObject)n["presets"]!).ContainsKey("Mine"));
        Assert.Throws<ArgumentException>(() => AtmoConfig.WithPreset(s, "Nope"));
    }

    [Test]
    static void MapsMergeKeyByKeyAndNullRemoves()
    {
        var s = AtmoConfig.Defaults();
        AtmoConfig.Merge(s, (JsonObject)JsonNode.Parse("""{"beautify": {"contrast": 1.5, "saturate": null, "bloomIntensity": 0.3}, "snow": 0.2, "notASetting": 1}""")!);
        var b = (JsonObject)s["beautify"]!;
        Assert.Equal(1.5, b["contrast"]!.GetValue<double>());
        Assert.True(!b.ContainsKey("saturate"));
        Assert.True(b.ContainsKey("tonemap"));
        Assert.Equal(0.2f, AtmoConfig.F(s, "snow"));
        Assert.True(!s.ContainsKey("notASetting"));
    }

    [Test]
    static void FogSliderRoundTrips()
    {
        var s = AtmoConfig.Defaults();
        AtmoConfig.Merge(s, AtmoConfig.FogFields(0.5f));
        Assert.True(MathF.Abs(AtmoConfig.FogValue(s) - 0.5f) < 1e-6f);
        Assert.Equal(1f, AtmoConfig.F(s, "skyHaze"));
        AtmoConfig.Merge(s, AtmoConfig.FogFields(0.1f));
        Assert.True(MathF.Abs(AtmoConfig.F(s, "skyHaze") - 0.5f) < 1e-6f);
    }

    [Test]
    static void TheModsTabHasTheRustControlsInOrder()
    {
        var mod = new AtmosphereMod();
        var keys = mod.Settings.Select(x => x.Key).ToArray();
        Assert.True(keys.SequenceEqual(new[] { "preset", "hotkeys", "toggleKey", "presetKey", "fog", "fogStart", "snow", "snowSize", "speedStreaks", "cloudBank", "cloudBankDistance", "sunShafts", "grading" }), string.Join(",", keys));
    }
}
