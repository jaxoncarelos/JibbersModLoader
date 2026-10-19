// The Rust SkiSmooth's smoothing and transform tests, run against the C# port.

using SkiSmooth;

static class SkiSmoothTests
{
    static readonly Params P = new(1, 12, 0, true, true, 60);

    /// <summary>A flat strip (x = length, z = width, y = up), one half-width per station; each segment is its own part.</summary>
    static List<Part> SteppedSki(float[] stations)
    {
        const float thickness = 0.02f;
        var parts = new List<Part>();
        for (var i = 0; i + 1 < stations.Length; i++)
        {
            var (x0, x1) = ((float)i, i + 1f);
            var pos = new List<Vec>();
            foreach (var y in new[] { 0f, thickness })
            {
                foreach (var (x, z) in new[] { (x0, -stations[i]), (x1, -stations[i + 1]), (x1, stations[i + 1]), (x0, stations[i]) })
                {
                    pos.Add(new Vec(x, y, z));
                }
            }
            static uint[] Quad(uint a, uint b, uint c, uint d) => new[] { a, b, c, a, c, d };
            var tris = new List<uint>();
            tris.AddRange(Quad(4, 7, 6, 5));
            tris.AddRange(Quad(0, 1, 2, 3));
            tris.AddRange(Quad(0, 4, 5, 1));
            tris.AddRange(Quad(1, 5, 6, 2));
            tris.AddRange(Quad(2, 6, 7, 3));
            tris.AddRange(Quad(3, 7, 4, 0));
            parts.Add(new Part { Pos = pos, Normals = pos.Select(p => p.Y > 0.01f ? new Vec(0, 1, 0) : new Vec(0, -1, 0)).ToList(), Tris = tris });
        }
        return parts;
    }

