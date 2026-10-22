namespace Jibbers.Sdk.Features.Atmosphere;

/// <summary>Scene measurements used to shade one frame; contains no game object handles.</summary>
internal readonly record struct AtmosphereFrame
{
    public Color FogColor { get; init; }
    public Vector3 SunDirection { get; init; }
    public Vector3 SunColor { get; init; }
    public Color AmbientLight { get; init; }
    public Vector3? CameraPosition { get; init; }
    public Vector3 CameraVelocity { get; init; }
    public float TerrainFloor { get; init; }
    public float TerrainCeiling { get; init; }
    public Vector4 MapRectangle { get; init; }
    public bool SparkleMaskReady { get; init; }
}

/// <summary>Translates named effect settings into the shader's packed vector layout. No Unity calls.</summary>
internal sealed class AtmosphereShaderParameters(Action<string, Vector4> writeVector)
{
    static readonly string[] EffectSwitches =
    {
        "_JA_FogColor",
        "_JA_Snow",
        "_JA_Cloud",
        "_JA_AO",
        "_JA_Sparkle",
        "_JA_Shafts",
        "_JA_Speed",
        "_JA_Bank",
    };
    public void Disable()
    {
        foreach (var property in EffectSwitches)
            writeVector(property, default);
    }

    public void Apply(AtmosphereRenderSettings settings, in AtmosphereFrame frame)
    {
        var sunlight = new Vector3(MathF.Min(frame.SunColor.X, 4), MathF.Min(frame.SunColor.Y, 4), MathF.Min(frame.SunColor.Z, 4));
        WriteFog(settings.Fog, frame, sunlight);
        WriteSnow(settings.Snow, frame.AmbientLight, sunlight);
        WriteCloudShadows(settings.CloudShadows, frame.CameraPosition?.Y ?? frame.TerrainFloor);
        WriteOcclusion(settings.Occlusion);
        WriteSparkles(settings.Sparkles, frame.SparkleMaskReady);
        WriteSunShafts(settings.SunShafts);
        WriteSpeedEffects(settings.SpeedEffects, frame.CameraVelocity);
        WriteCloudBank(settings.CloudBank, frame);
    }

    void WriteFog(FogSettings fog, in AtmosphereFrame frame, Vector3 sunlight)
    {
        var fogColor = new Vector3(frame.FogColor.R * fog.Tint.X, frame.FogColor.G * fog.Tint.Y, frame.FogColor.B * fog.Tint.Z);
        var topColor = new Vector3(fogColor.X * fog.TopTint.X, fogColor.Y * fog.TopTint.Y, fogColor.Z * fog.TopTint.Z);
        Write("_JA_FogColor", fogColor, 1);
        Write("_JA_FogTop", topColor, fog.SkyBand);
        writeVector("_JA_Fog", new(fog.Density, fog.HeightFalloff, frame.TerrainFloor + fog.BaseOffset, fog.MaximumOpacity));
        writeVector("_JA_Fog2", new(fog.StartDistance, fog.SkyHaze, fog.SkyDistance, fog.HazeDensity));
        Write("_JA_SunDir", frame.SunDirection, fog.SunGlow);
        Write("_JA_SunColor", sunlight * 0.5f, fog.SunGlowSharpness);
    }

    void WriteSnow(SnowSettings snow, Color ambientLight, Vector3 sunlight)
    {
        writeVector("_JA_Snow", new(snow.Amount, snow.BoxSize, snow.FallSpeed, snow.FlakeSize));
        Write("_JA_Wind", snow.Wind, snow.Flutter);
        // Flakes receive skylight and a little sunlight, capped at white.
        var snowLighting = new Vector3(MathF.Min(ambientLight.R * 1.25f + sunlight.X * 0.25f, 1), MathF.Min(ambientLight.G * 1.25f + sunlight.Y * 0.25f, 1), MathF.Min(ambientLight.B * 1.25f + sunlight.Z * 0.25f, 1));
        Write("_JA_SnowColor", snowLighting, snow.Opacity);
    }

    void WriteCloudShadows(CloudShadowSettings clouds, float cameraHeight)
    {
        writeVector("_JA_Cloud", new(clouds.Strength, clouds.InverseSize, clouds.Coverage, cameraHeight + clouds.Height));
        writeVector("_JA_Cloud2", new(clouds.Speed.X, clouds.Speed.Y, clouds.Softness, clouds.DebugSplit ? 1 : 0));
        Write("_JA_CloudTint", clouds.Tint, 1);
    }

    void WriteOcclusion(OcclusionSettings occlusion)
    {
        writeVector("_JA_AO", new(occlusion.Strength, occlusion.Radius, occlusion.Distance, occlusion.MinimumAngleSine));
        Write("_JA_AOTint", occlusion.Tint, 1);
    }

    void WriteSparkles(SparkleSettings sparkles, bool maskReady) => writeVector("_JA_Sparkle", new(maskReady ? sparkles.Strength : 0, sparkles.Density, sparkles.Distance, sparkles.Size));
    void WriteSunShafts(SunShaftSettings shafts) => writeVector("_JA_Shafts", new(shafts.Strength, shafts.Reach, 0.96f, shafts.Sharpness));
    void WriteSpeedEffects(SpeedEffectSettings effects, Vector3 measuredVelocity)
    {
        var cameraVelocity = measuredVelocity + effects.DebugVelocity;
        var speedKmh = cameraVelocity.Magnitude * 3.6f;
        var frostRange = effects.FrostSpeedRangeKmh;
        var frostProgress = Math.Clamp((speedKmh - frostRange.X) / MathF.Max(frostRange.Y - frostRange.X, 1), 0, 1);
        var frostBlend = frostProgress * frostProgress * (3 - 2 * frostProgress);
        Write("_JA_CamVel", cameraVelocity, speedKmh / 3.6f);
        writeVector("_JA_Speed", new(effects.StreakStrength, effects.FrostStrength * frostBlend, 0, 0));
    }

    void WriteCloudBank(CloudBankSettings clouds, in AtmosphereFrame frame)
    {
        var(baseHeight, topHeight) = clouds.Bounds(frame.CameraPosition?.Y, frame.TerrainFloor, frame.TerrainCeiling);
        writeVector("_JA_Bank", new(clouds.Strength, baseHeight, topHeight, clouds.InverseSize));
        writeVector("_JA_Bank2", new(clouds.Speed.X, clouds.Speed.Y, clouds.Coverage, clouds.Margin));
        writeVector("_JA_MapRect", frame.MapRectangle);
    }

    void Write(string property, Vector3 components, float fourthComponent) => writeVector(property, new(components.X, components.Y, components.Z, fourthComponent));
}
