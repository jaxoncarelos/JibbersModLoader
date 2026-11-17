using Jibbers.Sdk;
using SnowShovel;

namespace Snowcat;

/// <summary>
/// A track's belt: a loop around the track's wheels (flat along the ground and over the top, round at
/// both ends) whose tread texture scrolls as the snowcat moves. No game calls.
/// </summary>
internal static class Belt
{
    /// <summary>The snowcat's tracks (in its own frame): their inner and outer side, back and front ends, bottom and top (metres).</summary>
    public const float Inner = 1.38f, Outer = 2.06f, Back = -2.65f, Front = 2.2f, Bottom = 0.0f, Top = 1.12f;
    /// <summary>How long one repeat of the tread pattern is along the belt (metres).</summary>
    public const float Repeat = 0.3f;

    /// <summary>
    /// The belt between x <paramref name="xMin"/> and <paramref name="xMax"/>: its outside surface,
    /// with outward normals and texture coordinates (u across the belt, v along it in pattern repeats).
    /// </summary>
    public static MeshData Loop(float xMin, float xMax, int arcSteps = 12)
    {
        var r = (Top - Bottom) / 2;
        var cy = Bottom + r;
        float zFront = Front - r, zBack = Back + r;
        // The path round the loop in (y, z) with its outward normal: along the bottom from the front
        // to the back, round the back, along the top to the front, round the front.
        var path = new List<(float Y, float Z, float NY, float NZ)>();
        path.Add((Bottom, zFront, -1, 0));
        path.Add((Bottom, zBack, -1, 0));
        for (var k = 1; k < arcSteps; k++)
        {
            var a = -MathF.PI / 2 - MathF.PI * k / arcSteps; // from straight down round the back to straight up
            path.Add((cy + r * MathF.Sin(a), zBack + r * MathF.Cos(a), MathF.Sin(a), MathF.Cos(a)));
        }
        path.Add((Top, zBack, 1, 0));
        path.Add((Top, zFront, 1, 0));
        for (var k = 1; k <= arcSteps; k++)
        {
            var a = MathF.PI / 2 - MathF.PI * k / arcSteps; // from straight up round the front to straight down
            path.Add((cy + r * MathF.Sin(a), zFront + r * MathF.Cos(a), MathF.Sin(a), MathF.Cos(a)));
        }
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var along = 0f;
        for (var k = 0; k < path.Count; k++)
        {
            if (k > 0)
            {
                var (y0, z0, _, _) = path[k - 1];
                var (y1, z1, _, _) = path[k];
                along += MathF.Sqrt((y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
            }
            var (y, z, ny, nz) = path[k];
            foreach (var (x, u) in new[] { (xMin, 0f), (xMax, 1f) })
            {
                verts.Add(new Vector3(x, y, z));
                normals.Add(new Vector3(0, ny, nz));
                uvs.Add(new Vector2(u, along / Repeat));
            }
        }
        var tris = new List<int>();
        for (var k = 0; k + 1 < path.Count; k++)
        {
            int a = 2 * k, b = a + 1, c = a + 2, d = a + 3;
            // Facing out: Unity draws a triangle's front where its cross product points.
            var outward = normals[a] + normals[c];
            var cross = Held.Cross(verts[c] - verts[a], verts[b] - verts[a]);
            if (Held.Dot(cross, outward) >= 0) tris.AddRange(new[] { a, c, b, b, c, d });
            else tris.AddRange(new[] { a, b, c, b, d, c });
        }
        return new MeshData(verts.ToArray(), normals.ToArray(), new[] { tris.ToArray() }, new[] { "tread" }, uvs.ToArray());
    }

    /// <summary>
    /// How fast each track runs (m/s along the belt) for the snowcat going <paramref name="speed"/>
    /// while turning at <paramref name="turnDegreesPerSecond"/> (clockwise from above): the outside
    /// track of a turn runs faster, and turning on the spot runs them opposite ways.
    /// </summary>
    public static (float Left, float Right) Speeds(float speed, float turnDegreesPerSecond)
    {
        var spin = turnDegreesPerSecond * MathF.PI / 180 * (Inner + Outer) / 2;
        return (speed + spin, speed - spin);
    }
}
