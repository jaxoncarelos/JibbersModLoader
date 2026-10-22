// The drone itself, without game objects: flight modes, physics and collision response. Port of the
// Rust FPV's flight.rs.
//
// Cine mode is the game's own Drone logic (Drone.LateUpdate): it seeks a target pose at
// min(distance / followTime, sqrt(2 * maxAcceleration * distance)), steers with an acceleration clamped
// to maxAcceleration, turns with 1 - exp(-turnSpeed * dt) and tilts the body towards its acceleration
// up to maxTilt. Acro and Angle are a thrust model on top.
using static Jibbers.Sdk.Features.Fpv.DroneMath;

namespace Jibbers.Sdk.Features.Fpv;

internal sealed class Drone
{
    public const float GravityAcceleration = 9.81f;
    /// <summary>Longest physics step; frames are split so fast flight cannot tunnel through thin geometry.</summary>
    const float MaximumStepSeconds = 1f / 240;
    const float CollisionMargin = 0.02f;
    public FlightMode FlightMode;
    public DroneVector Position, Velocity;
    /// <summary>Body orientation (world from body).</summary>
    public DroneRotation Attitude;
    /// <summary>Body angular velocity, rad/s (x pitch, y yaw, z roll).</summary>
    public DroneVector AngularVelocity;
    /// <summary>Current thrust acceleration, m/s² (the motors spin up and down, they don't jump).</summary>
    public float ThrustAcceleration;
    /// <summary>Heading and gimbal pitch (positive down) in Angle/Cine, radians.</summary>
    public float Heading, Gimbal;
    /// <summary>Seconds left before the motors re-arm after a crash.</summary>
    public float RearmDelaySeconds;
    /// <summary>Locked-target follow offset (drone minus target).</summary>
    public DroneVector? FollowOffset;
    float chaseAngle;
    DroneRotation sway = DroneRotation.Identity;
    float elapsedFlightSeconds;
    /// <summary>Maps a centred throttle stick so the middle hovers: -1 is motors idle, 1 is full power.</summary>
    public static float CentreHover(float climb, float hover)
    {
        var climbInput = Math.Clamp(climb, -1, 1);
        return climbInput >= 0 ? hover + (1 - hover) * climbInput : hover * (1 + climbInput);
    }

    /// <summary>Betaflight "actual" rates, in rad/s.</summary>
    public static float Rate(float stick, DroneFlightSettings settings)
    {
        var x = Math.Clamp(stick, -1, 1);
        var expo = Math.Clamp(settings.Expo, 0, 1);
        var curved = MathF.Abs(x) * (MathF.Pow(x, 5) * expo + x * (1 - expo));
        var movement = MathF.Max(settings.RateMax - settings.RateCenter, 0);
        return Radians(x * settings.RateCenter + movement * curved);
    }

    static float CalculateGimbalPitch(DroneVector forward) => Math.Clamp(MathF.Asin(Math.Clamp(-forward.Y, -1, 1)), -1.2f, 1.4f);
    /// <summary>A drone hovering where the camera is, facing where it looks.</summary>
    public Drone(DroneVector position, DroneRotation camera, FlightMode mode)
    {
        var forward = camera.Forward;
        Heading = forward.Horizontal.NormalizeOr(camera.Up.Horizontal.Normalized).Yaw;
        FlightMode = mode;
        Position = position;
        Attitude = DroneRotation.FromYaw(Heading);
        ThrustAcceleration = GravityAcceleration;
        Gimbal = CalculateGimbalPitch(forward);
    }

    public void SetMode(FlightMode mode, DroneFlightSettings settings)
    {
        if (mode == FlightMode)
        {
            return;
        }

        var camera = CalculateCameraRotation(settings, 0);
        if (mode == FlightMode.Cine)
        {
            var forward = camera.Forward;
            Heading = forward.Horizontal.NormalizeOr(Attitude.Up.Horizontal.Normalized).Yaw;
            Gimbal = CalculateGimbalPitch(forward);
        }
        else
        {
            if (FlightMode == FlightMode.Cine)
            {
                Attitude = DroneRotation.FromYaw(Heading);
            }

            Heading = Attitude.Forward.Horizontal.NormalizeOr(-Attitude.Up.Horizontal.Normalized).Yaw;
            AngularVelocity = default;
            ThrustAcceleration = MathF.Max(ThrustAcceleration, GravityAcceleration);
        }

        FlightMode = mode;
    }

