using Jibbers.Sdk;

namespace Snowcat;

/// <summary>What the driver's doing: throttle (-1 reverse to 1 forward), steering (-1 left to 1 right), and the brake.</summary>
internal readonly record struct Controls(float Throttle, float Steer, bool Brake);

/// <summary>Where the snowcat is (on the ground, under its middle), which way it faces (yaw, degrees) and how fast it's going (m/s, negative backwards).</summary>
internal readonly record struct CatState(Vector3 At, float Yaw, float Speed);

/// <summary>
/// How a snowcat drives: slow and heavy, up to about 25 km/h forward and 9 km/h back, slowing on
/// the way uphill and stopping when it's too steep; tracks turn it on the spot. No game calls.
/// </summary>
internal static class Drive
{
    public const float MaxForward = 7f, MaxReverse = 2.5f, Accel = 2.2f, Braking = 7f, Coast = 1.2f, TurnRate = 50f;
    /// <summary>The steepest climb (rise over run) it'll take.</summary>
    public const float MaxGrade = 0.9f;

    /// <summary>The snowcat <paramref name="dt"/> seconds on, with the ground rising <paramref name="grade"/> (rise over run) the way it faces.</summary>
    public static CatState Step(CatState s, Controls c, float dt, float grade)
    {
        var speed = s.Speed;
        var throttle = Math.Clamp(c.Throttle, -1, 1);
        if (c.Brake || (throttle != 0 && MathF.Sign(throttle) != MathF.Sign(speed) && MathF.Abs(speed) > 0.05f))
            speed = Toward(speed, 0, Braking * dt);
        else if (throttle != 0)
            speed += throttle * Accel * dt;
        else
            speed = Toward(speed, 0, Coast * dt);
        // Uphill it can't go as fast, and past its limit it doesn't go at all.
        var up = speed >= 0 ? grade : -grade;
        var limit = up >= MaxGrade ? 0 : Math.Clamp(1 - MathF.Max(up, 0) / MaxGrade, 0.15f, 1);
        speed = Math.Clamp(speed, -MaxReverse * limit, MaxForward * limit);
        // Tracks: it turns on the spot too, a little slower at speed; backwards it steers the other way round.
        var turn = Math.Clamp(c.Steer, -1, 1) * TurnRate * (1 - 0.4f * MathF.Abs(speed) / MaxForward) * dt * (speed < -0.05f ? -1 : 1);
        var yaw = s.Yaw + turn;
        var forward = Forward(yaw);
        return new CatState(s.At + forward * (speed * dt), yaw, speed);
    }

    static float Toward(float v, float target, float by) => v > target ? MathF.Max(v - by, target) : MathF.Min(v + by, target);

    /// <summary>Which way a yaw (degrees, as Unity's) faces along the ground.</summary>
    public static Vector3 Forward(float yaw)
    {
        var t = yaw * MathF.PI / 180;
        return new Vector3(MathF.Sin(t), 0, MathF.Cos(t));
    }

    /// <summary>To its right.</summary>
    public static Vector3 Right(float yaw) => new(Forward(yaw).Z, 0, -Forward(yaw).X);

    /// <summary>
    /// How it sits on the ground from the ground's height under its four corners: nose up (pitch,
    /// degrees), right side up (roll, degrees), and the height of its middle.
    /// </summary>
    public static (float Pitch, float Roll, float Height) Sit(float frontLeft, float frontRight, float backLeft, float backRight, float length, float width)
    {
        var pitch = MathF.Atan2((frontLeft + frontRight - backLeft - backRight) / 2, length) * 180 / MathF.PI;
        var roll = MathF.Atan2((frontRight + backRight - frontLeft - backLeft) / 2, width) * 180 / MathF.PI;
        return (pitch, roll, (frontLeft + frontRight + backLeft + backRight) / 4);
    }

    /// <summary>Unity's <c>Quaternion.Euler(-pitch, yaw, roll)</c>: rolled, then pitched nose up, then turned.</summary>
    public static Quaternion Rotation(float yaw, float pitch, float roll)
    {
        static Quaternion Axis(float x, float y, float z, float degrees)
        {
            var h = degrees * MathF.PI / 360;
            var s = MathF.Sin(h);
            return new Quaternion(x * s, y * s, z * s, MathF.Cos(h));
        }
        return Mul(Mul(Axis(0, 1, 0, yaw), Axis(1, 0, 0, -pitch)), Axis(0, 0, 1, roll));
    }

    public static Quaternion Mul(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>A point turned by a rotation.</summary>
    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        var t = Cross(u, v) * 2;
        return v + t * q.W + Cross(u, t);
    }
}
