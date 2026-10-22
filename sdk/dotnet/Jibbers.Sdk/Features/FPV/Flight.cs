// The drone itself, without game objects: flight modes, physics and collision response. Port of the
// Rust FPV's flight.rs.
//
// Cine mode is the game's own Drone logic (Drone.LateUpdate): it seeks a target pose at
// min(distance / followTime, sqrt(2 * maxAcceleration * distance)), steers with an acceleration clamped
// to maxAcceleration, turns with 1 - exp(-turnSpeed * dt) and tilts the body towards its acceleration
// up to maxTilt. Acro and Angle are a thrust model on top.

using static Jibbers.Sdk.Features.Fpv.DroneMath;

namespace Jibbers.Sdk.Features.Fpv;

internal enum Mode
{
    Acro,
    Angle,
    Cine,
}

internal static class Modes
{
    public static readonly string[] Names = { "Acro", "Angle", "Cine" };

    public static Mode Next(this Mode m) => m switch { Mode.Acro => Mode.Angle, Mode.Angle => Mode.Cine, _ => Mode.Acro };

    public static Mode Parse(string s) => s switch { "Acro" => Mode.Acro, "Cine" => Mode.Cine, _ => Mode.Angle };
}

internal sealed record class FlightConfig
{
    public float ThrustToWeight = 4.5f;
    /// <summary>Level top speed at full throttle, m/s; sets the drag coefficient.</summary>
    public float TopSpeed = 38;
    /// <summary>Betaflight "actual" rates: centre sensitivity and max rate in deg/s, expo 0..1.</summary>
    public float RateCenter = 150, RateMax = 550, Expo = 0.54f;
    /// <summary>Master scale on every rotation the sticks command (rates, yaw, orbit, gimbal).</summary>
    public float Sensitivity = 0.6f;
    public float MaxAngle = 50;
    public bool AltitudeHold = true;
    public float ClimbSpeed = 8, CineSpeed = 12;
    /// <summary>The game drone's followTime, maxAcceleration and turnSpeed.</summary>
    public float FollowTime = 0.4f, MaxAcceleration = 25, TurnSpeed = 4;
    public bool FollowTarget = true, Chase;
    /// <summary>Camera uptilt in Acro and Angle, degrees.</summary>
    public float Uptilt = 25;
    public bool Collisions = true, Crash;
    public float Radius = 0.2f;
}

/// <summary>
/// One frame of pilot input. Sticks are -1..1: Pitch &gt; 0 is stick forward (nose down / move forward),
/// Roll and Yaw &gt; 0 are right, Climb &gt; 0 is up.
/// </summary>
internal record struct Controls
{
    /// <summary>Absolute throttle 0..1 when the pilot has one; otherwise Climb is a centred throttle whose middle is hover.</summary>
    public float? Throttle;
    public float Climb, Yaw, Pitch, Roll;
    /// <summary>Mouse look this frame, radians (positive: right / down).</summary>
    public float LookYaw, LookPitch;
    /// <summary>Gimbal tilt rate in Cine, -1..1 (positive: down).</summary>
    public float Gimbal;
    public bool Precision;
}

internal record struct Hit(float Distance, V3 Normal);

/// <summary>Collision queries against the map.</summary>
internal interface IWorld
{
    /// <summary>Sphere sweep (a ray when <paramref name="radius"/> is 0) along the unit <paramref name="direction"/>.</summary>
    Hit? Sweep(V3 origin, V3 direction, float length, float radius);
}

internal record struct Target(V3 Position, V3 Velocity);

internal record struct Report
{
    /// <summary>Fastest velocity into a surface this frame, m/s.</summary>
    public float Impact;
    public bool Crashed;
    public float? Ground;
    /// <summary>The state was not finite and has been reset; the caller should recall the drone.</summary>
    public bool Invalid;
}

internal sealed class Drone
{
    public const float G = 9.81f;
    /// <summary>Longest physics step; frames are split so fast flight cannot tunnel through thin geometry.</summary>
    const float Substep = 1f / 240;
    const float Skin = 0.02f;