    /// <summary>Locks onto (or releases) a target; the current offset becomes the follow offset.</summary>
    public void Lock(DroneVector? target)
    {
        FollowOffset = target is { } targetPosition ? Offset(Position - targetPosition) : null;
        chaseAngle = 0;
        static DroneVector Offset(DroneVector offset)
        {
            var horizontalOffset = offset.Horizontal;
            return horizontalOffset.NormalizeOr(new DroneVector(0, 0, -1)) * Math.Clamp(horizontalOffset.Length, 3, 40) + new DroneVector(0, Math.Clamp(offset.Y, 0.5f, 25), 0);
        }
    }

    /// <summary>Puts the drone behind and above <paramref name = "target"/>, looking at it.</summary>
    public void Recall(DroneVector target, DroneVector forward)
    {
        var back = forward.Horizontal.NormalizeOr(DroneVector.Forward);
        Position = target - back * 4 + DroneVector.Up * 2;
        Velocity = default;
        AngularVelocity = default;
        ThrustAcceleration = GravityAcceleration;
        RearmDelaySeconds = 0;
        var look = target + DroneVector.Up * 0.5f - Position;
        Heading = look.Yaw;
        Gimbal = MathF.Asin(-look.Normalized.Y);
        Attitude = DroneRotation.FromYaw(Heading);
        if (FollowOffset is not null)
        {
            Lock(target);
        }
    }

    /// <summary>Motor output as a fraction of full thrust (what an OSD shows as throttle).</summary>
    public float CalculateMotorOutput(DroneFlightSettings settings) => Math.Clamp(ThrustAcceleration / (MathF.Max(settings.ThrustToWeight, 1.1f) * GravityAcceleration), 0, 1);
    /// <summary>Where the camera looks: uptilted from the body in Acro/Angle, a stabilised gimbal in Cine.</summary>
    public DroneRotation CalculateCameraRotation(DroneFlightSettings settings, float vibration)
    {
        var baseRotation = FlightMode == FlightMode.Cine ? sway * DroneRotation.FromYaw(Heading) * DroneRotation.FromPitch(Gimbal) : Attitude * DroneRotation.FromPitch(-Radians(settings.Uptilt));
        if (vibration <= 0 || RearmDelaySeconds > 0)
        {
            return baseRotation;
        }

        // Prop buzz: a fraction of a degree, stronger with throttle.
        var elapsedSeconds = elapsedFlightSeconds;
        var power = 0.35f + Math.Clamp(ThrustAcceleration / (settings.ThrustToWeight * GravityAcceleration), 0, 1);
        var amplitude = Radians(0.12f * vibration * power);
        var jitter = new DroneVector(MathF.Sin(elapsedSeconds * 61.3f) + 0.5f * MathF.Sin(elapsedSeconds * 143.7f), 0.4f * MathF.Sin(elapsedSeconds * 89.1f), MathF.Sin(elapsedSeconds * 73.9f) + 0.5f * MathF.Sin(elapsedSeconds * 127.3f)) * amplitude;
        return baseRotation.RotateLocal(jitter);
    }

