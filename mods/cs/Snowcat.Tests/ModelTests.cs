using Snowcat;
using SnowShovel;

static class SnowcatModelTests
{
    [Test]
    static void TheSnowcatStandsOnTheGroundFacingForwardWithItsTexture()
    {
        var m = PackedAssets.Model();
        Assert.True(m.Uvs is { } uv && uv.Length == m.Vertices.Length, "no texture coordinates");
        Assert.True(m.Uvs!.All(t => t.X is >= -0.01f and <= 1.01f && t.Y is >= -0.01f and <= 1.01f), "texture coordinates off the image");
        Assert.True(MathF.Abs(m.Vertices.Min(v => v.Y)) < 0.01f, "not on the ground");
        var length = m.Vertices.Max(v => v.Z) - m.Vertices.Min(v => v.Z);
        var width = m.Vertices.Max(v => v.X) - m.Vertices.Min(v => v.X);
        Assert.True(length is > 7 and < 10 && width is > 3.5f and < 5.5f, $"{length} long, {width} wide");
        Assert.True(m.Submeshes.Sum(s => s.Length) % 3 == 0 && m.Submeshes.All(s => s.All(i => i >= 0 && i < m.Vertices.Length)));
        // The blade (front, +z) is wider than the tracks; the cab stands tallest just behind it.
        float WidthAt(float z0, float z1) { var p = m.Vertices.Where(v => v.Z > z0 && v.Z < z1).ToList(); return p.Max(v => v.X) - p.Min(v => v.X); }
        Assert.True(WidthAt(3.5f, 5) > WidthAt(-1, 1), "blade not at the front");
        var tallest = m.Vertices.MaxBy(v => v.Y);
        Assert.True(tallest.Z > -0.5f && tallest.Z < 2.6f, $"cab at z {tallest.Z}");
    }
}

static class SnowcatUvTests
{
    [Test]
    static void TheModelsTextureCoordinatesVaryAcrossTheImage()
    {
        var m = PackedAssets.Model();
        var uv = m.Uvs!;
        var distinct = uv.Select(t => (MathF.Round(t.X, 3), MathF.Round(t.Y, 3))).Distinct().Count();
        Console.WriteLine($"vertices {m.Vertices.Length}, uvs {uv.Length}, distinct uvs {distinct}, normals {m.Normals.Length}, triangles {m.Submeshes.Sum(s => s.Length) / 3}");
        Assert.True(distinct > 1000, $"only {distinct} distinct texture coordinates");
    }
}
