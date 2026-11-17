using System.Globalization;
using Jibbers.Sdk;

namespace SnowShovel;

/// <summary>A mesh ready for Unity: vertices and normals, and a triangle list per material.</summary>
internal sealed record MeshData(Vector3[] Vertices, Vector3[] Normals, int[][] Submeshes, string[] Materials, Vector2[]? Uvs = null);

/// <summary>Reads Wavefront OBJ (as Blender exports it) into Unity's frame. No game calls.</summary>
internal static class Obj
{
    /// <summary>
    /// Parses an OBJ: one submesh per <c>usemtl</c>, faces fanned into triangles. Blender's frame is
    /// right-handed, Unity's left-handed: x is mirrored and the winding turned round.
    /// </summary>
    public static MeshData Parse(TextReader reader)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var outV = new List<Vector3>();
        var outN = new List<Vector3>();
        var outT = new List<Vector2>();
        var corners = new Dictionary<(int, int, int), int>();
        var subs = new List<(string Name, List<int> Tris)>();
        List<int>? tris = null;
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        int Index(string token)
        {
            var parts = token.Split('/');
            var v = int.Parse(parts[0], CultureInfo.InvariantCulture);
            v = v < 0 ? positions.Count + v : v - 1;
            var n = -1;
            if (parts.Length > 2 && parts[2].Length > 0)
            {
                n = int.Parse(parts[2], CultureInfo.InvariantCulture);
                n = n < 0 ? normals.Count + n : n - 1;
            }
            var t = -1;
            if (parts.Length > 1 && parts[1].Length > 0)
            {
                t = int.Parse(parts[1], CultureInfo.InvariantCulture);
                t = t < 0 ? uvs.Count + t : t - 1;
                if (t < 0 || t >= uvs.Count) t = -1; // a file whose texture coordinates were left out
            }
            if (corners.TryGetValue((v, n, t), out var i)) return i;
            i = outV.Count;
            outV.Add(positions[v]);
            outN.Add(n >= 0 ? normals[n] : new Vector3(0, 1, 0));
            outT.Add(t >= 0 ? uvs[t] : new Vector2(0, 0));
            corners[(v, n, t)] = i;
            return i;
        }
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            switch (t[0])
            {
                case "v": positions.Add(new Vector3(-F(t[1]), F(t[2]), F(t[3]))); break;
                case "vn": normals.Add(new Vector3(-F(t[1]), F(t[2]), F(t[3]))); break;
                case "vt": uvs.Add(new Vector2(F(t[1]), t.Length > 2 ? F(t[2]) : 0)); break;
                case "usemtl":
                    var name = t.Length > 1 ? t[1] : "";
                    var found = subs.FindIndex(s => s.Name == name);
                    if (found < 0) { subs.Add((name, new List<int>())); found = subs.Count - 1; }
                    tris = subs[found].Tris;
                    break;
                case "f":
                    if (tris is null) { subs.Add(("", new List<int>())); tris = subs[^1].Tris; }
                    var first = Index(t[1]);
                    var prev = Index(t[2]);
                    for (var k = 3; k < t.Length; k++)
                    {
                        var next = Index(t[k]);
                        tris.Add(first); tris.Add(next); tris.Add(prev); // mirrored: reversed winding
                        prev = next;
                    }
                    break;
            }
        }
        return new MeshData(outV.ToArray(), outN.ToArray(), subs.Select(s => s.Tris.ToArray()).ToArray(), subs.Select(s => s.Name).ToArray(), uvs.Count > 0 ? outT.ToArray() : null);
    }
}

/// <summary>
/// A held tool's mesh put in a frame of its own: the grip (the top of the handle) at the origin, the
/// shaft running along +z to the blade, +y the blade's back. Then holding it is one rotation.
/// </summary>
internal sealed record Held(MeshData Mesh, float BladeZ, float Length)
{
    /// <summary>
    /// Reframes a shovel model standing in its own y-z plane: its grip is the top end (highest y), its
    /// blade the bottom end; <see cref="BladeZ"/> is the blade's middle along the shaft.
    /// </summary>
    public static Held Shovel(MeshData m) => Tool(m, gripAtTop: true, 1);

    /// <summary>A rake model standing on its handle's end, its head at the top, scaled to <paramref name="length"/> metres.</summary>
    public static Held Rake(MeshData m, float length = 1.7f)
    {
        var height = m.Vertices.Max(p => p.Y) - m.Vertices.Min(p => p.Y);
        return Tool(m, gripAtTop: false, height > 0 ? length / height : 1);
    }