    public FlightStepResult Step(float deltaTime, in PilotControls controls, FlightTarget? target, ICollisionWorld world, DroneFlightSettings settings)
    {
        var report = new FlightStepResult();
        if (!float.IsFinite(deltaTime))
            return report;
        deltaTime = Math.Clamp(deltaTime, 0, 0.1f);
        if (deltaTime <= 0)
        {
            return report;
        }

        elapsedFlightSeconds += deltaTime;
        var precisionScale = controls.Precision ? 0.5f : 1;
        report.Ground = world.Sweep(Position, -DroneVector.Up, 6, 0)?.Distance;
        var groundEffect = report.Ground is { } groundHeight ? 1 + 0.18f * Math.Clamp(1 - groundHeight / 1.2f, 0, 1) : 1;
        // Mouse look acts once per frame.
        switch (FlightMode)
        {
            case FlightMode.Acro:
                Attitude = Attitude.RotateLocal(new DroneVector(controls.LookPitch, controls.LookYaw, 0));
                break;
            case FlightMode.Angle:
                Heading += controls.LookYaw;
                break;
            default:
                Heading += controls.LookYaw;
                Gimbal = Math.Clamp(Gimbal + controls.LookPitch, -1.2f, 1.4f);
                break;
        }

        // The follow goal is planned once per frame: keep it on the target's side of walls.
        DroneVector? goal = null;
        if (FlightMode == FlightMode.Cine && target is { } followTarget && FollowOffset is not null && settings.FollowTarget)
        {
            UpdateFollowOffset(deltaTime, controls, precisionScale, followTarget, settings);
            goal = FindUnobstructedFollowGoal(followTarget, world, settings);
        }

        var stepCount = (int)Math.Clamp(MathF.Ceiling(deltaTime / MaximumStepSeconds), 1, 32);
        var stepSeconds = deltaTime / stepCount;
        for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
        {
            if (FlightMode == FlightMode.Cine)
            {
                SimulateCinematicFlight(stepSeconds, controls, precisionScale, target, goal, report.Ground, settings);
            }
            else
            {
                SimulatePoweredFlight(stepSeconds, controls, precisionScale, groundEffect, settings);
            }

            report.Impact = MathF.Max(report.Impact, MoveWithCollisions(stepSeconds, world, settings));
            RearmDelaySeconds = MathF.Max(RearmDelaySeconds - stepSeconds, 0);
        }

        if (settings.Crash && FlightMode != FlightMode.Cine && report.Impact > 9 && RearmDelaySeconds <= 0)
        {
            // Hard hit: the motors cut out and the quad tumbles until they re-arm.
            RearmDelaySeconds = 1.2f;
            AngularVelocity += new DroneVector(report.Impact * 0.5f, report.Impact * 0.15f, report.Impact * 0.35f);
            report.Crashed = true;
        }

        if (!(Position.IsFinite && Velocity.IsFinite && Attitude.IsFinite && AngularVelocity.IsFinite))
        {
            Velocity = default;
            AngularVelocity = default;
            Attitude = DroneRotation.Identity;
            report.Invalid = true;
        }

        return report;
    }

    /// <summary>Acro and Angle: rate or self-levelling attitude control, thrust along body up, drag.</summary>
    void SimulatePoweredFlight(float deltaTime, in PilotControls controls, float precisionScale, float groundEffect, DroneFlightSettings settings)
    {
        var maxThrust = MathF.Max(settings.ThrustToWeight, 1.1f) * GravityAcceleration;
        var sensitivity = Math.Clamp(settings.Sensitivity, 0.05f, 2);
        DroneVector desiredAngularVelocity;
        if (FlightMode == FlightMode.Acro)
        {
            desiredAngularVelocity = new DroneVector(Rate(controls.Pitch * precisionScale, settings), Rate(controls.Yaw * precisionScale, settings), -Rate(controls.Roll * precisionScale, settings)) * sensitivity;
        }
        else
        {
            Heading += Math.Clamp(Rate(controls.Yaw * precisionScale, settings) * 0.5f * sensitivity, -3, 3) * deltaTime;
            var tilt = Radians(Math.Clamp(settings.MaxAngle, 5, 85));
            var level = DroneRotation.FromYaw(Heading) * DroneRotation.FromPitch(controls.Pitch * precisionScale * tilt) * DroneRotation.FromRoll(-controls.Roll * precisionScale * tilt);
            desiredAngularVelocity = ((Attitude.Conjugate * level).RotationVector * 10).ClampLength(14);
        }

        // The flight controller follows the sticks within a few tens of milliseconds.
        if (RearmDelaySeconds <= 0)
        {
            AngularVelocity += (desiredAngularVelocity - AngularVelocity) * Blend(deltaTime, 0.03f);
        }

        Attitude = Attitude.RotateLocal(AngularVelocity * deltaTime);
        var hover = GravityAcceleration / maxThrust;
        float desiredThrust;
        if (FlightMode == FlightMode.Angle && settings.AltitudeHold && controls.Throttle is null)
        {
            // Centred stick holds altitude; deflection commands a climb rate.
            var climb = controls.Climb * precisionScale * settings.ClimbSpeed;
            desiredThrust = (GravityAcceleration + (climb - Velocity.Y) * 3.5f) / MathF.Max(Attitude.Up.Y, 0.3f);
        }
        else
        {
            desiredThrust = (controls.Throttle ?? CentreHover(controls.Climb, hover)) * maxThrust;
        }

        desiredThrust = RearmDelaySeconds > 0 ? 0 : Math.Clamp(desiredThrust, 0, maxThrust);
        ThrustAcceleration += (desiredThrust - ThrustAcceleration) * Blend(deltaTime, 0.035f);
        var quadraticDrag = maxThrust * 0.95f / MathF.Pow(MathF.Max(settings.TopSpeed, 5), 2);
        var velocity = Velocity;
        var acceleration = Attitude.Up * (ThrustAcceleration * groundEffect) - DroneVector.Up * GravityAcceleration - velocity * 0.05f - velocity * (velocity.Length * quadraticDrag);
        Velocity += acceleration * deltaTime;
    }