    public Mode Mode;
    public V3 Position, Velocity;
    /// <summary>Body orientation (world from body).</summary>
    public Quat Attitude;
    /// <summary>Body angular velocity, rad/s (x pitch, y yaw, z roll).</summary>
    public V3 Rates;
    /// <summary>Current thrust acceleration, m/s² (the motors spin up and down, they don't jump).</summary>
    public float Thrust;
    /// <summary>Heading and gimbal pitch (positive down) in Angle/Cine, radians.</summary>
    public float Heading, Gimbal;
    /// <summary>Seconds left before the motors re-arm after a crash.</summary>
    public float Disarmed;
    /// <summary>Locked-target follow offset (drone minus target).</summary>
    public V3? Follow;
    float chaseAngle;
    Quat sway = Quat.Identity;
    float time;

    /// <summary>Maps a centred throttle stick so the middle hovers: -1 is motors idle, 1 is full power.</summary>
    public static float CentreHover(float climb, float hover)
    {
        var c = Math.Clamp(climb, -1, 1);
        return c >= 0 ? hover + (1 - hover) * c : hover * (1 + c);
    }

    /// <summary>Betaflight "actual" rates, in rad/s.</summary>
    public static float Rate(float stick, FlightConfig cfg)
    {
        var x = Math.Clamp(stick, -1, 1);
        var expo = Math.Clamp(cfg.Expo, 0, 1);
        var curved = MathF.Abs(x) * (MathF.Pow(x, 5) * expo + x * (1 - expo));
        var movement = MathF.Max(cfg.RateMax - cfg.RateCenter, 0);
        return Radians(x * cfg.RateCenter + movement * curved);
    }

    static float GimbalOf(V3 forward) => Math.Clamp(MathF.Asin(Math.Clamp(-forward.Y, -1, 1)), -1.2f, 1.4f);

    /// <summary>A drone hovering where the camera is, facing where it looks.</summary>
    public Drone(V3 position, Quat camera, Mode mode)
    {
        var f = camera.ForwardDir;
        Heading = f.Horizontal.UnitOr(camera.UpDir.Horizontal.Unit).Yaw;
        Mode = mode;
        Position = position;
        Attitude = Quat.FromYaw(Heading);
        Thrust = G;
        Gimbal = GimbalOf(f);
    }

    public void SetMode(Mode mode, FlightConfig cfg)
    {
        if (mode == Mode)
        {
            return;
        }
        var camera = Camera(cfg, 0);
        if (mode == Mode.Cine)
        {
            var f = camera.ForwardDir;
            Heading = f.Horizontal.UnitOr(Attitude.UpDir.Horizontal.Unit).Yaw;
            Gimbal = GimbalOf(f);
        }
        else
        {
            if (Mode == Mode.Cine)
            {
                Attitude = Quat.FromYaw(Heading);
            }
            Heading = Attitude.ForwardDir.Horizontal.UnitOr(-Attitude.UpDir.Horizontal.Unit).Yaw;
            Rates = default;
            Thrust = MathF.Max(Thrust, G);
        }
        Mode = mode;
    }

    /// <summary>Locks onto (or releases) a target; the current offset becomes the follow offset.</summary>
    public void Lock(V3? target)
    {
        Follow = target is { } t ? Offset(Position - t) : null;
        chaseAngle = 0;

        static V3 Offset(V3 o)
        {
            var h = o.Horizontal;
            return h.UnitOr(new V3(0, 0, -1)) * Math.Clamp(h.Length, 3, 40) + new V3(0, Math.Clamp(o.Y, 0.5f, 25), 0);
        }
    }

    /// <summary>Puts the drone behind and above <paramref name="target"/>, looking at it.</summary>
    public void Recall(V3 target, V3 forward)
    {
        var back = forward.Horizontal.UnitOr(V3.Forward);
        Position = target - back * 4 + V3.Up * 2;
        Velocity = default;
        Rates = default;
        Thrust = G;
        Disarmed = 0;
        var look = target + V3.Up * 0.5f - Position;
        Heading = look.Yaw;
        Gimbal = MathF.Asin(-look.Unit.Y);
        Attitude = Quat.FromYaw(Heading);
        if (Follow is not null)
        {
            Lock(target);
        }
    }

