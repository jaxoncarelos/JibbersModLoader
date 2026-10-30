using ChairLift.Integration;
using Jibbers.Interop.Game;
using Jibbers.Sdk;
using System.Diagnostics;

namespace ChairLift;

/// <summary>What the ride reads from the settings.</summary>
internal sealed record RideOptions(SeatFit Fit, bool Sit, double HipPercent, double KneePercent, bool AutoOff, float UnloadDistance);

/// <summary>
/// The ride: near a chair lift's bottom station the ride button calls a chair; the next chair
/// heading up takes you in a free seat; you sit, can open the phone, and get off near the top
/// wheel (or jump off with the ride button). Runs on the game's frames (input, following the chair,
/// the prompt) and physics steps (holding you on the seat). Main thread only.
/// </summary>
internal sealed class RideRuntime : IDisposable
{
    /// <summary>How close (metres) to the bottom station you can call a chair from.</summary>
    const float BoardRange = 35;
    /// <summary>Chairs this close to the bottom wheel can be boarded.</summary>
    const float PickupRange = 18;
    /// <summary>How long the rider is placed the hard way before the physics takes over.</summary>
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);
    /// <summary>The chair's shapes switched off within this distance (metres) of the seat.</summary>
    const float ChairRadius = 4;

    readonly PhysicsHold physics = new();
    readonly Rider rider = new();
    readonly Dictionary<nint, Vector3> lastChairPosition = new();
    readonly HashSet<nint> fullChairs = new();
    readonly Stopwatch since = new(), lastStep = new(), lastNearCheck = Stopwatch.StartNew(), restoreTimer = new();

    public Stage Stage { get; private set; } = Stage.Idle;
    LiftLine lift;
    nint chair;
    Vector3 localSeat, upAxis = new(0, 0, 1), topWheel, velocity;
    Quaternion facing = Quaternion.Identity;
    (Vector3 At, float Time)? previous;
    // Where the physics step holds the rider: the seat at a Unity time, and its speed.
    (Vector3 At, Vector3 Velocity, float Time)? target;
    bool phoneOpen;
    int frame;

    public bool PhoneOpen { get => phoneOpen; set => phoneOpen = value; }

    // ---- frames ----

    /// <summary>One rendered frame: the buttons pressed since the last one, and the options.</summary>
    public void Frame(bool ridePressed, bool phonePressed, RideOptions options)
    {
        frame++;
        if (restoreTimer.IsRunning && restoreTimer.Elapsed > TimeSpan.FromSeconds(1.5))
        {
            restoreTimer.Reset();
            physics.RestoreCollisions();
        }
        if (Stage == Stage.Arrived && since.Elapsed > TimeSpan.FromSeconds(3))
            Stage = Stage.Idle;
        switch (Stage)
        {
            case Stage.Riding:
                // The ride button selects things on the phone, and a double press right after
                // getting on shouldn't throw you straight off.
                if (ridePressed && !phoneOpen && since.Elapsed > TimeSpan.FromSeconds(1.5))
                {
                    JumpOff();
                    return;
                }
                if (phonePressed)
                    TogglePhone();
                Ride(options);
                break;
            case Stage.Waiting:
                if (ridePressed || since.Elapsed > TimeSpan.FromSeconds(60))
                    Stage = Stage.Idle;
                else
                    Board(options);
                break;
            case Stage.Idle or Stage.Near:
                if (!ridePressed && lastNearCheck.Elapsed < TimeSpan.FromMilliseconds(250))
                    break;
                lastNearCheck.Restart();
                var me = Rider.Find() is { } r ? Rider.Centre(r) : (Vector3?)null;
                var nearest = me is { } m ? GameLifts.Nearest(m) : null;
                if (nearest is { } n && n.Distance <= BoardRange)
                {
                    if (ridePressed)
                    {
                        lift = n.Lift;
                        Stage = Stage.Waiting;
                        since.Restart();
                        fullChairs.Clear();
                        lastChairPosition.Clear();
                    }
                    else
                        Stage = Stage.Near;
                }
                else
                {
                    if (ridePressed)
                        Log.Info("ChairLift", me is null ? "ride pressed, but your rider wasn't found"
                            : nearest is { } far ? $"ride pressed {far.Distance:0} m from the nearest chair lift's bottom station (needs {BoardRange:0} m)"
                            : "ride pressed, but there's no chair lift here");
                    Stage = Stage.Idle;
                }
                break;
        }
    }

    /// <summary>Waiting: get on the nearest chair heading up from the bottom wheel, in a free seat.</summary>
    void Board(RideOptions options)
    {
        if (lift.IsNull || Unity.IsNull(lift.Ptr) || GameLifts.Point(lift.baseStation, "wheel") is not { } baseWheel || GameLifts.Point(lift.topStation, "wheel") is not { } top)
        {
            Stage = Stage.Idle;
            return;
        }
        var up = Vec.FlatDirection(top - baseWheel) ?? new Vector3(0, 0, 1);
        (nint Chair, Vector3 Dir, float Distance)? pick = null;
        foreach (var c in GameLifts.Carriers(lift))
        {
            var p = GameLifts.Position(c);
            var dir = lastChairPosition.TryGetValue(c, out var q) ? Vec.FlatDirection(p - q) : null;
            lastChairPosition[c] = p;
            var d = Vec.FlatDistance(p, baseWheel);
            if (dir is { } heading && Vec.Dot(heading, up) > 0.5f && d < PickupRange && !fullChairs.Contains(c) && (pick is not { } best || d < best.Distance))
                pick = (c, heading, d);
        }
        if (Rider.Find() is not { } r)
            return;
        // Skied away while waiting: don't teleport onto the lift from wherever you are now.
        if (GameLifts.DistanceToBase(lift, Rider.Centre(r)) is not { } fromBase || fromBase > BoardRange)
        {
            Stage = Stage.Idle;
            Log.Info("ChairLift", "left the bottom station, stopped waiting for a chair");
            return;
        }
        if (pick is not { } chosen)
            return;
        if (PickSeat(chosen.Chair, chosen.Dir, Rider.Centre(r), options.Fit) is not { } seat)
        {
            fullChairs.Add(chosen.Chair);
            return;
        }
        chair = chosen.Chair;
        localSeat = seat;
        facing = Vec.Yaw(chosen.Dir);
        upAxis = up;
        topWheel = top;
        previous = null;
        velocity = default;
        target = null;
        physics.End();
        // Off before you're put on the seat: placed inside the chair's shapes, the physics would
        // throw you off it (and the game may count that as a physics error and respawn you).
        restoreTimer.Reset();
        physics.ChairCollisionsOff(chair, GameLifts.TransformPoint(chair, seat), ChairRadius, Rider.Parts(r));
        Stage = Stage.Riding;
        since.Restart();
        if (options.Sit)
            rider.Sit(r, options.HipPercent, options.KneePercent);
        Log.Info("ChairLift", "on the chair lift");
    }

    /// <summary>A free seat on <paramref name="c"/>, in the chair's own space, or null when all four are taken.</summary>
    static Vector3? PickSeat(nint c, Vector3 dir, Vector3 me, SeatFit fit)
    {
        var (wlo, whi) = GameLifts.Bounds(c) ?? (GameLifts.Position(c) - new Vector3(1, 3, 1), GameLifts.Position(c) + new Vector3(1, 0, 1));
        // The chair's box in its own space, from the corners of its world box.
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? wlo.X : whi.X, (i & 2) == 0 ? wlo.Y : whi.Y, (i & 4) == 0 ? wlo.Z : whi.Z);
            var l = GameLifts.InverseTransformPoint(c, corner);
            lo = new(MathF.Min(lo.X, l.X), MathF.Min(lo.Y, l.Y), MathF.Min(lo.Z, l.Z));
            hi = new(MathF.Max(hi.X, l.X), MathF.Max(hi.Y, l.Y), MathF.Max(hi.Z, l.Z));
        }
        var seats = SeatLayout.Layout(lo, hi, GameLifts.InverseTransformDirection(c, new Vector3(0, 1, 0)),
            GameLifts.InverseTransformDirection(c, dir), GameLifts.InverseTransformDirection(c, Vec.Side(dir)), fit);
        var world = seats.Select(s => GameLifts.TransformPoint(c, s)).ToArray();
        var i2 = SeatLayout.Pick(world, Rider.Others(), me);
        return i2 < 0 ? null : seats[i2];
    }

    /// <summary>Riding: follow the chair, hand the seat to the physics step, get off at the top.</summary>
    void Ride(RideOptions options)
    {
        var r = Rider.Find();
        if (r is not { } me || chair == 0 || Unity.IsNull(chair))
        {
            Leave(r);
            Stage = Stage.Idle;
            return;
        }
        var at = GameLifts.TransformPoint(chair, localSeat);
        var now = Unity.Time;
        if (previous is { } p && now - p.Time > 1e-4f)
            velocity = Hold.Smooth(velocity, (at - p.At) * (1 / (now - p.Time)));
        previous = (at, now);

        if (options.AutoOff && Hold.ShouldGetOff(at, topWheel, options.UnloadDistance, since.Elapsed.TotalSeconds))
        {
            var dir = Vec.FlatDirection(velocity) ?? upAxis;
            Leave(me);
            Rider.Place(me, at + dir * 0.5f, facing, dir * 4 + new Vector3(0, 0.5f, 0));
            Stage = Stage.Arrived;
            since.Restart();
            Log.Info("ChairLift", "got off at the top");
            return;
        }
        Rider.AllowPhone(me);
        if (options.Sit && frame % 10 == 0)
            rider.Sit(me, options.HipPercent, options.KneePercent);
        // Look straight ahead up the lift (at head height), not round at the camera.
        rider.LookAt(me, at + (Vec.FlatDirection(velocity) ?? upAxis) * 20 + new Vector3(0, 0.7f, 0));

        // The game rebuilt its physics scene: the bodies held are gone, find them again.
        if (physics.Holding && !physics.StillValid())
        {
            Log.Info("ChairLift", "the physics scene was rebuilt, holding the rider again");
            physics.End();
            // The new scene has the chair's shapes on: off again before you're put back on the seat.
            physics.ChairCollisionsOff(chair, at, ChairRadius, Rider.Parts(me));
            target = null;
            since.Restart();
        }
        // First moment: onto the seat the hard way (this also resets the pose).
        if (since.Elapsed < Settle)
        {
            Rider.Place(me, at, facing, velocity);
            return;
        }
        if (!physics.Holding)
        {
            restoreTimer.Reset();
            var parts = Rider.Parts(me);
            if (!physics.Begin(at, Rider.Centre(me), 1.8f, parts))
            {
                Rider.Place(me, at, facing, velocity); // no physics access: the old way
                return;
            }
            // Again now the rider's bodies are known exactly (riders who sat down since, too).
            var off = physics.ChairCollisionsOff(chair, at, ChairRadius, parts);
            Log.Info("ChairLift", $"holding the rider on the chair, {off} of the chair's collision shapes off");
        }
        target = (at, velocity, now);
        // The physics step normally does the holding; if it isn't running, do it here.
        if (!lastStep.IsRunning || lastStep.Elapsed > TimeSpan.FromMilliseconds(250))
            physics.Hold(PhysicsHold.Scene(), at, velocity);
        // Knocked far off the seat anyway: put the rider back the hard way.
        if (physics.RootPosition() is { } root && Vec.Distance(root, at) > 3)
            Rider.Place(me, at, facing, velocity);
    }

    void JumpOff()
    {
        var r = Rider.Find();
        if (r is { } me && previous is { } p)
        {
            var dir = Vec.FlatDirection(velocity) ?? upAxis;
            Leave(me);
            Rider.Place(me, p.At + dir * 0.6f, facing, dir * 3 + new Vector3(0, 2.5f, 0));
        }
        else
            Leave(r);
        Stage = Stage.Idle;
        Log.Info("ChairLift", "jumped off the chair");
    }

    /// <summary>Standing up, putting the phone away, letting the physics go, and collisions back on a moment later.</summary>
    void Leave(RagdollController? r)
    {
        target = null;
        physics.End();
        rider.Stand(r);
        if (physics.CollisionsOff)
            restoreTimer.Restart();
        if (phoneOpen)
            TogglePhone();
    }

    /// <summary>The phone is the game's pause menu, which the game won't open off the ground: raise the event it listens for.</summary>
    void TogglePhone()
    {
        // Raise runs every listener before it returns, this mod's own pause/unpause listener too
        // (which sets phoneOpen), so decide the new state first rather than flipping it after.
        var open = !phoneOpen;
        Events.Raise(open ? "event.pause" : "event.unpause", stackalloc byte[1]);
        phoneOpen = open;
    }

    /// <summary>You respawned: off the chair, collisions straight back on.</summary>
    public void Respawned()
    {
        if (Stage is Stage.Riding or Stage.Waiting)
        {
            if (Stage == Stage.Riding)
                Leave(Rider.Find());
            restoreTimer.Reset();
            physics.RestoreCollisions();
            Stage = Stage.Idle;
        }
    }

    /// <summary>A new map: forget the old lifts.</summary>
    public void MapChanged()
    {
        Respawned();
        GameLifts.Reset();
    }

    // ---- physics steps ----

    /// <summary>One physics step of scene <paramref name="scene"/>: hold the rider where the chair is now.</summary>
    public void PhysicsStep(nint scene)
    {
        lastStep.Restart();
        if (Stage != Stage.Riding || target is not { } t || !physics.Holding)
            return;
        physics.Hold(scene, Hold.SeatAt(t.At, t.Velocity, Unity.Time - t.Time), t.Velocity);
    }

    public void Dispose()
    {
        if (Stage == Stage.Riding)
            Leave(Rider.Find());
        physics.RestoreCollisions();
        rider.Dispose();
    }
}
