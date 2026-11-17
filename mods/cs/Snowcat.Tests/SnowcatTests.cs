using Jibbers.Sdk;
using Snowcat;

static class DriveTests
{
    static bool Near(float a, float b, float within = 0.02f) => MathF.Abs(a - b) < within;

    static CatState Run(Controls c, float seconds, float grade = 0, CatState? from = null)
    {
        var s = from ?? new CatState(new Vector3(0, 0, 0), 0, 0);
        for (var t = 0f; t < seconds; t += 0.02f) s = Drive.Step(s, c, 0.02f, grade);
        return s;
    }

    [Test]
    static void ItGathersSpeedToItsTopSpeedAndCoastsToAStop()
    {
        var fast = Run(new Controls(1, 0, false), 10);
        Assert.True(Near(fast.Speed, Drive.MaxForward), $"{fast.Speed}");
        Assert.True(fast.At.Z > 20 && Near(fast.At.X, 0, 1e-3f), $"{fast.At}");
        var stopped = Run(new Controls(0, 0, false), 10, from: fast);
        Assert.Equal(0f, stopped.Speed);
        var braked = Run(new Controls(0, 0, true), 1.1f, from: fast);
        Assert.Equal(0f, braked.Speed);
    }

    [Test]
    static void ItReversesSlowerThanItGoesForward()
    {
        var back = Run(new Controls(-1, 0, false), 10);
        Assert.True(Near(back.Speed, -Drive.MaxReverse) && back.At.Z < 0);
    }

    [Test]
    static void ItSlowsUphillAndWontClimbTooSteep()
    {
        Assert.True(Run(new Controls(1, 0, false), 10, grade: 0.45f).Speed < Drive.MaxForward * 0.6f);
        Assert.Equal(0f, Run(new Controls(1, 0, false), 10, grade: 1.2f).Speed);
        // Downhill is fine.
        Assert.True(Near(Run(new Controls(1, 0, false), 10, grade: -0.5f).Speed, Drive.MaxForward));
    }

    [Test]
    static void ItTurnsOnTheSpot()
    {
        var turned = Run(new Controls(0, 1, false), 1.8f);
        Assert.True(Near(turned.Yaw, 90, 2) && Near(turned.At.X, 0) && Near(turned.At.Z, 0), $"{turned}");
        var f = Drive.Forward(90);
        Assert.True(Near(f.X, 1) && Near(f.Z, 0));
        var r = Drive.Right(0);
        Assert.True(Near(r.X, 1) && Near(r.Z, 0));
    }

    [Test]
    static void ItSitsOnTheSlopeUnderItsTracks()
    {
        var (pitch, roll, height) = Drive.Sit(1, 1, 0, 0, 4, 2);
        Assert.True(Near(pitch, 14.04f, 0.1f) && Near(roll, 0) && Near(height, 0.5f));
        var (_, roll2, _) = Drive.Sit(0, 1, 0, 1, 4, 2);
        Assert.True(roll2 > 20);
        // Nose up raises its front; right side up raises its right.
        var q = Drive.Rotation(0, pitch, 0);
        Assert.True(Drive.Rotate(q, new Vector3(0, 0, 1)).Y > 0.2f);
        var q2 = Drive.Rotation(0, 0, roll2);
        Assert.True(Drive.Rotate(q2, new Vector3(1, 0, 0)).Y > 0.2f);
        // And turned: forward goes where the yaw says.
        var q3 = Drive.Rotation(90, 0, 0);
        Assert.True(Near(Drive.Rotate(q3, new Vector3(0, 0, 1)).X, 1, 1e-3f));
    }
}
