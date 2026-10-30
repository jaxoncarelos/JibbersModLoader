using Jibbers.Sdk;

namespace ChairLift;

/// <summary>Where the ride is: what the prompt shows and what the ride button does.</summary>
internal enum Stage
{
    /// <summary>Nowhere near a chair lift.</summary>
    Idle,
    /// <summary>Near a chair lift's bottom station: the ride button calls a chair.</summary>
    Near,
    /// <summary>Waiting for the next chair heading up.</summary>
    Waiting,
    Riding,
    /// <summary>Just got off at the top.</summary>
    Arrived,
}

/// <summary>Seat fit, from the settings.</summary>
internal readonly record struct SeatFit(float Height, float Forward, float Spacing);

/// <summary>Small vector helpers (the SDK's vectors are plain records).</summary>
internal static class Vec
{
    public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static float Length(Vector3 a) => MathF.Sqrt(Dot(a, a));
    public static float Distance(Vector3 a, Vector3 b) => Length(a - b);
    public static float FlatDistance(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    /// <summary>The horizontal direction of <paramref name="v"/>, or null when it's (nearly) vertical or zero.</summary>
    public static Vector3? FlatDirection(Vector3 v)
    {
        var l = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
        return l > 1e-4f ? new Vector3(v.X / l, 0, v.Z / l) : null;
    }

    /// <summary>Facing horizontal direction <paramref name="d"/> (a yaw-only rotation).</summary>
    public static Quaternion Yaw(Vector3 d)
    {
        var half = MathF.Atan2(d.X, d.Z) / 2;
        return new Quaternion(0, MathF.Sin(half), 0, MathF.Cos(half));
    }

    /// <summary>Right of horizontal direction <paramref name="d"/> (world up × d).</summary>
    public static Vector3 Side(Vector3 d) => new(d.Z, 0, -d.X);
}

/// <summary>
/// The four seats of a chair, in the chair's own space: evenly along the bench, at the bench's
/// height and depth, so riders sharing a chair sit lined up side by side, the same for everyone.
/// </summary>
internal static class SeatLayout
{
    public const int Seats = 4;
    /// <summary>How close another rider can be to a seat before it counts as taken (metres).</summary>
    public const float TakenWithin = 0.8f;

    /// <summary>
    /// The seats. <paramref name="lo"/>/<paramref name="hi"/> bound the chair in its own space;
    /// <paramref name="up"/>, <paramref name="forward"/> (uphill) and <paramref name="side"/> are
    /// world directions expressed in that space.
    /// </summary>
    public static Vector3[] Layout(Vector3 lo, Vector3 hi, Vector3 up, Vector3 forward, Vector3 side, SeatFit fit)
    {
        var centre = (lo + hi) * 0.5f;
        // The bottom of the box, below its centre (the lowest corner along "up").
        var bottom = float.MaxValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
            bottom = MathF.Min(bottom, Vec.Dot(corner, up));
        }
        var floor = centre + up * (bottom - Vec.Dot(centre, up));
        var seats = new Vector3[Seats];
        for (var k = 0; k < Seats; k++)
        {
            var across = (k - (Seats - 1) / 2f) * fit.Spacing;
            seats[k] = floor + up * fit.Height + forward * fit.Forward + side * across;
        }
        return seats;
    }

    /// <summary>
    /// The free seat nearest to <paramref name="me"/> (index), or -1 when all are taken. Each other
    /// rider takes only the seat nearest to them (the seats are closer together than a rider is wide).
    /// </summary>
    public static int Pick(IReadOnlyList<Vector3> worldSeats, IReadOnlyList<Vector3> otherRiders, Vector3 me)
    {
        var taken = new bool[worldSeats.Count];
        foreach (var o in otherRiders)
        {
            var nearest = -1;
            for (var i = 0; i < worldSeats.Count; i++)
                if (Vec.Distance(o, worldSeats[i]) < TakenWithin && (nearest < 0 || Vec.Distance(o, worldSeats[i]) < Vec.Distance(o, worldSeats[nearest])))
                    nearest = i;
            if (nearest >= 0)
                taken[nearest] = true;
        }
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < worldSeats.Count; i++)
        {
            if (taken[i])
                continue;
            var d = Vec.Distance(worldSeats[i], me);
            if (d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }
}

/// <summary>Which lifts are chair lifts. Every lift's cable is grabbable (chairs too), so the game's carrier names decide.</summary>
internal static class LiftKinds
{
    static readonly string[] Tows = ["tbar", "t-bar", "t_bar", "rope", "tow", "button", "platter", "poma", "drag", "hanger"];

    /// <param name="carrierName">The first carrier's name ("chairlift_chair(Clone)", "tbar_hanger(Clone)").</param>
    /// <param name="width">The carrier's widest horizontal size (metres).</param>
    /// <param name="carriers">How many carriers the lift has.</param>
    public static bool IsChairLift(string carrierName, float width, int carriers)
    {
        var name = carrierName.ToLowerInvariant();
        if (carriers == 0 || Tows.Any(name.Contains))
            return false;
        return name.Contains("chair") || width is >= 1.8f and < 8f;
    }
}

/// <summary>
/// The sitting pose. The game's poses hold a value per body part in 0..1 of each joint's range, in
/// this order: left hip, knee, foot, right hip, knee, foot, spine (three), then the arms. Standing
/// has the hips at 0.10 and the knees at 0.96; the crouch ("Tuck") 0.75 and 0.13.
/// </summary>
internal static class SitPose
{
    public const string Base = "Casual";

    /// <summary>The (part, value) changes that bend the legs: hip and knee bend in percent.</summary>
    public static (int Part, float Value)[] Legs(double hipPercent, double kneePercent)
    {
        var hip = (float)Math.Clamp(hipPercent / 100, 0, 1);
        var knee = (float)Math.Clamp(1 - kneePercent / 100, 0, 1);
        return [(0, hip), (1, knee), (2, 0.5f), (3, hip), (4, knee), (5, 0.5f)];
    }
}

/// <summary>Holding the rider between frames: the chair moves once a frame, the physics in fixed steps.</summary>
internal static class Hold
{
    /// <summary>The seat at a physics step <paramref name="dt"/> seconds after it was at <paramref name="at"/>.</summary>
    public static Vector3 SeatAt(Vector3 at, Vector3 velocity, float dt) => at + velocity * Math.Clamp(dt, 0, 0.1f);

    /// <summary>The chair's speed from two frames, smoothed (frame times wobble; the chair doesn't).</summary>
    public static Vector3 Smooth(Vector3 previous, Vector3 measured) => previous == default ? measured : Vec.Lerp(previous, measured, 0.15f);

    /// <summary>Whether to get off: near enough to the top wheel, and not just after boarding.</summary>
    public static bool ShouldGetOff(Vector3 seat, Vector3 topWheel, float unloadDistance, double secondsRiding) =>
        secondsRiding > 5 && Vec.FlatDistance(seat, topWheel) < unloadDistance;
}
