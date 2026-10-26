using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Atmosphere;

static class AtmosphereRenderingTests
{
    static AtmosphereFrame Scene => new()
    {
        FogColor = new(0.8f, 0.85f, 0.95f, 1),
        SunDirection = new(0.3f, 0.8f, 0.5f),
        SunColor = new(6, 2, 0.4f),
        AmbientLight = new(0.5f, 0.52f, 0.55f, 1),
        CameraPosition = new(0, 500, 0),
        CameraVelocity = new(10, 0, 0),
        TerrainFloor = 100,
        TerrainCeiling = 1000,
        MapRectangle = new(0, 0, 1000, 2000),
        SparkleMaskReady = true,
    };
    static Dictionary<string, Vector4> Render(JsonObject settings, AtmosphereFrame? scene = null)
    {
        var vectors = new Dictionary<string, Vector4>();
        new AtmosphereShaderParameters((name, value) => vectors.Add(name, value)).Apply(new(settings), scene ?? Scene);
        return vectors;
    }
    static void Near(float expected, float actual)
    {
        Assert.True(float.IsFinite(actual) && MathF.Abs(expected - actual) < 0.00001f, $"expected {expected}, got {actual}");
    }
    static void Near(Vector4 expected, Vector4 actual)
    {
        Near(expected.X, actual.X); Near(expected.Y, actual.Y); Near(expected.Z, actual.Z); Near(expected.W, actual.W);
    }