    /// <summary>Cine: the game drone's pursuit towards a velocity or follow goal, with a stabilised gimbal.</summary>
    void SimulateCinematicFlight(float deltaTime, in PilotControls controls, float precisionScale, FlightTarget? target, DroneVector? goal, float? ground, DroneFlightSettings settings)
    {
        var followTime = MathF.Max(settings.FollowTime, 0.05f);
        DroneVector desiredVelocity;
        if (goal is { } followGoal && target is { } followTarget)
        {
            var displacement = followGoal - Position;
            var distance = displacement.Length;
            var speed = MathF.Min(distance / followTime, MathF.Sqrt(2 * settings.MaxAcceleration * distance));
            desiredVelocity = displacement.NormalizeOr(default) * speed + followTarget.Velocity;
        }
        else
        {
            Heading += controls.Yaw * precisionScale * 2 * settings.Sensitivity * deltaTime;
            var forward = new DroneVector(MathF.Sin(Heading), 0, MathF.Cos(Heading));
            var right = new DroneVector(forward.Z, 0, -forward.X);
            desiredVelocity = (forward * controls.Pitch + right * controls.Roll).ClampLength(1) * (settings.CineSpeed * precisionScale) + DroneVector.Up * (controls.Climb * precisionScale * settings.ClimbSpeed);
        }

        if (ground is < 0.8f)
        {
            desiredVelocity.Y = MathF.Max(desiredVelocity.Y, 0);
        }

        Gimbal = Math.Clamp(Gimbal + controls.GimbalInput * precisionScale * 1.4f * settings.Sensitivity * deltaTime, -1.2f, 1.4f);
        var acceleration = ((desiredVelocity - Velocity) * (1 / followTime)).ClampLength(settings.MaxAcceleration);
        Velocity += acceleration * deltaTime;
        if (target is { } trackedTarget)
        {
            // Keep the rider framed (chest height), turning like the game drone.
            var look = trackedTarget.Position + DroneVector.Up * 0.4f - Position;
            if (look.Length > 0.5f)
            {
                var turnBlend = Blend(deltaTime, 1 / MathF.Max(settings.TurnSpeed, 0.5f));
                Heading += AngleDelta(Heading, look.Yaw) * turnBlend;
                var pitch = Math.Clamp(MathF.Asin(-look.Normalized.Y), -1.2f, 1.4f);
                Gimbal += (pitch - Gimbal) * turnBlend;
            }
        }

        // The game's tilt from acceleration, felt through the gimbal as a little sway.
        var tilt = DroneRotation.FromTo(DroneVector.Up, DroneVector.Up + acceleration * 0.035f);
        var limited = DroneRotation.Identity.Slerp(tilt, MathF.Min(Radians(35) / MathF.Max(tilt.RotationVector.Length, 1e-4f), 1));
        Attitude = limited * DroneRotation.FromYaw(Heading);
        sway = sway.Slerp(DroneRotation.Identity.Slerp(limited, 0.2f), Blend(deltaTime, 0.15f));
        ThrustAcceleration = GravityAcceleration;
    }