    /// <summary>Motor output as a fraction of full thrust (what an OSD shows as throttle).</summary>
    public float Power(FlightConfig cfg) => Math.Clamp(Thrust / (MathF.Max(cfg.ThrustToWeight, 1.1f) * G), 0, 1);

    /// <summary>Where the camera looks: uptilted from the body in Acro/Angle, a stabilised gimbal in Cine.</summary>
    public Quat Camera(FlightConfig cfg, float vibration)
    {
        var b = Mode == Mode.Cine ? sway * Quat.FromYaw(Heading) * Quat.FromPitch(Gimbal) : Attitude * Quat.FromPitch(-Radians(cfg.Uptilt));
        if (vibration <= 0 || Disarmed > 0)
        {
            return b;
        }
        // Prop buzz: a fraction of a degree, stronger with throttle.
        var t = time;
        var power = 0.35f + Math.Clamp(Thrust / (cfg.ThrustToWeight * G), 0, 1);
        var amp = Radians(0.12f * vibration * power);
        var jitter = new V3(MathF.Sin(t * 61.3f) + 0.5f * MathF.Sin(t * 143.7f), 0.4f * MathF.Sin(t * 89.1f), MathF.Sin(t * 73.9f) + 0.5f * MathF.Sin(t * 127.3f)) * amp;
        return b.RotateLocal(jitter);
    }

