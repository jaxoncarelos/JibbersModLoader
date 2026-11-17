using Jibbers.Sdk;

namespace SnowShovel;

/// <summary>What a piece of snow is: a lumpy clump (an ellipsoid) or a box (kicker parts).</summary>
internal enum Kind { Clump, Box }

/// <summary>
/// One piece of snow, in a frame of its own: its centre, its half-sizes (radii for a clump), and its
/// tilt up the slope (degrees about the right-hand axis; positive raises the far end).
/// </summary>
internal readonly record struct Piece(Kind Kind, Vector3 Centre, Vector3 Half, float Pitch);

/// <summary>A kicker's size: lip height and width (metres), takeoff angle (degrees).</summary>
internal readonly record struct KickerSize(float Height, float Width, float Angle)
{
    public static KickerSize Named(string name) => name switch
    {
        "Small" => new(0.7f, 2.2f, 24),
        "Big" => new(2.2f, 4f, 34),
        "Huge" => new(3.2f, 5f, 36),
        _ => new(1.4f, 3f, 30),
    };
}

/// <summary>The shapes snow is built from. No game calls, so it's tested on its own.</summary>
internal static class Shapes
{
    public const float MinClump = 0.25f, MaxClump = 2f;

    /// <summary>The next clump size up (+1) or down (-1): x1.2 a step, 0.25 to 2 m.</summary>
    public static float Resize(float radius, int step) =>
        Math.Clamp(step > 0 ? radius * 1.2f : radius / 1.2f, MinClump, MaxClump);

    /// <summary>A clump of radius <paramref name="r"/> on the ground at the origin: a low mound, most of it sunk in, so only its top shows.</summary>
    public static Piece Clump(float r) => new(Kind.Clump, new(0, -r * 0.2f, 0), new(r, r * 0.7f, r), 0);

    /// <summary>The lumps on a clump (position and size in the mound's own unit space, where it spans -0.5..0.5).</summary>
    public static readonly (Vector3 At, Vector3 Size)[] Lumps =
    {
        (new(0.18f, 0.28f, 0.1f), new(0.45f, 0.4f, 0.45f)),
        (new(-0.2f, 0.25f, -0.12f), new(0.4f, 0.36f, 0.42f)),
        (new(0.02f, 0.34f, -0.2f), new(0.34f, 0.3f, 0.34f)),
        (new(-0.05f, 0.3f, 0.22f), new(0.3f, 0.26f, 0.3f)),
    };

    /// <summary>
    /// A kicker starting at the origin and rising towards +z: the ramp's surface is a circular arc from
    /// flat to the takeoff angle, reaching the lip height, made of tilted slabs (so it curves like a
    /// built kicker), with snow filled in under it down below the ground, and a short flat deck before
    /// the lip edge drop. Local frame: x right, y up, z forward.
    /// </summary>
    public static List<Piece> Kicker(KickerSize size, int segments = 7, float slab = 0.4f)
    {
        var a = size.Angle * MathF.PI / 180;
        var r = size.Height / (1 - MathF.Cos(a)); // the transition's radius
        var pieces = new List<Piece>();
        var half = size.Width / 2;
        for (var i = 0; i < segments; i++)
        {
            float a0 = a * i / segments, a1 = a * (i + 1) / segments;
            // The arc rises from (z 0, y 0) with its centre at (0, r).
            Vector3 P(float t) => new(0, r - r * MathF.Cos(t), r * MathF.Sin(t));
            var (p0, p1) = (P(a0), P(a1));
            var mid = (a0 + a1) / 2;
            var chord = CamLength(p1 - p0);
            // The slab's top lies on the chord: its centre is half a slab below, along the surface's normal.
            var normal = new Vector3(0, MathF.Cos(mid), -MathF.Sin(mid));
            var centre = (p0 + p1) * 0.5f - normal * (slab / 2);
            pieces.Add(new(Kind.Box, centre, new(half, slab / 2, chord / 2 + 0.03f), mid * 180 / MathF.PI));
            // Snow under it, from below the ground up to the slab.
            var top = MathF.Min(p0.Y, p1.Y);
            const float below = 1;
            if (top > 0.05f)
                pieces.Add(new(Kind.Box, new(0, (top - below) / 2, (p0.Z + p1.Z) / 2), new(half, (top + below) / 2, chord / 2 + 0.02f), 0));
        }
        return pieces;
    }

