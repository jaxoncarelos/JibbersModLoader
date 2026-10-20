// Vector and quaternion math in Unity's conventions: left-handed, Y up, Z forward, quaternions stored
// (x, y, z, w). Positive X rotation pitches the nose down, positive Y yaws right and positive Z rolls
// left, exactly as Quaternion.AngleAxis does in the game. Port of the Rust FPV's math.rs.

namespace Fpv;

public record struct V3(float X, float Y, float Z)
{
    public static readonly V3 Up = new(0, 1, 0);
    public static readonly V3 Forward = new(0, 0, 1);
    public static readonly V3 Right = new(1, 0, 0);

    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);

    public readonly float Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public readonly V3 Cross(V3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public readonly float Length => MathF.Sqrt(Dot(this));

    /// <summary>Unit vector, or <paramref name="fallback"/> when too short to have a direction.</summary>
    public readonly V3 UnitOr(V3 fallback)
    {
        var n = Length;
        return n > 1e-5f ? this * (1 / n) : fallback;
    }

    public readonly V3 Unit => UnitOr(Forward);
    public readonly bool Finite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    public readonly V3 Horizontal => new(X, 0, Z);

    public readonly V3 ClampLength(float max)
    {
        var n = Length;
        return n > max && n > 0 ? this * (max / n) : this;
    }

    /// <summary>Heading in radians about +Y, zero along +Z (<c>atan2(x, z)</c>).</summary>
    public readonly float Yaw => MathF.Atan2(X, Z);
}

public record struct Quat(float X, float Y, float Z, float W)
{
    public static readonly Quat Identity = new(0, 0, 0, 1);

    /// <summary><c>a * b</c> applies <c>b</c> first, in <c>a</c>'s local frame (Unity's operator).</summary>
    public static Quat operator *(Quat l, Quat r) => new(
        l.W * r.X + l.X * r.W + l.Y * r.Z - l.Z * r.Y,
        l.W * r.Y + l.Y * r.W + l.Z * r.X - l.X * r.Z,
        l.W * r.Z + l.Z * r.W + l.X * r.Y - l.Y * r.X,
        l.W * r.W - l.X * r.X - l.Y * r.Y - l.Z * r.Z);

    public static V3 operator *(Quat q, V3 v)
    {
        var u = new V3(q.X, q.Y, q.Z);
        var t = u.Cross(v) * 2;
        return v + t * q.W + u.Cross(t);
    }

    public static Quat AxisAngle(V3 axis, float radians)
    {
        var a = axis.Unit;
        var (s, c) = MathF.SinCos(radians * 0.5f);
        return new(a.X * s, a.Y * s, a.Z * s, c);
    }

    public static Quat FromYaw(float r) => AxisAngle(V3.Up, r);

    /// <summary>Positive values pitch the nose down.</summary>
    public static Quat FromPitch(float r) => AxisAngle(V3.Right, r);

    /// <summary>Positive values roll left.</summary>
    public static Quat FromRoll(float r) => AxisAngle(V3.Forward, r);

    public readonly Quat Conj => new(-X, -Y, -Z, W);

    public readonly Quat Normalized
    {
        get
        {
            var n = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return n > 1e-6f && float.IsFinite(n) ? new(X / n, Y / n, Z / n, W / n) : Identity;
        }
    }

    public readonly bool Finite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W);
    public readonly V3 ForwardDir => this * V3.Forward;
    public readonly V3 UpDir => this * V3.Up;

    /// <summary>Rotation vector (axis * angle, radians), taking the short way round.</summary>
    public readonly V3 RotationVector
    {
        get
        {
            var q = W < 0 ? new Quat(-X, -Y, -Z, -W) : this;
            var v = new V3(q.X, q.Y, q.Z);
            var s = v.Length;
            return s < 1e-6f ? v * 2 : v * (2 * MathF.Atan2(s, q.W) / s);
        }
    }

    /// <summary>Applies a small body-frame rotation vector (radians), as a gyro integration step.</summary>
    public readonly Quat RotateLocal(V3 rotation)
    {
        var angle = rotation.Length;
        return angle < 1e-7f ? this : (this * AxisAngle(rotation, angle)).Normalized;
    }

    public readonly Quat Slerp(Quat b, float t)
    {
        var d = X * b.X + Y * b.Y + Z * b.Z + W * b.W;
        if (d < 0)
        {
            b = new(-b.X, -b.Y, -b.Z, -b.W);
            d = -d;
        }
        if (d > 0.9995f)
        {
            return new Quat(X + (b.X - X) * t, Y + (b.Y - Y) * t, Z + (b.Z - Z) * t, W + (b.W - W) * t).Normalized;
        }
        var th = MathF.Acos(Math.Clamp(d, -1, 1));
        var s = MathF.Sin(th);
        var wa = MathF.Sin((1 - t) * th) / s;
        var wb = MathF.Sin(t * th) / s;
        return new Quat(X * wa + b.X * wb, Y * wa + b.Y * wb, Z * wa + b.Z * wb, W * wa + b.W * wb).Normalized;
    }

    /// <summary>The shortest rotation turning direction <paramref name="a"/> onto <paramref name="b"/>.</summary>
    public static Quat FromTo(V3 a, V3 b)
    {
        (a, b) = (a.Unit, b.Unit);
        var axis = a.Cross(b);
        var angle = MathF.Acos(Math.Clamp(a.Dot(b), -1, 1));
        if (axis.Length < 1e-6f)
        {
            return angle < 1 ? Identity : AxisAngle(a.Cross(V3.Right).UnitOr(V3.Up), angle);
        }
        return AxisAngle(axis, angle);
    }
}

public static class DroneMath
{
    /// <summary>Frame-rate independent smoothing factor for time constant <paramref name="tau"/>.</summary>
    public static float Blend(float dt, float tau) => 1 - MathF.Exp(-dt / MathF.Max(tau, 1e-3f));

    public static float AngleDelta(float from, float to)
    {
        var x = (to - from + MathF.PI) % MathF.Tau;
        if (x < 0)
        {
            x += MathF.Tau;
        }
        return x - MathF.PI;
    }

    public static float Radians(float degrees) => degrees * (MathF.PI / 180);
}
