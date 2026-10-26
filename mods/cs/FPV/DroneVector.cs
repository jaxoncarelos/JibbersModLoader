using Jibbers.Sdk;

// Vector and quaternion math in Unity's conventions: left-handed, Y up, Z forward, quaternions stored
// (x, y, z, w). Positive X rotation pitches the nose down, positive Y yaws right and positive Z rolls
// left, exactly as Quaternion.AngleAxis does in the game.
namespace Fpv;

internal record struct DroneVector(float X, float Y, float Z)
{
    public static readonly DroneVector Up = new(0, 1, 0);
    public static readonly DroneVector Forward = new(0, 0, 1);
    public static readonly DroneVector Right = new(1, 0, 0);
    public static DroneVector operator +(DroneVector left, DroneVector right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    public static DroneVector operator -(DroneVector left, DroneVector right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    public static DroneVector operator *(DroneVector vector, float scale) => new(vector.X * scale, vector.Y * scale, vector.Z * scale);
    public static DroneVector operator -(DroneVector left) => new(-left.X, -left.Y, -left.Z);
    public readonly float Dot(DroneVector other) => X * other.X + Y * other.Y + Z * other.Z;
    public readonly DroneVector Cross(DroneVector other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
    public readonly float Length => MathF.Sqrt(Dot(this));

    /// <summary>Unit vector, or <paramref name = "fallback"/> when too short to have a direction.</summary>
    public readonly DroneVector NormalizeOr(DroneVector fallback)
    {
        var length = Length;
        return length > 1e-5f ? this * (1 / length) : fallback;
    }

    public readonly DroneVector Normalized => NormalizeOr(Forward);
    public readonly bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    public readonly DroneVector Horizontal => new(X, 0, Z);

    public readonly DroneVector ClampLength(float maximum)
    {
        var length = Length;
        return length > maximum && length > 0 ? this * (maximum / length) : this;
    }

    /// <summary>Heading in radians about +Y, zero along +Z (<c>atan2(x, z)</c>).</summary>
    public readonly float Yaw => MathF.Atan2(X, Z);

    // Print components without recursively printing vector properties.
    public override readonly string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}
