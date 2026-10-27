using System.Numerics;
using Rumble;

static class RumbleTests
{
    const float Dt = 1f / 60;
    static readonly RumbleOptions Plain = new();

    static RumbleFrame Riding(Vector3 hips, float clearance = 0, float upright = 1, bool onObject = false, bool slide = false) =>
        new(hips, clearance, upright, onObject, slide);

    /// <summary>Runs frames, returning every motor level.</summary>
    static List<Motors> Run(RumbleDirector director, Func<float, RumbleFrame> frameAt, float seconds, RumbleOptions? options = null)
    {
        var levels = new List<Motors>();
        for (var t = 0f; t < seconds; t += Dt)
            levels.Add(director.Update(frameAt(t), Dt, options ?? Plain));
        return levels;
    }

    /// <summary>Rides flat snow, then drops <paramref name="height"/> metres off a ledge at 10 m/s; returns the levels after takeoff.</summary>
    static List<Motors> Drop(RumbleDirector director, float height, RumbleOptions? options = null)
    {
        Run(director, t => Riding(new(0, height + 0.9f, 10 * t - 10)), 1, options);
        var fall = MathF.Sqrt(2 * height / 9.81f);
        return Run(director, t =>
        {
            var y = MathF.Max(0, height - 4.905f * t * t);
            return Riding(new(0, y + 0.9f, 10 * t), clearance: y);
        }, fall + 1, options);
    }

    [Test]
    static void HardLandingsThumpAndFade()
    {
        var director = new RumbleDirector();
        var levels = Drop(director, 4);
        Assert.True(director.LastImpact > 7, $"landed at {director.LastImpact:0.0} m/s");
        var peak = levels.Max(m => MathF.Max(m.Low, m.High));
        Assert.True(peak > 0.6f, $"a 4 m drop thumps at {peak:0.00}");
        var landed = levels.FindIndex(m => MathF.Max(m.Low, m.High) > 0.3f);
        var afterThird = levels[landed + 20];
        Assert.True(afterThird.Low < 0.05f && afterThird.High < 0.05f, $"and is gone a third of a second later: {afterThird}");
    }

    [Test]
    static void JumpingAgainCutsTheLandingShort()
    {
        // Land hard, then take off again at once: the air is silent straight away.
        var director = new RumbleDirector();
        Drop(director, 4);
        var landing = Run(director, t => Riding(new(0, 0.9f, 30 + 10 * t)), 0.05f);
        var jump = Run(director, t => Riding(new(0, 2.9f, 31 + 10 * t), clearance: 2), 0.5f);
        Assert.True(jump.Skip(6).All(m => m == Motors.Off), "silent from the moment you're airborne");
    }

    [Test]
    static void SmallHopsBarelyRegister()
    {
        var hop = Drop(new RumbleDirector(), 0.5f).Max(m => m.Low);
        var big = Drop(new RumbleDirector(), 4).Max(m => m.Low);
        Assert.True(hop < 0.15f && big > hop * 4, $"hop {hop:0.00} vs drop {big:0.00}");
    }

    [Test]
    static void TheAirIsSilent()
    {
        var director = new RumbleDirector();
        Run(director, t => Riding(new(0, 3.9f, 15 * t)), 1, new() { SnowTexture = true });
        var air = Run(director, t => Riding(new(0, 30, 15 + 15 * t), clearance: 30), 1, new() { SnowTexture = true });
        Assert.True(air.Skip(10).All(m => m == Motors.Off), "no vibration in the air, even with snow texture on");
    }

    [Test]
    static void SnowTextureIsFaintAndGrowsWithSpeed()
    {
        var withTexture = new RumbleOptions { SnowTexture = true };
        var slow = Run(new RumbleDirector(), t => Riding(new(0, 0.9f, 4 * t)), 2, withTexture).Skip(60).Average(m => m.High);
        var fast = Run(new RumbleDirector(), t => Riding(new(0, 0.9f, 25 * t)), 2, withTexture).Skip(60).Average(m => m.High);
        Assert.True(fast > slow * 1.5f && fast < 0.2f, $"slow {slow:0.000}, fast {fast:0.000}");
    }

