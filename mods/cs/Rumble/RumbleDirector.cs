using System.Numerics;

// The game-free part of Rumble: what the rider is doing, and what the game is playing, becomes two
// motor levels. Unit tested.
namespace Rumble;

/// <summary>One frame of what the rider is doing, measured by the game adapter.</summary>
/// <param name="Center">The hips, for velocity.</param>
/// <param name="Clearance">Height of the lowest part of the body above whatever is under it, metres.</param>
/// <param name="Upright">How upright the spine is: 1 standing, 0 lying, below 0 upside down.</param>
/// <param name="OnObject">The feet are on something that isn't terrain (a rail, a box, a kicker).</param>
/// <param name="SlideTrick">The game is scoring a slide or grind right now.</param>
internal readonly record struct RumbleFrame(Vector3 Center, float Clearance, float Upright, bool OnObject, bool SlideTrick)
{
    /// <summary>The surface under the feet, for how hard a landing hits it.</summary>
    public Vector3 GroundNormal { get; init; } = Vector3.UnitY;
    /// <summary>How loud the game is playing its rail-slide loop (0..1), or null when it can't be read.</summary>
    public float? RailSlide { get; init; }
}

internal sealed record RumbleOptions
{
    public float Strength { get; init; } = 1;
    public bool Landings { get; init; } = true;
    public bool Crashes { get; init; } = true;
    public bool Rails { get; init; } = true;
    public bool Carving { get; init; } = true;
    public bool SnowTexture { get; init; }
}

/// <summary>The two motors, 0..1: the left (heavy, low-frequency) and the right (light, buzzing).</summary>
internal readonly record struct Motors(float Low, float High)
{
    public static readonly Motors Off = new(0, 0);

    /// <summary>
    /// What to send a pad so the level is felt: a motor doesn't spin at all below a fraction of full
    /// power (the heavy one most), so anything on starts there. Off stays off.
    /// </summary>
    public Motors Felt() => new(Lift(Low, 0.16f), Lift(High, 0.06f));

    static float Lift(float level, float floor) => level <= 0.005f ? 0 : floor + (1 - floor) * Math.Clamp(level, 0, 1);
}

internal enum RumbleState { Riding, Airborne, Grinding, Crashing }

/// <summary>The game's one-shot sounds that are felt.</summary>
internal enum GameSound { Takeoff, Land, LandPowder, RailHit, BodyHitSoft, BodyHitHard, Knockout, SkiOff, Break, Explosion }

/// <summary>The game's sound names (its AudioClips), and which are felt. Unit tested.</summary>
internal static class GameSounds
{
    public static GameSound? Classify(string name) => name.ToLowerInvariant() switch
    {
        "skijumpsnow" => GameSound.Takeoff,
        "skilandsnow" => GameSound.Land,
        "skilandpowder" => GameSound.LandPowder,
        "skirailhit" => GameSound.RailHit,
        "bodyhitsoft" => GameSound.BodyHitSoft,
        "bodyhithard" => GameSound.BodyHitHard,
        "knockout" or "knockout2" => GameSound.Knockout,
        "ski_binding_detach" => GameSound.SkiOff,
        "break" => GameSound.Break,
        "explosion" => GameSound.Explosion,
        _ => null,
    };
}

/// <summary>
/// Decides the vibration. The game's own sounds drive it, so it's felt on the same frame you hear it:
/// a thump on landing (scaled by how hard you hit the slope, deeper in powder), a clank onto a rail
/// and a buzz for as long as the rail sound plays, a hit for every body impact in a crash, a pop on
/// takeoff. Measured on top: the side you're turning to in a really hard carve, an optional snow
/// texture, and silence in the air. Riding normally is still. Until the game's sounds are heard
/// (they can't be hooked on some builds), landings and crashes are measured instead.
/// </summary>
internal sealed class RumbleDirector
{
    const float AirborneClearance = 0.45f, LandedClearance = 0.3f;
    /// <summary>Sideways g where carving starts to vibrate, and where it is at full strength.</summary>
    public const float HardCarveG = 0.6f, FullCarveG = 1.5f;

    /// <summary>A shaped pulse: full for <c>Hold</c> seconds, then fading with time constant <c>Decay</c>.</summary>
    record struct Envelope(float Low, float High, float Hold, float Decay, float Age)
    {
        public float Level => Age < Hold ? 1 : MathF.Exp(-(Age - Hold) / Decay);
        // Ends cleanly instead of trailing off: a faint tail is still felt once lifted past the dead zone.
        public bool Done => Age > Hold && Level < 0.06f;
    }

    readonly Random noise;
    readonly List<Envelope> envelopes = new();
    Vector3 lastCenter, velocity, lastTravel, airVelocity, groundNormal = Vector3.UnitY;
    bool hasLast, crashing, soundDriven;
    float airSeconds, clearanceAirSeconds, sinceAir = 10, sinceHit = 10, headingRate, jitter, uprightSeconds;

    public RumbleDirector(int seed = 1) => noise = new Random(seed);