    static float CamLength(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    /// <summary>The kicker's length along the ground, lip included.</summary>
    public static float KickerLength(KickerSize size)
    {
        var a = size.Angle * MathF.PI / 180;
        return size.Height / (1 - MathF.Cos(a)) * MathF.Sin(a);
    }

    /// <summary>
    /// Rotates a point by a turn about the vertical (yaw, degrees, clockwise seen from above as Unity's
    /// <c>Quaternion.Euler(0, yaw, 0)</c>).
    /// </summary>
    public static Vector3 Yaw(Vector3 v, float yaw)
    {
        var t = yaw * MathF.PI / 180;
        float c = MathF.Cos(t), s = MathF.Sin(t);
        return new(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    /// <summary>Unity's <c>Quaternion.Euler(-pitch, yaw, 0)</c>: tilted up the slope by <paramref name="pitch"/>, then turned by <paramref name="yaw"/>.</summary>
    public static Quaternion Rotation(float yaw, float pitch)
    {
        float y = yaw * MathF.PI / 360, p = -pitch * MathF.PI / 360;
        // Euler(x, y, 0) in Unity's order (z, then x, then y): q = qy * qx.
        float cy = MathF.Cos(y), sy = MathF.Sin(y), cx = MathF.Cos(p), sx = MathF.Sin(p);
        return new(cy * sx, sy * cx, -sy * sx, cy * cx);
    }
}

/// <summary>
/// Snow as terrain: how much a mound, a dig or a kicker raises the ground (metres) at a point, in a
/// frame of its own (x right, z forward from where it starts). Added to the terrain's own heights, so
/// slopes stay slopes. No game calls.
/// </summary>
internal static class Sculpt
{
    /// <summary>A mound of radius <paramref name="r"/>: <paramref name="height"/> in the middle, easing out to nothing at the edge.</summary>
    public static float Mound(float x, float z, float r, float height)
    {
        var d = MathF.Sqrt(x * x + z * z);
        if (d >= r) return 0;
        var c = MathF.Cos(MathF.PI / 2 * d / r);
        return height * c * c;
    }

    /// <summary>How tall a clump of radius <paramref name="r"/> stands.</summary>
    public static float ClumpHeight(float r) => r * 0.6f;

    /// <summary>
    /// A kicker: from z 0 the ground curves up (a circular transition) to the lip at the takeoff angle
    /// and height, then drops off the back at 40 degrees; full height across its width, easing off over
    /// 0.6 m at the sides.
    /// </summary>
    public static float Kicker(float x, float z, KickerSize k)
    {
        const float side = 0.6f, back = 40;
        var a = k.Angle * MathF.PI / 180;
        var r = k.Height / (1 - MathF.Cos(a));
        var length = r * MathF.Sin(a);
        float along;
        if (z <= 0) return 0;
        if (z <= length) along = r - MathF.Sqrt(MathF.Max(r * r - z * z, 0));
        else
        {
            var drop = (z - length) * MathF.Tan(back * MathF.PI / 180);
            along = k.Height - drop;
            if (along <= 0) return 0;
        }
        var across = MathF.Abs(x) - k.Width / 2;
        if (across <= 0) return along;
        if (across >= side) return 0;
        var e = MathF.Cos(MathF.PI / 2 * across / side);
        return along * e * e;
    }

    /// <summary>How much a rake stroke moves the snow at distance <paramref name="d"/> from its middle: fully out to half its radius, easing off to nothing at the edge.</summary>
    public static float RakeWeight(float d, float radius)
    {
        if (d >= radius) return 0;
        if (d <= radius / 2) return 1;
        var c = MathF.Cos(MathF.PI / 2 * (d - radius / 2) / (radius / 2));
        return c * c;
    }

    /// <summary>A box blur of a <paramref name="w"/> by <paramref name="h"/> grid (row-major), <paramref name="k"/> cells each way, edges clamped.</summary>
    public static float[] Blur(ReadOnlySpan<float> cells, int w, int h, int k)
    {
        var rows = new float[w * h];
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                float sum = 0;
                for (var d = -k; d <= k; d++) sum += cells[j * w + Math.Clamp(i + d, 0, w - 1)];
                rows[j * w + i] = sum / (2 * k + 1);
            }
        var result = new float[w * h];
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                float sum = 0;
                for (var d = -k; d <= k; d++) sum += rows[Math.Clamp(j + d, 0, h - 1) * w + i];
                result[j * w + i] = sum / (2 * k + 1);
            }
        return result;
    }

    /// <summary>How far a kicker reaches along its length, lip and back slope included.</summary>
    public static float KickerReach(KickerSize k) => Shapes.KickerLength(k) + k.Height / MathF.Tan(40 * MathF.PI / 180) + 0.1f;
}

/// <summary>
/// A kicker as the game's own terrain insert (what its object placer puts down, and every player sees):
/// a line from the base (on the ground) to the lip, the height between following a curve the game
/// stores upside down (1 at the base, 0 at the lip). No game calls.
/// </summary>
internal static class GameKicker
{
    /// <summary>The stored curve's keys: (time 0..1 along the kicker, value, slope) for a circular transition up to the takeoff angle.</summary>
    public static (float Time, float Value, float Slope)[] Keys(KickerSize k, int count = 6)
    {
        var a = k.Angle * MathF.PI / 180;
        var r = k.Height / (1 - MathF.Cos(a));
        var length = r * MathF.Sin(a);
        var keys = new (float, float, float)[count];
        for (var i = 0; i < count; i++)
        {
            var t = (float)i / (count - 1);
            var z = t * length;
            var root = MathF.Sqrt(MathF.Max(r * r - z * z, 1e-6f));
            var rise = (r - root) / k.Height;           // 0 at the base, 1 at the lip
            var slope = z / root * length / k.Height;   // d(rise)/dt
            keys[i] = (t, 1 - rise, -slope);
        }
        return keys;
    }
}
