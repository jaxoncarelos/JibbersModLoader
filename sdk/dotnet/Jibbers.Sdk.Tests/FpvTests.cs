// The Rust FPV's tests (math, synth, flight, input, OSD), run against the C# port.

using Jibbers.Sdk.Features.Fpv;
using static Jibbers.Sdk.Features.Fpv.DroneMath;

static class FpvTests
{
    static bool Near(DroneVector a, DroneVector b) => (a - b).Length < 1e-4f;

    [Test]
    static void RotationsMatchUnityHandedness()
    {
        var h = MathF.PI / 2;
        Assert.True(Near(DroneRotation.FromPitch(h) * DroneVector.Forward, new DroneVector(0, -1, 0)), "+X pitches down");
        Assert.True(Near(DroneRotation.FromYaw(h) * DroneVector.Forward, DroneVector.Right), "+Y yaws right");
        Assert.True(Near(DroneRotation.FromRoll(h) * DroneVector.Right, DroneVector.Up), "+Z rolls left");
    }

    [Test]
    static void RotationVectorRoundTrips()
    {
        var v = new DroneVector(0.3f, -0.7f, 0.2f);
        Assert.True(Near(DroneRotation.AxisAngle(v, v.Length).RotationVector, v));
        Assert.True(Near(DroneRotation.Identity.RotateLocal(v).RotationVector, v));
    }

    [Test]
    static void SlerpEndpointsAndMidpoint()
    {
        var a = DroneRotation.FromYaw(0);
        var b = DroneRotation.FromYaw(1);
        Assert.True(Near(a.Slerp(b, 0).Forward, a.Forward));
        Assert.True(Near(a.Slerp(b, 1).Forward, b.Forward));
        Assert.True(Near(a.Slerp(b, 0.5f).Forward, DroneRotation.FromYaw(0.5f).Forward));
    }

    [Test]
    static void AngleDeltaWraps()
    {
        Assert.True(MathF.Abs(AngleDelta(3.1f, -3.1f) - (MathF.Tau - 6.2f)) < 1e-4f);
        Assert.True(MathF.Abs(AngleDelta(0.5f, 0.2f) + 0.3f) < 1e-5f);
    }

    [Test]
    static void SoundLoopsAreSeamlessAndInRange()
    {
        foreach (var (name, clip) in new[] { ("motor", DroneSounds.MotorLoop()), ("wind", DroneSounds.WindLoop()) })
        {
            Assert.Equal(DroneSounds.Rate, clip.Length);
            Assert.True(clip.All(x => float.IsFinite(x) && MathF.Abs(x) <= 1), name);
            var wrap = MathF.Abs(clip[0] - clip[^1]);
            var typical = clip.Zip(clip.Skip(1), (a, b) => MathF.Abs(b - a)).Max();
            Assert.True(wrap <= typical, $"{name}: wrap {wrap} vs max step {typical}");
        }
    }

    [Test]
    static void CrashDiesAway()
    {
        var c = DroneSounds.CrashClip();
        Assert.True(c.All(x => MathF.Abs(x) <= 1));
        Assert.True(c[^500..].Max(MathF.Abs) < 0.05f);
    }

    [Test]
    static void MotorsRiseWithPowerAndStopWhenDisarmed()
    {
        var (p0, v0) = DroneSounds.MotorVoice(0.1f, true);
        var (p1, v1) = DroneSounds.MotorVoice(0.9f, true);
        Assert.True(p1 > p0 && v1 > v0);
        Assert.Equal(0f, DroneSounds.MotorVoice(1, false).Volume);
        Assert.Equal(0f, DroneSounds.WindVoice(0).Volume);
    }

    /// <summary>Flat ground at y = 0, optionally with a wall at z = <see cref="Wall"/>.</summary>
    sealed class Ground : ICollisionWorld
    {
        public float? Wall;

        public CollisionHit? Sweep(DroneVector o, DroneVector d, float len, float r)
        {
            CollisionHit? best = null;
            if (d.Y < -1e-6f)
            {
                var t = (o.Y - r) / -d.Y;
                if (t >= 0 && t <= len)
                {
                    best = new CollisionHit(t, DroneVector.Up);
                }
            }
            if (Wall is { } w && d.Z > 1e-6f)
            {
                var t = (w - r - o.Z) / d.Z;
                if (t >= 0 && t <= len && (best is null || t < best.Value.Distance))
                {
                    best = new CollisionHit(t, new DroneVector(0, 0, -1));
                }
            }
            return best;
        }
    }