    [Test]
    static void RidingNormallyIsStill()
    {
        // Straight, and gentle turns, at the defaults: no constant buzz on the ground.
        Vector3 Gentle(float t) => new(60 - 60 * MathF.Cos(12 * t / 60), 0.9f, 60 * MathF.Sin(12 * t / 60));
        Assert.True(Run(new RumbleDirector(), t => Riding(new(0, 0.9f, 15 * t)), 2).All(m => m == Motors.Off), "straight");
        Assert.True(Run(new RumbleDirector(), t => Riding(Gentle(t)), 2).All(m => m == Motors.Off), "a 0.25 g turn");
    }

    /// <summary>Carving at 15 m/s round a circle of <paramref name="radius"/> metres, turning right (or left); average levels.</summary>
    static Motors Carve(float radius, bool right)
    {
        var side = right ? 1 : -1;
        Vector3 At(float t) => new(side * (radius - radius * MathF.Cos(15 * t / radius)), 0.9f, radius * MathF.Sin(15 * t / radius));
        var levels = Run(new RumbleDirector(), t => Riding(At(t)), 2).Skip(60).ToArray();
        return new(levels.Average(m => m.Low), levels.Average(m => m.High));
    }

    [Test]
    static void HardCarvesVibrateTheSideYouTurnTo()
    {
        var hardLeft = Carve(15, right: false);
        var hardRight = Carve(15, right: true);
        Assert.True(hardLeft.Low > 0.3f && hardLeft.High < 0.01f, $"hard left (1.5 g): left {hardLeft.Low:0.00}, right {hardLeft.High:0.00}");
        Assert.True(hardRight.High > 0.3f && hardRight.Low < 0.01f, $"hard right (1.5 g): left {hardRight.Low:0.00}, right {hardRight.High:0.00}");
        var moderate = Carve(45, right: false);
        Assert.True(moderate.Low < 0.02f, $"a 0.5 g carve is still: {moderate.Low:0.00}");
    }

    [Test]
    static void RailsBuzzOnlyWhileSliding()
    {
        var director = new RumbleDirector();
        var slide = Run(director, t => Riding(new(0, 1.9f, 8 * t), onObject: true, slide: true), 1);
        Assert.Equal(RumbleState.Grinding, director.State);
        Assert.True(slide.Skip(10).All(m => m.High > 0.3f), "a steady buzz on the rail");
        // A kicker is an object too, but nothing is being slid.
        var kicker = Run(new RumbleDirector(), t => Riding(new(0, 1.9f, 8 * t), onObject: true), 1);
        Assert.True(kicker.All(m => m.High < 0.1f), "no buzz riding up a kicker");
    }

    [Test]
    static void CrashesJoltThenSettle()
    {
        var director = new RumbleDirector();
        Run(director, t => Riding(new(0, 0.9f, 10 * t)), 1);
        var crash = Run(director, t => Riding(new(0, 0.3f, 10 + 2 * t), upright: -0.5f), 2);
        Assert.Equal(RumbleState.Crashing, director.State);
        Assert.True(crash.Take(5).Max(m => m.Low) > 0.8f, "a jolt as you go down");
        Assert.True(crash[^1].Low < 0.15f, "settling once you've stopped");
    }

    [Test]
    static void StrengthScalesEverythingAndZeroIsOff()
    {
        var off = Drop(new RumbleDirector(), 4, Plain with { Strength = 0 });
        Assert.True(off.All(m => m == Motors.Off));
        var half = Drop(new RumbleDirector(), 4, Plain with { Strength = 0.5f }).Max(m => m.Low);
        var full = Drop(new RumbleDirector(), 4).Max(m => m.Low);
        Assert.True(MathF.Abs(half - full / 2) < 0.05f, $"half {half:0.00}, full {full:0.00}");
    }

    [Test]
    static void ARespawnIsNotALanding()
    {
        var director = new RumbleDirector();
        Run(director, t => Riding(new(0, 50, 10 * t), clearance: 50), 1);
        var after = Run(director, t => Riding(new(500, 0.9f, 0)), 0.5f);
        Assert.True(after.All(m => m.Low < 0.05f), "teleporting to the spawn point doesn't thump");
    }

