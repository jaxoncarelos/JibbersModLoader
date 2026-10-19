// The Rust mods' logic tests, run against the C# ports.

using Filmer;

static class FilmerTests
{
    [Test]
    static void RespawnResetsVelocity()
    {
        var d = new Director();
        var c = Config.Default;
        var f = new V(0, 0, 1);
        d.Observe(new V(0, 5000, 0), f, 0.016f);
        d.Plan(new V(0, 5000, 0), 0.016f, true, null, c);
        d.Observe(new V(0, 5000, 0.5f), f, 0.016f);
        Assert.True(d.Velocity.Len() > 0);
        Assert.True(d.Observe(new V(400, 8000, 0), f, 0.016f));
        Assert.Equal(default, d.Velocity);
    }

    [Test]
    static void CameraTracksFastRiderWithoutLag()
    {
        var d = new Director();
        var c = Config.Default;
        for (var i = 0; i < 600; i++)
        {
            var p = new V(0, 5000, i * 0.5f);
            d.Observe(p, new V(0, 0, 1), 1 / 60f);
            var s = d.Plan(p, 1 / 60f, true, null, c);
            Assert.True((s.Position - p).Len() < 12, $"frame {i}");
        }
    }

    [Test]
    static void JumpsWidenAndThenRecover()
    {
        var d = new Director();
        var p = new V(0, 0, 0);
        var c = Config.Default;
        d.Observe(p, new V(0, 0, 1), 0.016f);
        var baseShot = d.Plan(p, 0.016f, true, null, c);
        var shot = baseShot;
        for (var i = 0; i < 90; i++)
        {
            shot = d.Plan(p, 0.016f, false, new V(0, -5, 15), c);
        }
        Assert.True(shot.Fov > baseShot.Fov + 3);
        Assert.True((shot.Position - p).Len() > (baseShot.Position - p).Len());
        for (var i = 0; i < 250; i++)
        {
            shot = d.Plan(p, 0.016f, true, null, c);
        }
        Assert.True(MathF.Abs(shot.Fov - baseShot.Fov) < 0.1f);
    }

    [Test]
    static void HeadingWrapAndUturnStayFinite()
    {
        var d = new Director();
        var c = Config.Default;
        for (var i = 0; i < 500; i++)
        {
            var p = new V(0, 0, i < 250 ? i * 0.1f : (500 - i) * 0.1f);
            d.Observe(p, new V(0, 0, 1), 0.016f);
            var s = d.Plan(p, 0.016f, true, null, c);
            Assert.True(s.Position.Finite() && s.Aim.Finite());
        }
        Assert.True(MathF.Abs(Director.AngleDelta(3.13f, -3.13f) - 0.023185f) < 0.001f);
    }

    [Test]
    static void OrientationFacesTargetWithUnitQuaternion()
    {
        foreach (var dir in new[] { new V(0, 0, 1), new V(1, 0, 0), new V(0, 1, 0), new V(0, -1, 0), new V(0, 0, -1), new V(-1, 2, -3) })
        {
            var q = Director.LookRotation(dir);
            var (x, y, z, w) = (q.X, q.Y, q.Z, q.W);
            Assert.True(MathF.Abs(x * x + y * y + z * z + w * w - 1) < 0.001f);
            var facing = new V(2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y));
            Assert.True((facing - dir.Unit()).Len() < 0.001f, $"{dir}");
        }
    }
}