    static FlightStepResult Run(Drone d, float seconds, PilotControls ctl, FlightTarget? target, DroneFlightSettings cfg)
    {
        var world = new Ground();
        var last = new FlightStepResult();
        for (var i = 0; i < (int)(seconds * 60); i++)
        {
            last = d.Step(1f / 60, ctl, target, world, cfg);
            Assert.True(d.Position.IsFinite && d.Attitude.IsFinite);
            Assert.True(d.Position.Y >= cfg.Radius - 0.03f, $"never sinks into the ground: {d.Position}");
        }
        return last;
    }

    [Test]
    static void CentredStickIsHoverAndEndsAreIdleAndFull()
    {
        Assert.Equal(0.25f, Drone.CentreHover(0, 0.25f));
        Assert.Equal(1f, Drone.CentreHover(1, 0.25f));
        Assert.Equal(0f, Drone.CentreHover(-1, 0.25f));
    }

    [Test]
    static void RatesReachMaxAtFullStick()
    {
        var c = new DroneFlightSettings();
        Assert.True(MathF.Abs(Drone.Rate(1, c) - Radians(c.RateMax)) < 1e-4f);
        Assert.True(MathF.Abs(Drone.Rate(-1, c) + Radians(c.RateMax)) < 1e-4f);
        Assert.True(Drone.Rate(0.5f, c) < Drone.Rate(1, c) * 0.5f, "expo softens the centre");
    }

    [Test]
    static void AcroCentreThrottleHoversLevel()
    {
        var d = new Drone(new DroneVector(0, 20, 0), DroneRotation.Identity, FlightMode.Acro);
        Run(d, 4, default, null, new DroneFlightSettings());
        Assert.True(MathF.Abs(d.Position.Y - 20) < 0.5f, d.Position.ToString());
    }

    [Test]
    static void AcroPitchFlipsAndReturnsFinite()
    {
        var c = new DroneFlightSettings();
        var d = new Drone(new DroneVector(0, 50, 0), DroneRotation.Identity, FlightMode.Acro);
        Run(d, 0.3f, new PilotControls { Pitch = 1 }, null, c);
        Assert.True(d.Attitude.Forward.Y < -0.5f || d.Attitude.Up.Y < 0, "nose went over");
        Run(d, 1, default, null, c);
        Assert.True(d.AngularVelocity.Length < 0.2f, "stops rotating when the stick centres");
    }

    [Test]
    static void AngleModeLevelsAndHoldsAltitude()
    {
        var c = new DroneFlightSettings();
        var d = new Drone(new DroneVector(0, 30, 0), DroneRotation.Identity, FlightMode.Angle);
        Run(d, 1.5f, new PilotControls { Pitch = 1, Roll = 0.5f }, null, c);
        Assert.True(d.Velocity.Horizontal.Length > 5, "tilting moves the quad");
        Run(d, 6, default, null, c);
        Assert.True(d.Attitude.Up.Y > 0.98f, "self levels");
        Assert.True(MathF.Abs(d.Position.Y - 30) < 3, $"altitude held: {d.Position.Y}");
    }

    [Test]
    static void DivingIntoTheGroundBouncesAndCanCrash()
    {
        var c = new DroneFlightSettings();
        var d = new Drone(new DroneVector(0, 15, 0), DroneRotation.FromPitch(MathF.PI), FlightMode.Acro) { Attitude = DroneRotation.FromPitch(MathF.PI) };
        Assert.True(Run(d, 1.5f, new PilotControls { Throttle = 1 }, null, c).Ground is not null);
        c = c with { Crash = true };
        d = new Drone(new DroneVector(0, 15, 0), DroneRotation.Identity, FlightMode.Acro) { Attitude = DroneRotation.FromPitch(MathF.PI) };
        var world = new Ground();
        var crashed = false;
        for (var i = 0; i < 120; i++)
        {
            crashed |= d.Step(1f / 60, new PilotControls { Throttle = 1 }, null, world, c).Crashed;
        }
        Assert.True(crashed);
    }

    [Test]
    static void WallsStopFastFlight()
    {
        var c = new DroneFlightSettings();
        var d = new Drone(new DroneVector(0, 5, 0), DroneRotation.Identity, FlightMode.Cine);
        var world = new Ground { Wall = 30 };
        for (var i = 0; i < 600; i++)
        {
            d.Step(1f / 30, new PilotControls { Pitch = 1 }, null, world, c);
            Assert.True(d.Position.Z <= 30 - c.Radius + 0.01f);
        }
    }