    static readonly Vector3 SlopeNormal = Vector3.Normalize(new(0, MathF.Cos(MathF.PI / 6), MathF.Sin(MathF.PI / 6)));

    /// <summary>Riding straight down a 30 degree slope at <paramref name="speed"/> m/s.</summary>
    static RumbleFrame OnSlope(float t, float speed, float clearance = 0) =>
        Riding(new(0, 0.9f + clearance - speed * t * 0.5f, speed * t * 0.866f), clearance) with { GroundNormal = SlopeNormal };

    [Test]
    static void TheLandingSoundThumpsAtOnceScaledByTheHitIntoTheSlope()
    {
        // A big drop onto flat snow: the thump is on the frame the game plays its landing sound.
        var drop = new RumbleDirector();
        Run(drop, t => Riding(new(0, 4.9f, 10 * t - 10)), 1);
        Run(drop, t => Riding(new(0, MathF.Max(0.9f, 4.9f - 4.905f * t * t), 10 * t), clearance: MathF.Max(0, 4 - 4.905f * t * t)), 0.85f);
        drop.Hear(GameSound.Land, 1, Plain);
        var thump = drop.Update(Riding(new(0, 0.9f, 9)), Dt, Plain);
        Assert.True(thump.Low > 0.6f && thump.High > 0.6f, $"a 4 m drop thumps at once: {thump}");
        var after = Run(drop, t => Riding(new(0, 0.9f, 9 + 10 * t)), 0.35f);
        Assert.True(after[^1].Low < 0.05f && after[^1].High < 0.05f, $"and is gone a third of a second later: {after[^1]}");

        // Riding down a steep face at 20 m/s falls 10 m/s, but along the slope: a gentle landing.
        var slope = new RumbleDirector();
        Run(slope, t => OnSlope(t, 20), 1);
        slope.Hear(GameSound.Land, 1, Plain);
        var gentle = slope.Update(OnSlope(1, 20), Dt, Plain);
        Assert.True(gentle.Low > 0.05f && gentle.Low < 0.25f, $"a landing along the slope is a tick: {gentle}");
    }

    [Test]
    static void MeasuredLandingsOnASlopeAreGentle()
    {
        // Without the game's sounds: a hop while riding down a steep face doesn't hit like a drop.
        var director = new RumbleDirector();
        Run(director, t => OnSlope(t, 20), 1);
        var hop = Run(director, t => OnSlope(1 + t, 20, clearance: 0.6f * MathF.Sin(MathF.PI * t / 0.5f)), 0.5f);
        var landed = Run(director, t => OnSlope(1.5f + t, 20), 0.5f);
        Assert.True(hop.Concat(landed).All(m => m.Low < 0.15f), $"the hop lands at {director.LastImpact:0.0} m/s into the slope");
    }

    [Test]
    static void TheRailSoundBuzzesAndStopsWithIt()
    {
        var director = new RumbleDirector();
        director.Hear(GameSound.RailHit, 1, Plain);
        var clank = director.Update(Riding(new(0, 1.9f, 0), clearance: 1) with { RailSlide = 0.8f }, Dt, Plain);
        Assert.True(clank.High > 0.8f, $"a clank onto the rail: {clank}");
        // On a rail the feet are above the snow, but it's a grind, not the air.
        var slide = Run(director, t => Riding(new(0, 1.9f, 8 * t), clearance: 1) with { RailSlide = 0.8f }, 1);
        Assert.Equal(RumbleState.Grinding, director.State);
        Assert.True(slide.Skip(10).All(m => m.High > 0.4f && m.High < 0.75f), "a steady buzz while the rail sound plays");
        var off = Run(director, t => Riding(new(0, 1.9f, 8 + 8 * t)) with { RailSlide = 0 }, 0.1f);
        Assert.True(off.Skip(1).All(m => m == Motors.Off), "silent as soon as the rail sound stops");
        // A kicker is an object too, but no rail sound is playing.
        var kicker = Run(new RumbleDirector(), t => Riding(new(0, 1.9f, 8 * t), onObject: true, slide: true) with { RailSlide = 0 }, 1);
        Assert.True(kicker.All(m => m.High < 0.1f), "no buzz riding up a kicker");
    }

