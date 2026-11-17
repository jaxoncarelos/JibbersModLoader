using Jibbers.Sdk;
using SnowShovel;

namespace Snowcat;

/// <summary>
/// The moving parts of the one-piece model (the blade at the front, the tiller at the back), moved by
/// bending: the parts themselves move all the way, the arms that join them to the body follow part
/// way, smoothly, and the body doesn't move, so there's no seam or gap where they meet. No game calls.
/// </summary>
internal static class Parts
{
    /// <summary>The tiller's arm (metres along the model, -z is the back): fixed at the body end, moving fully at the tiller end.</summary>
    public const float TillerBendFrom = -2.85f, TillerBendTo = -3.15f;
    /// <summary>The blade's arms: fixed at the body end, moving fully at the blade.</summary>
    public const float BladeBendFrom = 2.45f, BladeBendTo = 3.15f;
    /// <summary>How far the tiller swings back as it goes down (metres, fully lowered).</summary>
    public const float TillerBack = 0.2f;

    /// <summary>How much of a part's movement a point of the model follows (0 the body, 1 the part, smooth between).</summary>
    public static float TillerWeight(Vector3 v) => Ramp(v.Z, TillerBendFrom, TillerBendTo);
    public static float BladeWeight(Vector3 v) => Ramp(v.Z, BladeBendFrom, BladeBendTo);

    static float Ramp(float z, float from, float to)
    {
        var t = Math.Clamp((z - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Eased: starts and stops gently (for raising and lowering).</summary>
    public static float Ease(float amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Where a part is moved to: <paramref name="amount"/> of the way (eased) from its raised height to its
    /// lowered height (metres up from where the model has it), swinging back <paramref name="back"/> as it lowers.
    /// </summary>
    public static Vector3 Shift(float amount, float raised, float lowered, float back = 0)
    {
        var e = Ease(amount);
        return new Vector3(0, raised + (lowered - raised) * e, -back * e);
    }

    /// <summary>
    /// Splits off the bit of a mesh that bends: every triangle with a corner that moves at all (so the
    /// rest only has corners that never move), with how much each of its vertices moves.
    /// </summary>
    public static (MeshData Body, MeshData Part, float[] Weights) Bendable(MeshData m, Func<Vector3, float> weight)
    {
        var (body, part) = Split(m, (a, b, c) => weight(a) > 0 || weight(b) > 0 || weight(c) > 0);
        return (body, part, part.Vertices.Select(weight).ToArray());
    }

    /// <summary>A bent part's vertices: each moved by its share of <paramref name="shift"/>.</summary>
    public static Vector3[] Bend(Vector3[] vertices, float[] weights, Vector3 shift) =>
        Bend(vertices, null, weights, default, new Pose(shift)).Vertices;

    /// <summary>
    /// Where a moving part goes: raised or lowered by <see cref="Shift"/>, swung round (<see cref="Angle"/>,
    /// degrees, + turns it to the right) and tilted (<see cref="Tilt"/>, degrees, + lifts its right end)
    /// about its pivot.
    /// </summary>
    public readonly record struct Pose(Vector3 Shift, float Angle = 0, float Tilt = 0);

    /// <summary>The blade swings and tilts about the middle of its back, where its arms meet it.</summary>
    public static readonly Vector3 BladePivot = new(0, 0.5f, 3.4f);

    /// <summary>
    /// A bent part's vertices (and normals) at <paramref name="pose"/>: each vertex goes its share
    /// (weight) of the way from where it is to where the posed part puts it, so the arms bend smoothly.
    /// </summary>
    public static (Vector3[] Vertices, Vector3[]? Normals) Bend(Vector3[] vertices, Vector3[]? normals, float[] weights, Vector3 pivot, Pose pose)
    {
        var turned = pose.Angle != 0 || pose.Tilt != 0;
        var q = Drive.Rotation(pose.Angle, 0, pose.Tilt);
        var moved = new Vector3[vertices.Length];
        var facing = normals is not null && turned ? new Vector3[normals.Length] : null;
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var target = turned ? pivot + Drive.Rotate(q, v - pivot) + pose.Shift : v + pose.Shift;
            moved[i] = v + (target - v) * weights[i];
            if (facing is not null && i < facing.Length)
            {
                var n = normals![i];
                var m = n + (Drive.Rotate(q, n) - n) * weights[i];
                var l = MathF.Sqrt(m.X * m.X + m.Y * m.Y + m.Z * m.Z);
                facing[i] = l > 1e-6f ? m * (1 / l) : n;
            }
        }
        return (moved, facing ?? normals);
    }

    /// <summary>
    /// Splits a mesh in two: the triangles <paramref name="inPart"/> says belong to the part (given their
    /// corners), and the rest. Each half keeps only the vertices it uses (with their normals and texture coordinates).
    /// </summary>
    public static (MeshData Body, MeshData Part) Split(MeshData m, Func<Vector3, Vector3, Vector3, bool> inPart)
    {
        var rest = new Builder(m);
        var part = new Builder(m);
        for (var s = 0; s < m.Submeshes.Length; s++)
        {
            var t = m.Submeshes[s];
            for (var i = 0; i + 2 < t.Length; i += 3)
                (inPart(m.Vertices[t[i]], m.Vertices[t[i + 1]], m.Vertices[t[i + 2]]) ? part : rest).Add(s, t[i], t[i + 1], t[i + 2]);
        }
        return (rest.Build(), part.Build());
    }

    sealed class Builder(MeshData source)
    {
        readonly Dictionary<int, int> map = new();
        readonly List<int>[] subs = source.Submeshes.Select(_ => new List<int>()).ToArray();

        public void Add(int sub, params int[] corners)
        {
            foreach (var c in corners)
            {
                if (!map.TryGetValue(c, out var n)) map[c] = n = map.Count;
                subs[sub].Add(n);
            }
        }

        public MeshData Build()
        {
            var order = map.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
            return new MeshData(
                order.Select(i => source.Vertices[i]).ToArray(),
                source.Normals.Length == source.Vertices.Length ? order.Select(i => source.Normals[i]).ToArray() : Array.Empty<Vector3>(),
                subs.Select(s => s.ToArray()).ToArray(),
                source.Materials,
                source.Uvs is { } uv ? order.Select(i => uv[i]).ToArray() : null);
        }
    }
}
