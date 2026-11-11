using Jibbers.Sdk;

namespace PhoneCamera;

/// <summary>The camera's maths: aiming, smoothing, turning and zooming. No game calls, so it's tested on its own.</summary>
internal static class CamMath
{
    public static float Length(Vector3 a) => MathF.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);

    public static Vector3 Normalize(Vector3 a)
    {
        var l = MathF.Max(Length(a), 1e-6f);
        return new(a.X / l, a.Y / l, a.Z / l);
    }

    public static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Unity's <c>Quaternion.LookRotation(forward, up = world up)</c>.</summary>
    public static Quaternion LookRotation(Vector3 forward)
    {
        var f = Normalize(forward);
        var r = Cross(new(0, 1, 0), f);
        if (Length(r) < 1e-4f)
            r = new(1, 0, 0);
        r = Normalize(r);
        var u = Cross(f, r);
        float m00 = r.X, m11 = u.Y, m22 = f.Z;
        var tr = m00 + m11 + m22;
        if (tr > 0)
        {
            var s = MathF.Sqrt(tr + 1) * 2;
            return new((u.Z - f.Y) / s, (f.X - r.Z) / s, (r.Y - u.X) / s, 0.25f * s);
        }
        if (m00 > m11 && m00 > m22)
        {
            var s = MathF.Sqrt(1 + m00 - m11 - m22) * 2;
            return new(0.25f * s, (u.X + r.Y) / s, (f.X + r.Z) / s, (u.Z - f.Y) / s);
        }
        if (m11 > m22)
        {
            var s = MathF.Sqrt(1 + m11 - m00 - m22) * 2;
            return new((u.X + r.Y) / s, 0.25f * s, (f.Y + u.Z) / s, (f.X - r.Z) / s);
        }
        var t = MathF.Sqrt(1 + m22 - m00 - m11) * 2;
        return new((f.X + r.Z) / t, (f.Y + u.Z) / t, 0.25f * t, (r.Y - u.X) / t);
    }

    /// <summary>Normalised lerp along the shorter way (smoothing).</summary>
    public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
    {
        var d = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (d < 0)
            b = new(-b.X, -b.Y, -b.Z, -b.W);
        var q = new Quaternion(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
        var l = MathF.Max(MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W), 1e-6f);
        return new(q.X / l, q.Y / l, q.Z / l, q.W / l);
    }

    /// <summary>Rotates <paramref name="v"/> by <paramref name="q"/>.</summary>
    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        var t = Cross(u, v) * 2;
        return v + t * q.W + Cross(u, t);
    }

    /// <summary><paramref name="dir"/> turned <paramref name="yaw"/> degrees right and <paramref name="pitch"/> up (kept short of straight up or down).</summary>
    public static Vector3 Turn(Vector3 dir, float yaw, float pitch)
    {
        var d = Normalize(dir);
        var y = MathF.Atan2(d.X, d.Z) + yaw * MathF.PI / 180;
        var p = Math.Clamp(MathF.Asin(Math.Clamp(d.Y, -1, 1)) + pitch * MathF.PI / 180, -1.45f, 1.45f);
        return new(MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p), MathF.Cos(p) * MathF.Cos(y));
    }

    /// <summary>A field of view (degrees) made <paramref name="by"/> times wider across, as a lens would (never reaching 180).</summary>
    public static float Widen(float fov, float by) => 2 * MathF.Atan(MathF.Tan(fov * MathF.PI / 360) * by) * 180 / MathF.PI;

    /// <summary>
    /// The field of view wanted: zoomed like a lens (0.1x is very wide), and when following a mate,
    /// about 3.5 m of the scene across at their distance. Upright (portrait) is widened so about as
    /// much fits across.
    /// </summary>
    public static float FieldOfView(bool selfie, float? mateDistance, float zoom, bool portrait)
    {
        float Lens(float f) => Widen(f, 1 / zoom);
        float fov;
        if (selfie)
            fov = Math.Clamp(Lens(65), 4, 170);
        else if (mateDistance is { } d)
            fov = Math.Clamp(2 * MathF.Atan(3.5f / zoom / 2 / MathF.Max(d, 0.5f)) * 180 / MathF.PI, 3, MathF.Min(Lens(75), 170));
        else
            fov = Math.Clamp(Lens(60), 4, 170);
        return portrait ? MathF.Min(Widen(fov, 1.4f), 170) : fov;
    }

    /// <summary>The next zoom step in (+1) or out (-1): x1.25 a step, 0.1x to 8x.</summary>
    public static float Zoom(float zoom, int step) => step > 0 ? MathF.Min(zoom * 1.25f, 8) : MathF.Max(zoom / 1.25f, 0.1f);

    /// <summary>The phone's screen size for the quality setting ("540p" .. "1440p"): 16:9 sideways, 9:16 upright.</summary>
    public static (int W, int H) FeedSize(string quality, bool portrait)
    {
        var lines = quality switch { "1440p" => 1440, "1080p" => 1080, "720p" => 720, _ => 540 };
        var wide = lines * 16 / 9;
        return portrait ? (lines, wide) : (wide, lines);
    }

    /// <summary>About 0.35 bits a pixel at 30 fps: ~5 Mbit/s at 540p up to ~39 at 1440p.</summary>
    public static int Bitrate(int w, int h, int fps) => (int)(w * (double)h * fps * 0.35);
}