    /// <summary>
    /// Reframes a long tool standing in its own y-z plane: its grip at one end (the top, or with
    /// <paramref name="gripAtTop"/> false the bottom), its working end (blade, head) at the other;
    /// <see cref="BladeZ"/> is the working end's middle along the shaft. Sizes times <paramref name="scale"/>.
    /// </summary>
    public static Held Tool(MeshData m, bool gripAtTop, float scale)
    {
        var v = m.Vertices.Select(p => p * scale).ToArray();
        float minY = v.Min(p => p.Y), maxY = v.Max(p => p.Y), band = (maxY - minY) * 0.08f;
        Vector3 Mean(IEnumerable<Vector3> ps)
        {
            var list = ps.ToList();
            var s = list.Aggregate(new Vector3(0, 0, 0), (a, b) => a + b);
            return s * (1f / list.Count);
        }
        var top = Mean(v.Where(p => p.Y > maxY - band));
        var bottom = Mean(v.Where(p => p.Y < minY + band));
        var (grip, blade) = gripAtTop ? (top, bottom) : (bottom, top);
        var shaft = blade - grip;
        var a = Normalise(new Vector3(0, shaft.Y, shaft.Z));
        var n = new Vector3(0, a.Z, -a.Y);
        if (n.Y < 0) n = n * -1;
        // Keep it a rotation, not a mirror: x stays x, so (x, n, a) must be right-handed like (x, y, z).
        var x = new Vector3(1, 0, 0);
        if (Dot(Cross(n, a), x) < 0) x = new Vector3(-1, 0, 0);
        Vector3 To(Vector3 p) { var d = p - grip; return new Vector3(Dot(d, x), Dot(d, n), Dot(d, a)); }
        Vector3 Turn(Vector3 p) => new(Dot(p, x), Dot(p, n), Dot(p, a));
        var verts = v.Select(To).ToArray();
        var norms = m.Normals.Select(Turn).ToArray();
        var subs = m.Submeshes;
        if (x.X < 0) subs = subs.Select(s => { var r = (int[])s.Clone(); for (var i = 0; i < r.Length; i += 3) (r[i + 1], r[i + 2]) = (r[i + 2], r[i + 1]); return r; }).ToArray();
        var bladeZ = To(blade).Z;
        return new Held(new MeshData(verts, norms, subs, m.Materials), bladeZ, verts.Max(p => p.Z));
    }

    public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vector3 Normalise(Vector3 v)
    {
        var l = MathF.Sqrt(Dot(v, v));
        return l < 1e-6f ? new Vector3(0, 0, 1) : v * (1 / l);
    }

    /// <summary>Unity's <c>Quaternion.LookRotation</c>: +z towards <paramref name="forward"/>, +y as near <paramref name="up"/> as it can.</summary>
    public static Quaternion Look(Vector3 forward, Vector3 up)
    {
        var f = Normalise(forward);
        var r = Cross(up, f);
        if (Dot(r, r) < 1e-8f) r = Cross(new Vector3(1, 0, 0), f);
        r = Normalise(r);
        var u = Cross(f, r);
        // Rows of the rotation matrix whose columns are r, u, f.
        float m00 = r.X, m01 = u.X, m02 = f.X, m10 = r.Y, m11 = u.Y, m12 = f.Y, m20 = r.Z, m21 = u.Z, m22 = f.Z;
        var trace = m00 + m11 + m22;
        if (trace > 0)
        {
            var s = MathF.Sqrt(trace + 1) * 2;
            return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, s / 4);
        }
        if (m00 > m11 && m00 > m22)
        {
            var s = MathF.Sqrt(1 + m00 - m11 - m22) * 2;
            return new Quaternion(s / 4, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        if (m11 > m22)
        {
            var s = MathF.Sqrt(1 + m11 - m00 - m22) * 2;
            return new Quaternion((m01 + m10) / s, s / 4, (m12 + m21) / s, (m02 - m20) / s);
        }
        var s2 = MathF.Sqrt(1 + m22 - m00 - m11) * 2;
        return new Quaternion((m02 + m20) / s2, (m12 + m21) / s2, s2 / 4, (m10 - m01) / s2);
    }
}