    [Test]
    static void DefaultFrameRetainsTheExistingShaderContract()
    {
        // Reference values from the original frame, including HDR sunlight, snow lighting and terrain anchoring.
        var expected = new Dictionary<string, Vector4>
        {
            ["_JA_FogColor"] = new(0.8f, 0.8075f, 0.817f, 1),
            ["_JA_FogTop"] = new(0.8f, 0.7752f, 0.7353f, 0),
            ["_JA_Fog"] = new(0.004f, 0.012f, 100, 1),
            ["_JA_Fog2"] = new(5, 1, 3000, 0.004f),
            ["_JA_SunDir"] = new(0.3f, 0.8f, 0.5f, 0.25f),
            ["_JA_SunColor"] = new(2, 1, 0.2f, 10),
            ["_JA_Snow"] = new(0.7f, 40, 1.1f, 0.035f),
            ["_JA_Wind"] = new(1.2f, 0, 0.5f, 0.35f),
            ["_JA_SnowColor"] = new(1, 1, 0.7875f, 0.85f),
            ["_JA_Cloud"] = new(0, 0.002f, 0.4f, 2000),
            ["_JA_Cloud2"] = new(6, 2.5f, 0.12f, 0),
            ["_JA_CloudTint"] = new(0.62f, 0.7f, 0.86f, 1),
            ["_JA_AO"] = new(0, 0.6f, 80, 0.3746066f),
            ["_JA_AOTint"] = new(0.5f, 0.58f, 0.72f, 1),
            ["_JA_Sparkle"] = new(1, 0.04f, 40, 0.015f),
            ["_JA_Shafts"] = new(0, 0.8f, 0.96f, 6),
            ["_JA_CamVel"] = new(10, 0, 0, 10),
            ["_JA_Speed"] = new(1, 0, 0, 0),
            ["_JA_Bank"] = new(1, 70, 320, 1f / 450),
            ["_JA_Bank2"] = new(1.5f, 0.6f, 0.6f, 120),
            ["_JA_MapRect"] = new(0, 0, 1000, 2000),
        };
        var actual = Render(AtmosphereConfiguration.Defaults());
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (name, value) in expected) Near(value, actual[name]);
    }

    [Test]
    static void MissingCameraUsesTerrainAndFailedCharacterMaskDisablesSparkles()
    {
        var vectors = Render(AtmosphereConfiguration.Defaults(), Scene with { CameraPosition = null, SparkleMaskReady = false });
        Near(new Vector4(1, 345, 595, 1f / 450), vectors["_JA_Bank"]);
        Near(1600, vectors["_JA_Cloud"].W);
        Near(0, vectors["_JA_Sparkle"].X);
    }

    [Test]
    static void SpeedFrostUsesKilometresPerHourAndSmoothlyBlendsTheRange()
    {
        var settings = AtmosphereConfiguration.Defaults();
        settings["speedFrost"] = 2;
        settings["speedFrostKmh"] = new JsonArray(0, 72);
        foreach (var (velocity, frost) in new[] { (0f, 0f), (10f, 1f), (20f, 2f), (40f, 2f) })
        {
            var vectors = Render(settings, Scene with { CameraVelocity = new(velocity, 0, 0) });
            Near(frost, vectors["_JA_Speed"].Y);
            Near(velocity, vectors["_JA_CamVel"].W);
        }
    }

    [Test]
    static void SettingsLimitsPreventInvalidShaderRanges()
    {
        var settings = AtmosphereConfiguration.Defaults();
        settings["fogMax"] = 5;
        settings["snow"] = -1;
        settings["cloudSize"] = 0;
        settings["aoRadius"] = -1;
        settings["sparkleSize"] = -1;
        settings["sunShaftReach"] = 0;
        settings["cloudBankSize"] = -10;
        var vectors = Render(settings);
        Near(1, vectors["_JA_Fog"].W);
        Near(0, vectors["_JA_Snow"].X);
        Near(1, vectors["_JA_Cloud"].Y);
        Near(0.05f, vectors["_JA_AO"].Y);
        Near(0.002f, vectors["_JA_Sparkle"].W);
        Near(0.05f, vectors["_JA_Shafts"].Y);
        Near(1, vectors["_JA_Bank"].W);
    }

    [Test]
    static void ExistingColorArraysMayIncludeAnUnusedAlphaComponent()
    {
        var settings = AtmosphereConfiguration.Defaults();
        settings["fogTint"] = new JsonArray(1, 0.95, 0.86, 1);
        settings["cloudSpeed"] = new JsonArray(6, 2.5, 0);
        var vectors = Render(settings);
        Near(new Vector4(0.8f, 0.8075f, 0.817f, 1), vectors["_JA_FogColor"]);
        Near(new Vector4(6, 2.5f, 0.12f, 0), vectors["_JA_Cloud2"]);
    }

    [Test]
    static void DisableClearsEveryEffectSwitch()
    {
        var vectors = new Dictionary<string, Vector4>();
        var shader = new AtmosphereShaderParameters((name, value) => vectors[name] = value);
        shader.Apply(new(AtmosphereConfiguration.Defaults()), Scene);
        shader.Disable();
        foreach (var name in new[] { "_JA_FogColor", "_JA_Snow", "_JA_Cloud", "_JA_AO", "_JA_Sparkle", "_JA_Shafts", "_JA_Speed", "_JA_Bank" })
            Assert.Equal(default(Vector4), vectors[name]);
    }

    [Test]
    static void RenderSnapshotIsIsolatedFromLaterConfigurationEdits()
    {
        var configuration = AtmosphereConfiguration.Defaults();
        var snapshot = new AtmosphereRenderSettings(configuration);
        configuration["snow"] = 0;
        configuration["fogTint"]![0] = 0;
        Assert.Equal(0.7f, snapshot.Snow.Amount);
        Assert.Equal(1f, snapshot.Fog.Tint.X);
        Assert.Equal(0.7f, AtmosphereConfiguration.GetFloat(snapshot.Values, "snow"));
    }

    [Test]
    static void MenuChangesReplaceTheCachedSnapshotAndKeepPreviousFramesStable()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "jibbers_atmosphere.json");
        var previousFile = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            var effects = new AtmosphereMod();
            var original = effects.CaptureSettings();
            Assert.True(ReferenceEquals(original, effects.CaptureSettings()));
            effects.OnSettingChanged("snow", JsonValue.Create(0.2)!);
            var updated = effects.CaptureSettings();
            Assert.True(!ReferenceEquals(original, updated));
            Assert.True(ReferenceEquals(updated, effects.CaptureSettings()));
            Assert.Equal(0.7f, original.Snow.Amount);
            Assert.Equal(0.2f, updated.Snow.Amount);
        }
        finally
        {
            if (previousFile is null) File.Delete(settingsPath);
            else File.WriteAllBytes(settingsPath, previousFile);
        }
    }
}