    public RumbleState State { get; private set; }
    public Vector3 Velocity => velocity;
    /// <summary>The last landing's speed into the slope in m/s, for the log.</summary>
    public float LastImpact { get; private set; }
    /// <summary>Whether the game's sounds have been heard, so they drive landings and crashes.</summary>
    public bool SoundDriven => soundDriven;

    /// <summary>A sound the game just played; <paramref name="volume"/> is what it was played at.</summary>
    public void Hear(GameSound sound, float volume, RumbleOptions options)
    {
        soundDriven = true;
        var loud = 0.5f + 0.5f * Math.Clamp(float.IsFinite(volume) ? volume : 1, 0, 1);
        switch (sound)
        {
            case GameSound.Takeoff when options.Landings:
                Add(0.1f, 0.3f, 0.02f, 0.04f);
                break;
            case GameSound.Land or GameSound.LandPowder:
                // The sound is the touchdown: you're on the snow from this frame.
                var impact = Impact(sinceAir < 0.3f ? airVelocity : velocity);
                LastImpact = impact;
                clearanceAirSeconds = airSeconds = 0;
                sinceAir = 10;
                if (!options.Landings)
                    break;
                // Even a hop is a tick; a big drop is everything. Powder is deeper and softer.
                var s = Math.Clamp((impact - 1) / 7, 0.2f, 1);
                if (sound == GameSound.Land)
                    Add(0.85f * s, 0.9f * s, 0.03f, 0.05f + 0.05f * s);
                else
                    Add(0.9f * s, 0.35f * s, 0.04f, 0.1f + 0.1f * s);
                break;
            case GameSound.RailHit when options.Rails:
                Add(0.45f * loud, 0.9f * loud, 0.02f, 0.06f);
                break;
            case GameSound.BodyHitSoft when options.Crashes:
                sinceHit = 0;
                Add(0.45f * loud, 0.25f * loud, 0.02f, 0.07f);
                break;
            case GameSound.BodyHitHard when options.Crashes:
                sinceHit = 0;
                Add(0.95f * loud, 0.55f * loud, 0.04f, 0.12f);
                break;
            case GameSound.Knockout when options.Crashes:
                sinceHit = 0;
                Add(1, 0.9f, 0.08f, 0.3f);
                break;
            case GameSound.SkiOff when options.Crashes:
                Add(0.25f, 0.75f, 0.02f, 0.05f);
                break;
            case GameSound.Break when options.Crashes:
                Add(0.6f, 0.85f, 0.03f, 0.1f);
                break;
            case GameSound.Explosion when options.Crashes:
                Add(1, 1, 0.1f, 0.45f);
                break;
            case GameSound.BodyHitSoft or GameSound.BodyHitHard or GameSound.Knockout:
                sinceHit = 0;
                break;
        }
    }

    public Motors Update(RumbleFrame frame, float seconds, RumbleOptions options)
    {
        seconds = Math.Clamp(float.IsFinite(seconds) ? seconds : 0, 0, 0.1f);
        Measure(frame.Center, seconds);
        if (float.IsFinite(frame.GroundNormal.X + frame.GroundNormal.Y + frame.GroundNormal.Z) && frame.GroundNormal.Y > 0.2f)
            groundNormal = Vector3.Normalize(frame.GroundNormal);
        var travel = new Vector3(velocity.X, 0, velocity.Z);
        var speed = travel.Length();
        UpdateHeadingRate(travel, speed, seconds);
        sinceHit += seconds;
        sinceAir += seconds;

        // On a rail, in the air, on the snow, or crashing.
        var grinding = frame.RailSlide is { } rail ? rail > 0.05f : frame.OnObject && frame.SlideTrick;
        if (frame.Clearance > AirborneClearance)
            clearanceAirSeconds += seconds;
        else if (frame.Clearance < LandedClearance)
            clearanceAirSeconds = 0;
        var airborne = clearanceAirSeconds > 0.08f && !frame.OnObject && !grinding;
        if (airborne)
        {
            airSeconds += seconds;
            airVelocity = velocity;
            sinceAir = 0;
            // Leaving the snow cuts whatever is still fading (not the takeoff pop): the air is silent.
            envelopes.RemoveAll(e => e.Age > 0.12f);
        }
        else
        {
            if (airSeconds > 0.15f && !soundDriven)
            {
                LastImpact = Impact(airVelocity);
                var strength = Math.Clamp((LastImpact - 2.5f) / 6.5f, 0, 1);
                if (options.Landings && strength > 0)
                    Add(0.8f * strength, 0.9f * strength, 0.03f, 0.05f + 0.05f * strength);
            }
            airSeconds = 0;
        }
        uprightSeconds = frame.Upright > 0.6f ? uprightSeconds + seconds : 0;
        bool nowCrashing;
        if (soundDriven)
            nowCrashing = !airborne && !grinding && sinceHit < 1 && frame.Upright < 0.6f;
        else
        {
            nowCrashing = !airborne && (frame.Upright < 0.35f || (crashing && uprightSeconds < 0.5f));
            if (nowCrashing && !crashing && options.Crashes)
                Add(1, 0.7f, 0.04f, 0.12f);
        }
        crashing = nowCrashing;
        State = grinding ? RumbleState.Grinding : crashing ? RumbleState.Crashing : airborne ? RumbleState.Airborne : RumbleState.Riding;

        // Continuous feel for the current state, plus every pulse still fading.
        jitter += ((float)noise.NextDouble() * 2 - 1 - jitter) * Blend(seconds, 0.04f);
        var (low, high) = (0f, 0f);
        switch (State)
        {
            case RumbleState.Grinding when options.Rails:
                // Follows the game's rail sound: louder sliding, stronger buzz; it stops with the sound.
                var level = frame.RailSlide is { } loudness ? Math.Clamp(loudness, 0, 1) : 0.6f;
                low = 0.08f + 0.12f * level;
                high = 0.25f + 0.35f * level + 0.08f * jitter;
                break;
            case RumbleState.Crashing when options.Crashes && !soundDriven:
                low = 0.25f * Math.Clamp(speed / 10, 0, 1) * (1 + 0.4f * jitter);
                break;
            case RumbleState.Riding:
                if (options.SnowTexture && speed > 2)
                    high = (0.05f + 0.1f * Math.Clamp(speed / 25, 0, 1)) * (1 + 0.35f * jitter);
                if (options.Carving && sinceHit > 1)
                {
                    // Sideways g: speed times how fast the direction of travel is turning. Only hard
                    // carves count, felt on the side you're turning to: the left motor for a left carve.
                    var g = speed * MathF.Abs(headingRate) / 9.81f;
                    var carve = 0.6f * MathF.Pow(Math.Clamp((g - HardCarveG) / (FullCarveG - HardCarveG), 0, 1), 1.5f);
                    if (headingRate > 0)
                        high += carve;
                    else
                        low += carve;
                }
                break;
        }
        for (var i = 0; i < envelopes.Count; i++)
        {
            var e = envelopes[i];
            low = MathF.Max(low, e.Low * e.Level);
            high = MathF.Max(high, e.High * e.Level);
            envelopes[i] = e with { Age = e.Age + seconds };
        }
        envelopes.RemoveAll(e => e.Done);
        var strengthScale = Math.Clamp(float.IsFinite(options.Strength) ? options.Strength : 1, 0, 2);
        return new(Math.Clamp(low * strengthScale, 0, 1), Math.Clamp(high * strengthScale, 0, 1));
    }