    void UpdateFollowOffset(float deltaTime, in PilotControls controls, float precisionScale, FlightTarget target, DroneFlightSettings settings)
    {
        if (FollowOffset is not { } offset)
        {
            return;
        }

        var flat = offset.Horizontal;
        var distance = Math.Clamp(flat.Length * (1 - controls.Pitch * precisionScale * 0.9f * deltaTime), 2, 60);
        var height = Math.Clamp(offset.Y + controls.Climb * precisionScale * settings.ClimbSpeed * deltaTime, -3, 40);
        var orbit = -controls.Roll * precisionScale * 1.5f * settings.Sensitivity * deltaTime;
        var direction = DroneRotation.FromYaw(orbit) * flat.NormalizeOr(new DroneVector(0, 0, -1));
        var moving = target.Velocity.Horizontal;
        if (settings.Chase && moving.Length > 3)
        {
            // Hold a camera angle relative to "behind the rider", swinging round with their line.
            chaseAngle += orbit;
            var behind = DroneRotation.FromYaw(chaseAngle) * -moving.Normalized;
            var turn = AngleDelta(direction.Yaw, behind.Yaw) * Blend(deltaTime, 1.2f);
            direction = DroneRotation.FromYaw(turn) * direction;
        }

        FollowOffset = direction * distance + DroneVector.Up * height;
    }

    /// <summary>The follow point, pulled in towards the target when a wall or the ground is in the way.</summary>
    DroneVector FindUnobstructedFollowGoal(FlightTarget target, ICollisionWorld world, DroneFlightSettings settings)
    {
        var offset = FollowOffset ?? default;
        var origin = target.Position + DroneVector.Up * 0.8f;
        var goal = target.Position + offset;
        var delta = goal - origin;
        var length = delta.Length;
        if (length > 0.5f && world.Sweep(origin, delta * (1 / length), length, settings.Radius * 1.5f)is { } hit)
        {
            goal = origin + delta * (MathF.Max(hit.Distance - 0.3f, 0.8f) / length);
        }

        if (world.Sweep(goal + DroneVector.Up * 2, -DroneVector.Up, 3.2f, 0)is { } down)
        {
            goal.Y += 3.2f - down.Distance;
        }

        return goal;
    }

    /// <summary>Moves along the velocity, sliding along whatever is hit. Returns the impact speed.</summary>
    float MoveWithCollisions(float deltaTime, ICollisionWorld world, DroneFlightSettings settings)
    {
        var remaining = Velocity * deltaTime;
        var impact = 0f;
        var bounce = FlightMode == FlightMode.Cine ? 0 : 0.25f;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var length = remaining.Length;
            if (length < 1e-6f)
            {
                break;
            }

            var direction = remaining * (1 / length);
            var hit = settings.Collisions ? world.Sweep(Position, direction, length + CollisionMargin, settings.Radius) : null;
            if (hit is not { } collision)
            {
                Position += remaining;
                break;
            }

            var moved = Math.Clamp(collision.Distance - CollisionMargin, 0, length);
            Position += direction * moved;
            var surfaceNormal = collision.Normal.NormalizeOr(-direction);
            var normalSpeed = Velocity.Dot(surfaceNormal);
            if (normalSpeed < 0)
            {
                impact = MathF.Max(impact, -normalSpeed);
                var tangent = Velocity - surfaceNormal * normalSpeed;
                Velocity = tangent * 0.85f - surfaceNormal * (normalSpeed * bounce);
            }

            remaining = direction * (length - moved);
            remaining -= surfaceNormal * MathF.Min(remaining.Dot(surfaceNormal), 0);
        }

        return impact;
    }
}
