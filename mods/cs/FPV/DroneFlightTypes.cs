// The drone itself, without game objects: flight modes, physics and collision response.
//
// Cine mode is the game's own Drone logic (Drone.LateUpdate): it seeks a target pose at
// min(distance / followTime, sqrt(2 * maxAcceleration * distance)), steers with an acceleration clamped
// to maxAcceleration, turns with 1 - exp(-turnSpeed * dt) and tilts the body towards its acceleration
// up to maxTilt. Acro and Angle are a thrust model on top.
using Jibbers.Sdk;
using static Fpv.DroneMath;

namespace Fpv;

internal enum FlightMode
{
    Acro,
    Angle,
    Cine,
}

internal static class Modes
{
    public static readonly string[] Names =
    {
        "Acro",
        "Angle",
        "Cine"
    };
    public static FlightMode Next(this FlightMode mode) => mode switch
    {
        FlightMode.Acro => FlightMode.Angle,
        FlightMode.Angle => FlightMode.Cine,
        _ => FlightMode.Acro
    };
    public static FlightMode Parse(string name) => name switch
    {
        "Acro" => FlightMode.Acro,
        "Cine" => FlightMode.Cine,
        _ => FlightMode.Angle
    };
}

internal sealed record class DroneFlightSettings
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
internal record struct PilotControls
{
    /// <summary>Absolute throttle 0..1 when the pilot has one; otherwise Climb is a centred throttle whose middle is hover.</summary>
    public float? Throttle;
    public float Climb, Yaw, Pitch, Roll;
    /// <summary>Mouse look this frame, radians (positive: right / down).</summary>
    public float LookYaw, LookPitch;
    /// <summary>Gimbal tilt rate in Cine, -1..1 (positive: down).</summary>
    public float GimbalInput;
    public bool Precision;
}

internal record struct CollisionHit(float Distance, DroneVector Normal);
/// <summary>Collision queries against the map.</summary>
internal interface ICollisionWorld
{
    /// <summary>Sphere sweep (a ray when <paramref name = "radius"/> is 0) along the unit <paramref name = "direction"/>.</summary>
    CollisionHit? Sweep(DroneVector origin, DroneVector direction, float length, float radius);
}

internal record struct FlightTarget(DroneVector Position, DroneVector Velocity);
internal record struct FlightStepResult
{
    /// <summary>Fastest velocity into a surface this frame, m/s.</summary>
    public float Impact;
    public bool Crashed;
    public float? Ground;
    /// <summary>The state was not finite and has been reset; the caller should recall the drone.</summary>
    public bool Invalid;
}