    [Test]
    static void EveryBodyHitInACrashIsFelt()
    {
        var director = new RumbleDirector();
        Run(director, t => Riding(new(0, 0.9f, 10 * t)), 1);
        var levels = new List<Motors>();
        for (var hit = 0; hit < 3; hit++)
        {
            director.Hear(hit == 1 ? GameSound.BodyHitSoft : GameSound.BodyHitHard, 1, Plain);
            levels.AddRange(Run(director, t => Riding(new(0, 0.3f, 10 + hit + 2 * t), upright: 0), 0.4f));
        }
        Assert.Equal(RumbleState.Crashing, director.State);
        Assert.True(levels[0].Low > 0.9f && levels[24].Low > 0.4f && levels[48].Low > 0.9f, "a hit for each impact");
        Assert.True(levels[20].Low < 0.15f, $"with stillness between them: {levels[20]}");
        var settled = Run(director, t => Riding(new(0, 0.3f, 13), upright: 0), 1.5f);
        Assert.True(settled[^1] == Motors.Off, "still once you've stopped");
    }

    [Test]
    static void WeakLevelsAreLiftedToWhereTheMotorsSpin()
    {
        Assert.Equal(Motors.Off, Motors.Off.Felt());
        var faint = new Motors(0.05f, 0.05f).Felt();
        Assert.True(faint.Low > 0.18f && faint.High > 0.1f, $"faint but felt: {faint}");
        Assert.Equal(new Motors(1, 1), new Motors(1, 1).Felt());
    }

    [Test]
    static void RecognisesTheGameSounds()
    {
        Assert.Equal(GameSound.Land, GameSounds.Classify("SkiLandSnow"));
        Assert.Equal(GameSound.LandPowder, GameSounds.Classify("SkiLandPowder"));
        Assert.Equal(GameSound.RailHit, GameSounds.Classify("SkiRailHit"));
        Assert.Equal(GameSound.Knockout, GameSounds.Classify("Knockout2"));
        Assert.Equal(GameSound.SkiOff, GameSounds.Classify("Ski_Binding_Detach"));
        foreach (var name in new[] { "UI_Click", "fanfare-46385", "SkiFast", "Airborne" })
            Assert.True(GameSounds.Classify(name) is null, name);
    }

    [Test]
    static void PlayStationReportsSetOnlyTheMotors()
    {
        var motors = new Motors(1, 0.5f);
        var dualSense = SonyPads.Report(true, 48, motors);
        Assert.True(dualSense.Length == 48 && dualSense[0] == 0x02 && dualSense[1] == 0x03 && dualSense[2] == 0, "DualSense: rumble emulation, lights untouched");
        Assert.True(dualSense[3] == 127 && dualSense[4] == 255, $"right (light) then left (heavy): {dualSense[3]}, {dualSense[4]}");
        var dualShock = SonyPads.Report(false, 32, motors);
        Assert.True(dualShock[0] == 0x05 && dualShock[1] == 0x01 && dualShock[4] == 127 && dualShock[5] == 255, "DualShock 4: motors only");
        Assert.True(dualShock.Skip(6).All(b => b == 0), "lightbar left alone");
    }

    [Test]
    static void RecognisesPlayStationPads()
    {
        foreach (var name in new[] { "Sony DualSense", "Sony DualShock 4", "PS4 Controller", "DualSense Edge" })
            Assert.True(ControllerMotors.IsPlayStation(name), name);
        foreach (var name in new[] { "Xbox 360 Controller", "Xbox One Controller", "Generic Gamepad" })
            Assert.True(!ControllerMotors.IsPlayStation(name), name);
    }

    [Test]
    static void RecognisesSlideTricks()
    {
        foreach (var (name, details) in new[] { ("Regular On - Slide - Front Misty 540", ""), ("50-50 Grind", ""), ("Boardslide", ""), ("Nose Press", "") })
            Assert.True(SlideTricks.IsSlide(name, details), name);
        foreach (var name in new[] { "Zero Spin", "Front Misty 540", "Backflip", "Cork 720" })
            Assert.True(!SlideTricks.IsSlide(name, ""), name);
    }
}