    /// <summary>How fast you were moving into the slope: falling down a steep face along it is no impact.</summary>
    float Impact(Vector3 landingVelocity) => MathF.Max(0, -Vector3.Dot(landingVelocity, groundNormal));

    void Add(float low, float high, float hold, float decay)
    {
        if (low > 0.005f || high > 0.005f)
            envelopes.Add(new(low, high, hold, decay, 0));
    }

    void Measure(Vector3 center, float seconds)
    {
        if (!hasLast || !float.IsFinite(center.X + center.Y + center.Z))
        {
            lastCenter = center;
            hasLast = float.IsFinite(center.X + center.Y + center.Z);
            return;
        }
        if (seconds <= 1e-4f)
            return;
        var moved = center - lastCenter;
        lastCenter = center;
        // A respawn is not a movement.
        if (moved.Length() > 80 * seconds + 3)
        {
            velocity = airVelocity = Vector3.Zero;
            airSeconds = clearanceAirSeconds = 0;
            return;
        }
        velocity += (moved / seconds - velocity) * Blend(seconds, 0.06f);
    }

    void UpdateHeadingRate(Vector3 travel, float speed, float seconds)
    {
        if (speed < 3 || seconds <= 1e-4f || lastTravel == Vector3.Zero)
        {
            headingRate -= headingRate * Blend(seconds, 0.2f);
            lastTravel = speed < 3 ? Vector3.Zero : travel / speed;
            return;
        }
        var heading = travel / speed;
        var turned = MathF.Atan2(Vector3.Cross(lastTravel, heading).Y, Vector3.Dot(lastTravel, heading));
        headingRate += (Math.Clamp(turned / seconds, -3, 3) - headingRate) * Blend(seconds, 0.15f);
        lastTravel = heading;
    }

    static float Blend(float seconds, float timeConstant) => 1 - MathF.Exp(-Math.Max(seconds, 0) / timeConstant);

    public void Reset()
    {
        hasLast = false;
        velocity = lastTravel = airVelocity = Vector3.Zero;
        airSeconds = clearanceAirSeconds = headingRate = uprightSeconds = 0;
        sinceAir = sinceHit = 10;
        envelopes.Clear();
        crashing = false;
        State = RumbleState.Riding;
    }
}

/// <summary>Whether a trick's name says you're sliding or grinding something. Unit tested.</summary>
internal static class SlideTricks
{
    public static bool IsSlide(string name, string details)
    {
        var text = $"{name} {details}".ToLowerInvariant();
        return text.Contains("slide") || text.Contains("grind") || text.Contains("boardslide") || text.Contains("lipslide")
            || text.Contains("press") || text.Contains(" on -") || text.StartsWith("on -") || text.Contains("rail");
    }
}