    [Test]
    static void CineFollowArrivesLikeTheGameDrone()
    {
        var c = new DroneFlightSettings();
        var t = new FlightTarget(default, default);
        var d = new Drone(new DroneVector(0, 4, -6), DroneRotation.Identity, FlightMode.Cine);
        d.Lock(t.Position);
        d.Position = new DroneVector(30, 12, 20);
        var world = new Ground();
        for (var i = 0; i < 600; i++)
        {
            d.Step(1f / 60, default, t, world, c);
        }
        Assert.True((d.Position - new DroneVector(0, 4, -6)).Length < 0.3f, d.Position.ToString());
        Assert.True(d.CalculateCameraRotation(c, 0).Forward.Dot((t.Position + DroneVector.Up * 0.4f - d.Position).Normalized) > 0.99f);
    }

    [Test]
    static void CineKeepsUpWithAFastRider()
    {
        var c = new DroneFlightSettings { Chase = true };
        var d = new Drone(new DroneVector(0, 3, -6), DroneRotation.Identity, FlightMode.Cine);
        d.Lock(default(DroneVector));
        var world = new Ground();
        for (var i = 0; i < 900; i++)
        {
            var p = new DroneVector(0, 0, i * (20f / 60));
            d.Step(1f / 60, default, new FlightTarget(p, new DroneVector(0, 0, 20)), world, c);
            if (i > 240)
            {
                Assert.True((d.Position - p).Length < 12, $"falls behind: {d.Position} vs {p}");
                Assert.True(d.Position.Z < p.Z, "chases from behind");
            }
        }
    }

    [Test]
    static void ModeSwitchesKeepTheView()
    {
        var c = new DroneFlightSettings();
        var d = new Drone(new DroneVector(0, 10, 0), DroneRotation.FromYaw(1) * DroneRotation.FromPitch(0.2f), FlightMode.Acro);
        var before = d.CalculateCameraRotation(c, 0).Forward;
        d.SetMode(FlightMode.Cine, c);
        Assert.True(d.CalculateCameraRotation(c, 0).Forward.Dot(before) > 0.999f, "the gimbal takes over the view");
        // Leaving Cine levels the quad (a pitched gimbal must not fling it) but keeps the heading.
        d.SetMode(FlightMode.Angle, c);
        Assert.True(MathF.Abs(d.Heading - 1) < 0.01f);
        Assert.True(d.Attitude.Up.Y > 0.999f);
    }

    [Test]
    static void DeadzoneIsRadialAndRescaled()
    {
        foreach (var dz in new Func<float, float, float, (float X, float Y)>[] { PilotInput.Deadzone, Jibbers.Sdk.Gamepad.Deadzone })
        {
            Assert.Equal((0f, 0f), dz(0.05f, 0.05f, 0.1f));
            var (x, y) = dz(1, 0, 0.1f);
            Assert.True(MathF.Abs(x - 1) < 1e-6f && y == 0);
            Assert.True(MathF.Abs(dz(0.55f, 0, 0.1f).X - 0.5f) < 1e-6f);
        }
    }

    [Test]
    static void KeyboardAxesEaseInAndSnapBack()
    {
        var v = PilotInput.Ramp(0, 1, 0.1f);
        Assert.True(MathF.Abs(v - 0.5f) < 1e-6f);
        v = PilotInput.Ramp(v, 1, 0.2f);
        Assert.Equal(1f, v);
        v = PilotInput.Ramp(v, 0, 0.05f);
        Assert.True(MathF.Abs(v - 0.55f) < 1e-6f);
        Assert.Equal(-1f, PilotInput.Ramp(v, -1, 1));
    }

    [Test]
    static void OsdShowsModeThrottleSpeedAndLock()
    {
        var b = DroneTelemetryFormatter.Format(new DroneTelemetry(FlightMode.Acro, 0.62f, 15, 3.25f, 75, "you", false, false, false));
        Assert.True(b[0].Contains("ACRO") && b[0].Contains("[||||||....]") && b[0].Contains("62%"), b[0]);
        Assert.True(b[1].Contains("54 km/h") && b[1].Contains("3.2 m") && b[1].Contains("01:15"), b[1]);
        Assert.True(b[2].Contains("LOCK  you") && !b[2].Contains("F9"), "hint gone after a few seconds");
        Assert.True(b[3].Contains('+'));
    }

    [Test]
    static void OsdFlagsAndHint()
    {
        var b = DroneTelemetryFormatter.Format(new DroneTelemetry(FlightMode.Angle, 0, 0, null, 0, null, true, true, true));
        Assert.True(b[0].Contains("DISARMED") && b[0].Contains("[..........]"), b[0]);
        Assert.True(b[1].Contains("--- m"), b[1]);
        Assert.True(b[2].Contains("PAUSED") && b[2].Contains("L3+R3"), b[2]);
    }
}
