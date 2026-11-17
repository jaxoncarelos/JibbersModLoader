using Jibbers.Sdk;
using Snowcat;
using SnowShovel;

static class PartsTests
{
    static MeshData Model()
    {
        return PackedAssets.Model();
    }

    [Test]
    static void TheTillerAndBladeBendOffWithNoSeam()
    {
        var m = Model();
        var (withBlade, tiller, tw) = Parts.Bendable(m, Parts.TillerWeight);
        var (rest, blade, bw) = Parts.Bendable(withBlade, Parts.BladeWeight);
        Assert.Equal(m.Submeshes.Sum(s => s.Length), rest.Submeshes.Sum(s => s.Length) + tiller.Submeshes.Sum(s => s.Length) + blade.Submeshes.Sum(s => s.Length));
        Assert.True(tiller.Submeshes.Sum(s => s.Length) > 1000 && blade.Submeshes.Sum(s => s.Length) > 1000, "no tiller or blade");
        // The body never moves: no vertex of it follows either part at all.
        Assert.True(rest.Vertices.All(v => Parts.TillerWeight(v) == 0 && Parts.BladeWeight(v) == 0), "the body would bend");
        // Every point where a part meets the body stays put (weight 0), so nothing opens up there.
        var bodyPoints = rest.Vertices.ToHashSet();
        foreach (var (part, w) in new[] { (tiller, tw), (blade, bw) })
        {
            Assert.True(part.Uvs!.Length == part.Vertices.Length && part.Normals.Length == part.Vertices.Length && w.Length == part.Vertices.Length);
            for (var i = 0; i < part.Vertices.Length; i++)
                if (bodyPoints.Contains(part.Vertices[i])) Assert.True(w[i] == 0, $"shared point moves ({w[i]})");
            Assert.True(part.Submeshes.All(s => s.All(i => i >= 0 && i < part.Vertices.Length)));
        }
        // The tiller rides above the snow, so lowering it by the default 0.95 m puts its bottom on the ground.
        var bottom = tiller.Vertices.Where(v => v.Z < -3.3f).Min(v => v.Y);
        Console.WriteLine($"tiller bottom {bottom:0.00} m up, {tiller.Vertices.Length} vertices; blade {blade.Vertices.Length} vertices");
        Assert.True(bottom - 0.95f is > -0.2f and < 0.25f, $"tiller bottom {bottom} m up");
    }

    [Test]
    static void TheyMoveSmoothly()
    {
        // Eased: gentle at both ends, half way in the middle.
        Assert.True(Parts.Ease(0) == 0 && Parts.Ease(1) == 1 && MathF.Abs(Parts.Ease(0.5f) - 0.5f) < 1e-6f);
        Assert.True(Parts.Ease(0.05f) < 0.05f && Parts.Ease(0.95f) > 0.95f);
        var up = Parts.Shift(0, 0.5f, -0.2f);
        var down = Parts.Shift(1, 0.5f, -0.2f, 0.2f);
        Assert.True(MathF.Abs(up.Y - 0.5f) < 1e-6f && MathF.Abs(down.Y + 0.2f) < 1e-6f && MathF.Abs(down.Z + 0.2f) < 1e-6f);
        // The arms bend: a point half way along follows part of the way, the part all of it.
        Assert.True(Parts.TillerWeight(new Vector3(0, 0, -2.5f)) == 0 && Parts.TillerWeight(new Vector3(0, 0, -3.5f)) == 1);
        var mid = Parts.BladeWeight(new Vector3(0, 0, (Parts.BladeBendFrom + Parts.BladeBendTo) / 2));
        Assert.True(mid is > 0.4f and < 0.6f, $"{mid}");
        var bent = Parts.Bend(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0) }, new[] { 0f, 0.5f }, new Vector3(0, 2, 0));
        Assert.True(bent[0].Y == 0 && MathF.Abs(bent[1].Y - 1) < 1e-6f);
    }
}