    public Report Step(float dt, in Controls ctl, Target? target, IWorld world, FlightConfig cfg)
    {
        var report = new Report();
        if (!float.IsFinite(dt)) return report;
        dt = Math.Clamp(dt, 0, 0.1f);
        if (dt <= 0)
        {
            return report;
        }
        time += dt;
        var s = ctl.Precision ? 0.5f : 1;
        report.Ground = world.Sweep(Position, -V3.Up, 6, 0)?.Distance;
        var groundEffect = report.Ground is { } gh ? 1 + 0.18f * Math.Clamp(1 - gh / 1.2f, 0, 1) : 1;

        // Mouse look acts once per frame.
        switch (Mode)
        {
            case Mode.Acro:
                Attitude = Attitude.RotateLocal(new V3(ctl.LookPitch, ctl.LookYaw, 0));
                break;
            case Mode.Angle:
                Heading += ctl.LookYaw;
                break;
            default:
                Heading += ctl.LookYaw;
                Gimbal = Math.Clamp(Gimbal + ctl.LookPitch, -1.2f, 1.4f);
                break;
        }

        // The follow goal is planned once per frame: keep it on the target's side of walls.
        V3? goal = null;
        if (Mode == Mode.Cine && target is { } tg && Follow is not null && cfg.FollowTarget)
        {
            SteerFollow(dt, ctl, s, tg, cfg);
            goal = ClearGoal(tg, world, cfg);
        }

        var steps = (int)Math.Clamp(MathF.Ceiling(dt / Substep), 1, 32);
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            if (Mode == Mode.Cine)
            {
                Cine(h, ctl, s, target, goal, report.Ground, cfg);
            }
            else
            {
                Fly(h, ctl, s, groundEffect, cfg);
            }
            report.Impact = MathF.Max(report.Impact, Travel(h, world, cfg));
            Disarmed = MathF.Max(Disarmed - h, 0);
        }
        if (cfg.Crash && Mode != Mode.Cine && report.Impact > 9 && Disarmed <= 0)
        {
            // Hard hit: the motors cut out and the quad tumbles until they re-arm.
            Disarmed = 1.2f;
            Rates += new V3(report.Impact * 0.5f, report.Impact * 0.15f, report.Impact * 0.35f);
            report.Crashed = true;
        }
        if (!(Position.Finite && Velocity.Finite && Attitude.Finite && Rates.Finite))
        {
            Velocity = default;
            Rates = default;
            Attitude = Quat.Identity;
            report.Invalid = true;
        }
        return report;
    }

    /// <summary>Acro and Angle: rate or self-levelling attitude control, thrust along body up, drag.</summary>
    void Fly(float h, in Controls ctl, float s, float groundEffect, FlightConfig cfg)
    {
        var maxThrust = MathF.Max(cfg.ThrustToWeight, 1.1f) * G;
        var k = Math.Clamp(cfg.Sensitivity, 0.05f, 2);
        V3 wantedRates;
        if (Mode == Mode.Acro)
        {
            wantedRates = new V3(Rate(ctl.Pitch * s, cfg), Rate(ctl.Yaw * s, cfg), -Rate(ctl.Roll * s, cfg)) * k;
        }
        else
        {
            Heading += Math.Clamp(Rate(ctl.Yaw * s, cfg) * 0.5f * k, -3, 3) * h;
            var tilt = Radians(Math.Clamp(cfg.MaxAngle, 5, 85));
            var level = Quat.FromYaw(Heading) * Quat.FromPitch(ctl.Pitch * s * tilt) * Quat.FromRoll(-ctl.Roll * s * tilt);
            wantedRates = ((Attitude.Conj * level).RotationVector * 10).ClampLength(14);
        }
        // The flight controller follows the sticks within a few tens of milliseconds.
        if (Disarmed <= 0)
        {
            Rates += (wantedRates - Rates) * Blend(h, 0.03f);
        }
        Attitude = Attitude.RotateLocal(Rates * h);

        var hover = G / maxThrust;
        float wanted;
        if (Mode == Mode.Angle && cfg.AltitudeHold && ctl.Throttle is null)
        {
            // Centred stick holds altitude; deflection commands a climb rate.
            var climb = ctl.Climb * s * cfg.ClimbSpeed;
            wanted = (G + (climb - Velocity.Y) * 3.5f) / MathF.Max(Attitude.UpDir.Y, 0.3f);
        }
        else
        {
            wanted = (ctl.Throttle ?? CentreHover(ctl.Climb, hover)) * maxThrust;
        }
        wanted = Disarmed > 0 ? 0 : Math.Clamp(wanted, 0, maxThrust);
        Thrust += (wanted - Thrust) * Blend(h, 0.035f);

        var quad = maxThrust * 0.95f / MathF.Pow(MathF.Max(cfg.TopSpeed, 5), 2);
        var v = Velocity;
        var acc = Attitude.UpDir * (Thrust * groundEffect) - V3.Up * G - v * 0.05f - v * (v.Length * quad);
        Velocity += acc * h;
    }

    /// <summary>Cine: the game drone's pursuit towards a velocity or follow goal, with a stabilised gimbal.</summary>
    void Cine(float h, in Controls ctl, float s, Target? target, V3? goal, float? ground, FlightConfig cfg)
    {
        var followTime = MathF.Max(cfg.FollowTime, 0.05f);
        V3 wanted;
        if (goal is { } gl && target is { } tg)
        {
            var diff = gl - Position;
            var distance = diff.Length;
            var speed = MathF.Min(distance / followTime, MathF.Sqrt(2 * cfg.MaxAcceleration * distance));
            wanted = diff.UnitOr(default) * speed + tg.Velocity;
        }
        else
        {
            Heading += ctl.Yaw * s * 2 * cfg.Sensitivity * h;
            var forward = new V3(MathF.Sin(Heading), 0, MathF.Cos(Heading));
            var right = new V3(forward.Z, 0, -forward.X);
            wanted = (forward * ctl.Pitch + right * ctl.Roll).ClampLength(1) * (cfg.CineSpeed * s) + V3.Up * (ctl.Climb * s * cfg.ClimbSpeed);
        }
        if (ground is < 0.8f)
        {
            wanted.Y = MathF.Max(wanted.Y, 0);
        }
        Gimbal = Math.Clamp(Gimbal + ctl.Gimbal * s * 1.4f * cfg.Sensitivity * h, -1.2f, 1.4f);
        var accel = ((wanted - Velocity) * (1 / followTime)).ClampLength(cfg.MaxAcceleration);
        Velocity += accel * h;

        if (target is { } t)
        {
            // Keep the rider framed (chest height), turning like the game drone.
            var look = t.Position + V3.Up * 0.4f - Position;
            if (look.Length > 0.5f)
            {
                var k = Blend(h, 1 / MathF.Max(cfg.TurnSpeed, 0.5f));
                Heading += AngleDelta(Heading, look.Yaw) * k;
                var pitch = Math.Clamp(MathF.Asin(-look.Unit.Y), -1.2f, 1.4f);
                Gimbal += (pitch - Gimbal) * k;
            }
        }
        // The game's tilt from acceleration, felt through the gimbal as a little sway.
        var tilt = Quat.FromTo(V3.Up, V3.Up + accel * 0.035f);
        var limited = Quat.Identity.Slerp(tilt, MathF.Min(Radians(35) / MathF.Max(tilt.RotationVector.Length, 1e-4f), 1));
        Attitude = limited * Quat.FromYaw(Heading);
        sway = sway.Slerp(Quat.Identity.Slerp(limited, 0.2f), Blend(h, 0.15f));
        Thrust = G;
    }

    void SteerFollow(float dt, in Controls ctl, float s, Target t, FlightConfig cfg)
    {
        if (Follow is not { } o)
        {
            return;
        }
        var flat = o.Horizontal;
        var distance = Math.Clamp(flat.Length * (1 - ctl.Pitch * s * 0.9f * dt), 2, 60);
        var height = Math.Clamp(o.Y + ctl.Climb * s * cfg.ClimbSpeed * dt, -3, 40);
        var orbit = -ctl.Roll * s * 1.5f * cfg.Sensitivity * dt;
        var direction = Quat.FromYaw(orbit) * flat.UnitOr(new V3(0, 0, -1));
        var moving = t.Velocity.Horizontal;
        if (cfg.Chase && moving.Length > 3)
        {
            // Hold a camera angle relative to "behind the rider", swinging round with their line.
            chaseAngle += orbit;
            var behind = Quat.FromYaw(chaseAngle) * -moving.Unit;
            var turn = AngleDelta(direction.Yaw, behind.Yaw) * Blend(dt, 1.2f);
            direction = Quat.FromYaw(turn) * direction;
        }
        Follow = direction * distance + V3.Up * height;
    }

    /// <summary>The follow point, pulled in towards the target when a wall or the ground is in the way.</summary>
    V3 ClearGoal(Target t, IWorld world, FlightConfig cfg)
    {
        var offset = Follow ?? default;
        var origin = t.Position + V3.Up * 0.8f;
        var goal = t.Position + offset;
        var delta = goal - origin;
        var length = delta.Length;
        if (length > 0.5f && world.Sweep(origin, delta * (1 / length), length, cfg.Radius * 1.5f) is { } hit)
        {
            goal = origin + delta * (MathF.Max(hit.Distance - 0.3f, 0.8f) / length);
        }
        if (world.Sweep(goal + V3.Up * 2, -V3.Up, 3.2f, 0) is { } down)
        {
            goal.Y += 3.2f - down.Distance;
        }
        return goal;
    }

    /// <summary>Moves along the velocity, sliding along whatever is hit. Returns the impact speed.</summary>
    float Travel(float h, IWorld world, FlightConfig cfg)
    {
        var remaining = Velocity * h;
        var impact = 0f;
        var bounce = Mode == Mode.Cine ? 0 : 0.25f;
        for (var i = 0; i < 3; i++)
        {
            var length = remaining.Length;
            if (length < 1e-6f)
            {
                break;
            }
            var dir = remaining * (1 / length);
            var hit = cfg.Collisions ? world.Sweep(Position, dir, length + Skin, cfg.Radius) : null;
            if (hit is not { } hh)
            {
                Position += remaining;
                break;
            }
            var moved = Math.Clamp(hh.Distance - Skin, 0, length);
            Position += dir * moved;
            var n = hh.Normal.UnitOr(-dir);
            var into = Velocity.Dot(n);
            if (into < 0)
            {
                impact = MathF.Max(impact, -into);
                var tangent = Velocity - n * into;
                Velocity = tangent * 0.85f - n * (into * bounce);
            }
            remaining = dir * (length - moved);
            remaining -= n * MathF.Min(remaining.Dot(n), 0);
        }
        return impact;
    }
}
