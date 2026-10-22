// Vector and quaternion math in Unity's conventions: left-handed, Y up, Z forward, quaternions stored
// (x, y, z, w). Positive X rotation pitches the nose down, positive Y yaws right and positive Z rolls
// left, exactly as Quaternion.AngleAxis does in the game. Port of the Rust FPV's math.rs.
namespace Jibbers.Sdk.Features.Fpv;

internal static class DroneMath
{
    /// <summary>Frame-rate independent smoothing factor for time constant <paramref name = "timeConstant"/>.</summary>
    public static float Blend(float deltaTime, float timeConstant) => 1 - MathF.Exp(-deltaTime / MathF.Max(timeConstant, 1e-3f));
    public static float AngleDelta(float sourceAngle, float destinationAngle)
    {
        var x = (destinationAngle - sourceAngle + MathF.PI) % MathF.Tau;
        if (x < 0)
        {
            x += MathF.Tau;
        }

        return x - MathF.PI;
    }

    public static float Radians(float degrees) => degrees * (MathF.PI / 180);
}