    [Test]
    static void NoSmoothingReturnsTheSameShape()
    {
        var parts = SteppedSki(new[] { 0.1f, 0.2f, 0.1f });
        var outs = Smooth.Process(parts, P with { Strength = 0, Passes = 0 });
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.True(outs[i].Pos.SequenceEqual(parts[i].Pos));
            Assert.True(outs[i].Tris.SequenceEqual(parts[i].Tris));
        }
    }

    [Test]
    static void SmoothingEvensOutStepsAcrossSegments()
    {
        var stations = new[] { 0.1f, 0.3f, 0.1f, 0.3f, 0.1f, 0.3f };
        var outs = Smooth.Process(SteppedSki(stations), P);
        static float WidthAt(Out o, float x) => o.Pos.Where(p => MathF.Abs(p.X - x) < 1e-3f && p.Y > 0.01f).Select(p => MathF.Abs(p.Z)).DefaultIfEmpty(0).Max();
        for (var j = 1; j < stations.Length - 1; j++)
        {
            Assert.True(MathF.Abs(WidthAt(outs[j - 1], j) - WidthAt(outs[j], j)) < 1e-5f, $"seam tore open at {j}");
        }
        var inner = Enumerable.Range(1, stations.Length - 2).Select(j => WidthAt(outs[j], j)).ToList();
        Assert.True(inner.Max() - inner.Min() < 0.1f, $"zig-zag not reduced: {string.Join(",", inner)}");
    }

    [Test]
    static void ThicknessAndLengthAreKept()
    {
        var outs = Smooth.Process(SteppedSki(new[] { 0.1f, 0.3f, 0.1f, 0.3f }), P);
        var (mn, mx) = Smooth.Bounds(outs.SelectMany(o => o.Pos))!.Value;
        Assert.True(MathF.Abs(mx.Y - mn.Y - 0.02f) < 1e-6f, $"thickness changed: {mx.Y - mn.Y}");
        Assert.True(MathF.Abs(mx.X - mn.X - 3) < 1e-3f, $"length changed: {mx.X - mn.X}");
    }

    [Test]
    static void SubdivisionAddsVerticesAndKeepsTrianglesWhole()
    {
        var parts = SteppedSki(new[] { 0.1f, 0.2f });
        var outs = Smooth.Process(parts, P with { Subdivide = 1 });
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.True(outs[i].Pos.Count > parts[i].Pos.Count);
            Assert.Equal(parts[i].Tris.Count * 4, outs[i].Tris.Count);
            Assert.True(outs[i].Tris.All(t => t < outs[i].Pos.Count));
            Assert.Equal(outs[i].Pos.Count, outs[i].Normals.Count);
        }
    }

    [Test]
    static void SubdivisionStopsAtTheVertexCap()
    {
        var big = new Part { Pos = Enumerable.Repeat(new Vec(0, 0, 0), Smooth.MaxVerts / 2).ToList() };
        Assert.Equal(Smooth.MaxVerts / 2, Smooth.Process(new[] { big }, P with { Subdivide = 2 })[0].Pos.Count);
    }

    static void FacesPointTheRightWay(List<Out> outs)
    {
        foreach (var o in outs)
        {
            for (var i = 0; i < o.Pos.Count; i++)
            {
                if (MathF.Abs(o.Normals[i].Y) > 0.9f)
                {
                    Assert.Equal(o.Pos[i].Y > 0.01f, o.Normals[i].Y > 0);
                }
            }
        }
    }

    [Test]
    static void TopFacesPointUpAndBottomFacesDown() => FacesPointTheRightWay(Smooth.Process(SteppedSki(new[] { 0.1f, 0.2f }), P));

    [Test]
    static void FlippedWindingIsCorrectedFromTheGameNormals()
    {
        var parts = SteppedSki(new[] { 0.1f, 0.2f });
        foreach (var p in parts)
        {
            for (var t = 0; t + 2 < p.Tris.Count; t += 3)
            {
                (p.Tris[t + 1], p.Tris[t + 2]) = (p.Tris[t + 2], p.Tris[t + 1]);
            }
        }
        FacesPointTheRightWay(Smooth.Process(parts, P));
    }

    [Test]
    static void CreaseAngleControlsEdgeShading()
    {
        var parts = SteppedSki(new[] { 0.1f, 0.1f });
        var sharp = Smooth.Process(parts, P with { CreaseDeg = 10, Strength = 0, Passes = 0 });
        var soft = Smooth.Process(parts, P with { CreaseDeg = 120, Strength = 0, Passes = 0 });
        var corner = sharp[0].Pos.FindIndex(p => p.Y > 0.01f);
        Assert.True(sharp[0].Normals[corner].Y > 0.99f);
        Assert.True(soft[0].Normals[corner].Y < sharp[0].Normals[corner].Y - 1e-4f);
    }

    [Test]
    static void BadTrianglesAndNonFiniteVerticesDoNotThrow()
    {
        var part = new Part { Pos = new() { new(0, 0, 0), new(1, 0, 0), new(float.NaN, 0, 1) }, Tris = new() { 0, 1, 2, 0, 1, 99, 5 } };
        var o = Smooth.Process(new[] { part }, P)[0];
        Assert.Equal(3, o.Pos.Count);
        Assert.True(o.Pos.All(p => p.Finite()));
        Assert.True(o.Tris.SequenceEqual(new uint[] { 0, 1, 2 }));
    }

    [Test]
    static void EmptyInputIsFine()
    {
        Assert.Equal(0, Smooth.Process(Array.Empty<Part>(), P).Count);
        Assert.Equal(0, Smooth.Process(new[] { new Part() }, P)[0].Pos.Count);
    }

    static float[] Sample()
    {
        var m = Xform.Identity();
        (m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], m[12], m[13], m[14]) = (0, 0, -2, 0, 1, 0, 0.5f, 0, 0, 1, 2, 3);
        return m;
    }

    static bool Near(Vec a, Vec b) => (a - b).Len() < 1e-4f;

    [Test]
    static void InverseRoundTripsPoints()
    {
        var m = Sample();
        var inv = Xform.InverseAffine(m)!;
        var p = new Vec(0.3f, -4, 7.5f);
        Assert.True(Near(Xform.Point(inv, Xform.Point(m, p)), p));
        Assert.True(Near(Xform.Point(m, Xform.Point(inv, p)), p));
    }

    [Test]
    static void MultiplicationComposesInOrder()
    {
        var a = Sample();
        var b = Xform.Identity();
        b[12] = 5;
        var p = new Vec(1, 1, 1);
        Assert.True(Near(Xform.Point(Xform.Mul(a, b), p), Xform.Point(a, Xform.Point(b, p))));
    }

    [Test]
    static void NormalsStayPerpendicularUnderNonUniformScale()
    {
        var m = Xform.Identity();
        m[0] = 4;
        var n = Xform.Normal(m, new Vec(1, -1, 0));
        Assert.True(MathF.Abs(n.Dot(new Vec(4, 1, 0))) < 1e-4f);
    }

    [Test]
    static void SingularMatricesHaveNoInverse()
    {
        var m = Xform.Identity();
        m[5] = 0;
        Assert.True(Xform.InverseAffine(m) is null);
    }
}
