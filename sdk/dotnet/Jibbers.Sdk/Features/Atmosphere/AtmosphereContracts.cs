using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Features.Atmosphere;

internal readonly record struct AtmosphereStatus(bool Spawned, float TerrainFloor, float CameraSpeedKmh, string? Error);
internal interface IAtmosphereSettings
{
    AtmosphereRenderSettings Capture();
}

internal interface IAtmosphereScene : IDisposable
{
    void Start(Action frame);
    AtmosphereFrame Capture();
}

internal interface IAtmospherePrefab : IDisposable
{
    bool Spawned { get; }

    void EnsureSpawned();
    void RequestRespawn();
}

internal interface IAtmosphereSurface : IDisposable
{
    void Apply(JsonObject settings, bool enabled);
    void Invalidate();
    void Restore();
}

internal interface IAtmosphereQuality : IDisposable
{
    void Apply(JsonObject settings);
    void Restore();
}

internal interface IAtmosphereGrading : IDisposable
{
    void Apply(JsonObject settings, bool enabled);
    void Restore();
}

internal interface IAtmosphereRenderer : IDisposable
{
    void Apply(AtmosphereRenderSettings settings, AtmosphereFrame frame);
    void Disable();
}
