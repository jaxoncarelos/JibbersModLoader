// Vector and quaternion math in Unity's conventions: left-handed, Y up, Z forward, quaternions stored
// (x, y, z, w). Positive X rotation pitches the nose down, positive Y yaws right and positive Z rolls
// left, exactly as Quaternion.AngleAxis does in the game. Port of the Rust FPV's math.rs.
namespace Jibbers.Sdk.Features.Fpv;

internal record struct DroneRotation(float X, float Y, float Z, float W)
{
    public static readonly DroneRotation Identity = new(0, 0, 0, 1);
    /// <summary><c>a * b</c> applies <c>b</c> first, in <c>a</c>'s local frame (Unity's operator).</summary>
    public static DroneRotation operator *(DroneRotation left, DroneRotation right) => new(left.W * right.X + left.X * right.W + left.Y * right.Z - left.Z * right.Y, left.W * right.Y + left.Y * right.W + left.Z * right.X - left.X * right.Z, left.W * right.Z + left.Z * right.W + left.X * right.Y - left.Y * right.X, left.W * right.W - left.X * right.X - left.Y * right.Y - left.Z * right.Z);
    public static DroneVector operator *(DroneRotation rotation, DroneVector vector)
    {
        var rotationAxis = new DroneVector(rotation.X, rotation.Y, rotation.Z);
        var crossProduct = rotationAxis.Cross(vector) * 2;
        return vector + crossProduct * rotation.W + rotationAxis.Cross(crossProduct);
    }

    public static DroneRotation AxisAngle(DroneVector axis, float radians)
    {
        var normalizedAxis = axis.Normalized;
        var(sine, cosine) = MathF.SinCos(radians * 0.5f);
        return new(normalizedAxis.X * sine, normalizedAxis.Y * sine, normalizedAxis.Z * sine, cosine);
    }

    public static DroneRotation FromYaw(float radians) => AxisAngle(DroneVector.Up, radians);
    /// <summary>Positive values pitch the nose down.</summary>
    public static DroneRotation FromPitch(float radians) => AxisAngle(DroneVector.Right, radians);
    /// <summary>Positive values roll left.</summary>
    public static DroneRotation FromRoll(float radians) => AxisAngle(DroneVector.Forward, radians);
    public readonly DroneRotation Conjugate => new(-X, -Y, -Z, W);

    public readonly DroneRotation Normalized
    {
        get
        {
            var length = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return length > 1e-6f && float.IsFinite(length) ? new(X / length, Y / length, Z / length, W / length) : Identity;
        }
    }

    public readonly bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W);
    public readonly DroneVector Forward => this * DroneVector.Forward;
    public readonly DroneVector Up => this * DroneVector.Up;

    /// <summary>Rotation vector (axis * angle, radians), taking the short way round.</summary>
    public readonly DroneVector RotationVector
    {
        get
        {
            var shortestRotation = W < 0 ? new DroneRotation(-X, -Y, -Z, -W) : this;
            var axis = new DroneVector(shortestRotation.X, shortestRotation.Y, shortestRotation.Z);
            var axisLength = axis.Length;
            return axisLength < 1e-6f ? axis * 2 : axis * (2 * MathF.Atan2(axisLength, shortestRotation.W) / axisLength);
        }
    }

    /// <summary>Applies a small body-frame rotation vector (radians), as a gyro integration step.</summary>
    public readonly DroneRotation RotateLocal(DroneVector rotation)
    {
        var angle = rotation.Length;
        return angle < 1e-7f ? this : (this * AxisAngle(rotation, angle)).Normalized;
    }

    public readonly DroneRotation Slerp(DroneRotation destination, float progress)
    {
        var dotProduct = X * destination.X + Y * destination.Y + Z * destination.Z + W * destination.W;
        if (dotProduct < 0)
        {
            destination = new(-destination.X, -destination.Y, -destination.Z, -destination.W);
            dotProduct = -dotProduct;
        }

        if (dotProduct > 0.9995f)
        {
            return new DroneRotation(X + (destination.X - X) * progress, Y + (destination.Y - Y) * progress, Z + (destination.Z - Z) * progress, W + (destination.W - W) * progress).Normalized;
        }

        var angle = MathF.Acos(Math.Clamp(dotProduct, -1, 1));
        var sine = MathF.Sin(angle);
        var sourceWeight = MathF.Sin((1 - progress) * angle) / sine;
        var destinationWeight = MathF.Sin(progress * angle) / sine;
        return new DroneRotation(X * sourceWeight + destination.X * destinationWeight, Y * sourceWeight + destination.Y * destinationWeight, Z * sourceWeight + destination.Z * destinationWeight, W * sourceWeight + destination.W * destinationWeight).Normalized;
    }

    /// <summary>The shortest rotation turning direction <paramref name = "source"/> onto <paramref name = "destination"/>.</summary>
    public static DroneRotation FromTo(DroneVector source, DroneVector destination)
    {
        (source, destination) = (source.Normalized, destination.Normalized);
        var axis = source.Cross(destination);
        var angle = MathF.Acos(Math.Clamp(source.Dot(destination), -1, 1));
        if (axis.Length < 1e-6f)
        {
            return angle < 1 ? Identity : AxisAngle(source.Cross(DroneVector.Right).NormalizeOr(DroneVector.Up), angle);
        }

        return AxisAngle(axis, angle);
    }

    public override readonly string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###}, {W:0.###})";
}
