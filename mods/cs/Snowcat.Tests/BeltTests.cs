using Jibbers.Sdk;
using Snowcat;
using SnowShovel;

static class BeltTests
{
    [Test]
    static void TheBeltWrapsTheTrackFacingOut()
    {
        var m = Belt.Loop(Belt.Inner, Belt.Outer);
        Assert.True(MathF.Abs(m.Vertices.Min(v => v.Y) - Belt.Bottom) < 0.01f && MathF.Abs(m.Vertices.Max(v => v.Y) - Belt.Top) < 0.01f);
        Assert.True(MathF.Abs(m.Vertices.Min(v => v.Z) - Belt.Back) < 0.01f && MathF.Abs(m.Vertices.Max(v => v.Z) - Belt.Front) < 0.01f);
        // Every triangle faces away from the belt's middle.
        var middle = new Vector3(0, (Belt.Top + Belt.Bottom) / 2, (Belt.Front + Belt.Back) / 2);
        var t = m.Submeshes[0];
        for (var i = 0; i < t.Length; i += 3)
        {
            var (a, b, c) = (m.Vertices[t[i]], m.Vertices[t[i + 1]], m.Vertices[t[i + 2]]);
            var centre = (a + b + c) * (1f / 3);
            var away = new Vector3(0, centre.Y - middle.Y, centre.Z - middle.Z);
            Assert.True(Held.Dot(Held.Cross(b - a, c - a), away) > 0, $"triangle {i / 3} faces in");
        }
        // The pattern runs along the belt, once every repeat.
        Assert.True(m.Uvs!.Max(u => u.Y) > 20 && m.Uvs!.All(u => u.X is 0 or 1));
    }

    [Test]
    static void TurningRunsTheTracksAtDifferentSpeeds()
    {
        Assert.True(Belt.Speeds(3, 0) == (3f, 3f));
        var (left, right) = Belt.Speeds(0, 50);
        Assert.True(left > 0 && right < 0 && MathF.Abs(left + right) < 1e-4f, $"{left} {right}");
        var (l2, r2) = Belt.Speeds(5, -20);
        Assert.True(r2 > l2, "turning left: the right track runs faster");
    }
}
