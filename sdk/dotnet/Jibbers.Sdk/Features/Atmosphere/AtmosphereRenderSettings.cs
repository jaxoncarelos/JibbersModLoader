using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Features.Atmosphere;

/// <summary>A stable settings snapshot shared by one or more frames, rebuilt only after configuration changes.</summary>
internal sealed class AtmosphereRenderSettings
{
    public JsonObject Values { get; }
    public bool Enabled { get; }
    public bool GradingEnabled { get; }
    public FogSettings Fog { get; }
    public SnowSettings Snow { get; }
    public CloudShadowSettings CloudShadows { get; }
    public OcclusionSettings Occlusion { get; }
    public SparkleSettings Sparkles { get; }
    public SunShaftSettings SunShafts { get; }
    public SpeedEffectSettings SpeedEffects { get; }
    public CloudBankSettings CloudBank { get; }

    public AtmosphereRenderSettings(JsonObject configuration)
    {
        Values = (JsonObject)configuration.DeepClone();
        Enabled = AtmosphereConfiguration.GetBool(Values, "enabled");
        GradingEnabled = AtmosphereConfiguration.GetBool(Values, "grading");
        Fog = new(
            Number("fogDensity"), Number("fogFalloff"), Number("fogBaseOffset"), Unit("fogMax"),
            Number("fogStart"), Unit("skyHaze"), Number("skyDistance"), Number("hazeDensity"),
            ReadVector3("fogTint"), ReadVector3("topTint"), Number("skyBand"), Number("sunGlow"), Number("sunGlowSharpness"));
        Snow = new(
            Unit("snow"), Number("snowBox"), Number("snowFall"), Number("snowSize"),
            ReadVector3("wind"), Number("flutter"), Unit("snowOpacity"));
        CloudShadows = new(
            NonNegative("cloudShadows"), 1 / MathF.Max(Number("cloudSize"), 1), Unit("cloudCoverage"), Number("cloudHeight"),
            ReadVector2("cloudSpeed"), Number("cloudSoftness"), AtmosphereConfiguration.GetBool(Values, "debugSplit"), ReadVector3("cloudTint"));
        Occlusion = new(
            NonNegative("ao"), MathF.Max(Number("aoRadius"), 0.05f), Number("aoDistance"),
            MathF.Sin(Number("aoMinAngle") * MathF.PI / 180), ReadVector3("aoTint"));
        Sparkles = new(NonNegative("sparkle"), Unit("sparkleDensity"), Number("sparkleDistance"), MathF.Max(Number("sparkleSize"), 0.002f));
        SunShafts = new(NonNegative("sunShafts"), Math.Clamp(Number("sunShaftReach"), 0.05f, 1), Number("sunShaftSharpness"));
        SpeedEffects = new(NonNegative("speedStreaks"), NonNegative("speedFrost"), ReadVector2("speedFrostKmh"), ReadVector3("debugCamVel"));
        CloudBank = new(
            NonNegative("cloudBank"), MathF.Max(Number("cloudBankDistance"), 0), Number("cloudBankTop"),
            MathF.Max(Number("cloudBankThickness"), 1), 1 / MathF.Max(Number("cloudBankSize"), 1),
            ReadVector2("cloudBankSpeed"), Unit("cloudBankCoverage"), Number("cloudBankMargin"));
    }

    float Number(string key) => AtmosphereConfiguration.GetFloat(Values, key);
    float Unit(string key) => Math.Clamp(Number(key), 0, 1);
    float NonNegative(string key) => MathF.Max(Number(key), 0);
    Vector3 ReadVector3(string key)
    {
        var components = AtmosphereConfiguration.GetArray(Values, key);
        if (components.Length < 3) throw new ArgumentException($"{key} requires three components.");
        return new(components[0], components[1], components[2]);
    }
    Vector2 ReadVector2(string key)
    {
        var components = AtmosphereConfiguration.GetArray(Values, key);
        if (components.Length < 2) throw new ArgumentException($"{key} requires two components.");
        return new(components[0], components[1]);
    }
}

internal sealed record FogSettings(float Density, float HeightFalloff, float BaseOffset, float MaximumOpacity,
    float StartDistance, float SkyHaze, float SkyDistance, float HazeDensity, Vector3 Tint, Vector3 TopTint,
    float SkyBand, float SunGlow, float SunGlowSharpness);
internal sealed record SnowSettings(float Amount, float BoxSize, float FallSpeed, float FlakeSize, Vector3 Wind, float Flutter, float Opacity);
internal sealed record CloudShadowSettings(float Strength, float InverseSize, float Coverage, float Height,
    Vector2 Speed, float Softness, bool DebugSplit, Vector3 Tint);
internal sealed record OcclusionSettings(float Strength, float Radius, float Distance, float MinimumAngleSine, Vector3 Tint);
internal sealed record SparkleSettings(float Strength, float Density, float Distance, float Size);
internal sealed record SunShaftSettings(float Strength, float Reach, float Sharpness);
internal sealed record SpeedEffectSettings(float StreakStrength, float FrostStrength, Vector2 FrostSpeedRangeKmh, Vector3 DebugVelocity);
internal sealed record CloudBankSettings(float Strength, float DistanceBelowCamera, float TerrainHeightFraction, float Thickness,
    float InverseSize, Vector2 Speed, float Coverage, float Margin)
{
    public (float Base, float Top) Bounds(float? cameraHeight, float terrainFloor, float terrainCeiling)
    {
        var top = cameraHeight is { } height && float.IsFinite(height)
            ? height - DistanceBelowCamera
            : terrainFloor + TerrainHeightFraction * MathF.Max(terrainCeiling - terrainFloor, 50);
        return (top - Thickness, top);
    }
}
