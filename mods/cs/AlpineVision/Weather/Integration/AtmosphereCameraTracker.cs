using Jibbers.Sdk;
using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Map;

namespace JibbersAlpineVision.Weather;

internal sealed class AtmosphereCameraTracker : IDisposable
{
    GameRef? cameraTransform;
    int framesUntilCameraCheck;
    Vector3? previousCameraPosition;
    Vector3 cameraVelocity;
    public Vector3? ReadPosition()
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
    public Vector3 MeasureVelocity(Vector3? position)
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

    public void Dispose()
    {
        cameraTransform?.Dispose();
        cameraTransform = null;
    }
}
