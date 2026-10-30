using Jibbers.Sdk;

namespace Slvsh.Integration;

/// <summary>
/// A chase camera behind a moving rider: it sits behind and above them, turns to face where they're
/// heading, and eases after them. Pure (no game calls), so it is tested without the game.
/// </summary>
internal sealed class ChaseCamera
{
    /// <summary>How far behind and above the rider the camera sits, and how far a jump counts as a respawn.</summary>
    public const float Distance = 6, Height = 2.2f, LookAbove = 0.6f, Teleport = 25;
    /// <summary>Below this speed (m/s) the camera keeps its heading, so it doesn't swing around a rider standing still.</summary>
    public const float TurnSpeed = 2;

    Vector3 body, velocity, heading, eye;
    bool started;

    /// <summary>Where the camera wants to be (before keeping it out of the ground).</summary>
    public Vector3 Eye => eye;
    /// <summary>The point it looks at, just above the rider.</summary>
    public Vector3 Focus => body + Up * LookAbove;
    public Vector3 Heading => heading;

    static readonly Vector3 Up = new(0, 1, 0);

    /// <summary>Starts from where the camera is now, facing the rider from there.</summary>
    public void Start(Vector3 rider, Vector3 cameraAt)
    {
        body = rider;
        velocity = default;
        heading = Flat(rider - cameraAt) ?? new Vector3(0, 0, 1);
        eye = cameraAt;
        started = true;
    }

    /// <summary>Follows the rider, now at <paramref name="rider"/>, for <paramref name="dt"/> seconds.</summary>
    public void Step(float dt, Vector3 rider)
    {
        if (!started || (rider - body).Magnitude > Teleport)
        {
            // First frame or they respawned: cut straight to behind them.
            var keep = started ? heading : new Vector3(0, 0, 1);
            body = rider;
            velocity = default;
            heading = keep;
            eye = Behind(rider, heading);
            started = true;
            return;
        }
        if (dt <= 0) return;
        // Puppets move in network steps, so a frame's movement is spiky: smooth it into a speed.
        velocity = Lerp(velocity, (rider - body) * (1 / dt), Ease(dt, 4));
        body = rider;
        if (Flat(velocity) is { } moving && Horizontal(velocity) > TurnSpeed)
            heading = Flat(Lerp(heading, moving, Ease(dt, 2.5f))) ?? heading;
        eye = Lerp(eye, Behind(rider, heading), Ease(dt, 5));
    }

    static Vector3 Behind(Vector3 rider, Vector3 heading) => rider - heading * Distance + Up * Height;

    /// <summary>The fraction to move this frame to close a gap at <paramref name="rate"/> per second, whatever the frame rate.</summary>
    static float Ease(float dt, float rate) => 1 - MathF.Exp(-dt * rate);

    static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    static float Horizontal(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    /// <summary>The horizontal direction of <paramref name="v"/>, or null when it has none.</summary>
    static Vector3? Flat(Vector3 v)
    {
        var length = Horizontal(v);
        return length < 1e-3f ? null : new Vector3(v.X / length, 0, v.Z / length);
    }

    /// <summary>The rotation that looks along <paramref name="forward"/> with no roll (Unity's <c>LookRotation</c> with world up).</summary>
    public static Quaternion Look(Vector3 forward)
    {
        var length = forward.Magnitude;
        if (length < 1e-5f) return Quaternion.Identity;
        var f = forward * (1 / length);
        var yaw = MathF.Atan2(f.X, f.Z);
        var pitch = -MathF.Asin(Math.Clamp(f.Y, -1, 1));
        float sy = MathF.Sin(yaw / 2), cy = MathF.Cos(yaw / 2), sp = MathF.Sin(pitch / 2), cp = MathF.Cos(pitch / 2);
        return new Quaternion(cy * sp, sy * cp, -sy * sp, cy * cp);
    }
}
