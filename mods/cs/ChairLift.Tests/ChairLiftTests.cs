using ChairLift;
using Jibbers.Sdk;
using System.Runtime.CompilerServices;

static class ChairLiftTests
{
    static readonly Vector3 Up = new(0, 1, 0), Forward = new(0, 0, 1), Side = new(1, 0, 0);
    static readonly SeatFit Fit = new(Height: 0.8f, Forward: -0.35f, Spacing: 0.6f);

    static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

    [Test]
    static void TheModDescribesItself()
    {
        // Without running the constructor, which may expect the game.
        var mod = (ChairLiftMod)RuntimeHelpers.GetUninitializedObject(typeof(ChairLiftMod));
        Assert.Equal("chairlift", mod.Id);
        Assert.Equal("Chair Lift", mod.Name);
    }

    [Test]
    static void SeatsAreEvenlyLinedUpAlongTheBench()
    {
        // A 2.4 m wide, 2.5 m tall, 1 m deep chair centred on the origin.
        var seats = SeatLayout.Layout(new(-1.2f, -1.25f, -0.5f), new(1.2f, 1.25f, 0.5f), Up, Forward, Side, Fit);
        Assert.Equal(4, seats.Length);
        // Same height and depth for every seat: 0.8 m above the chair's bottom, 0.35 m back.
        Assert.True(seats.All(s => Near(s.Y, -1.25f + 0.8f) && Near(s.Z, -0.35f)));
        // 0.6 m apart, centred across the chair.
        Assert.True(Near(seats[0].X, -0.9f) && Near(seats[1].X, -0.3f) && Near(seats[2].X, 0.3f) && Near(seats[3].X, 0.9f));
    }

    [Test]
    static void SeatsFollowTheChairsOwnAxes()
    {
        // The same chair turned so "uphill" is its -X axis and "across" its +Z.
        var seats = SeatLayout.Layout(new(-0.5f, -1.25f, -1.2f), new(0.5f, 1.25f, 1.2f), Up, new(-1, 0, 0), new(0, 0, 1), Fit);
        Assert.True(seats.All(s => Near(s.X, 0.35f)), "0.35 m back from uphill is +X");
        Assert.True(Near(seats[0].Z, -0.9f) && Near(seats[3].Z, 0.9f));
    }

    [Test]
    static void YouGetTheNearestFreeSeat()
    {
        var seats = new[] { new Vector3(-0.9f, 0, 0), new Vector3(-0.3f, 0, 0), new Vector3(0.3f, 0, 0), new Vector3(0.9f, 0, 0) };
        Assert.Equal(3, SeatLayout.Pick(seats, Array.Empty<Vector3>(), new Vector3(2, 0, 0)));
        // A friend already sits in that seat: the next one along.
        Assert.Equal(2, SeatLayout.Pick(seats, new[] { new Vector3(0.95f, 0.1f, 0) }, new Vector3(2, 0, 0)));
        // A full chair.
        Assert.Equal(-1, SeatLayout.Pick(seats, seats, new Vector3(2, 0, 0)));
    }

    [Test]
    static void OnlyChairLiftsAreRidden()
    {
        Assert.True(LiftKinds.IsChairLift("chairlift_chair(Clone)", 2.7f, 77));
        Assert.True(!LiftKinds.IsChairLift("tbar_hanger(Clone)", 52.2f, 66), "a T-bar, however wide its bounds");
        Assert.True(!LiftKinds.IsChairLift("rope_tow_handle", 0.3f, 40));
        Assert.True(!LiftKinds.IsChairLift("chairlift_chair(Clone)", 2.7f, 0), "a lift with no carriers");
        Assert.True(LiftKinds.IsChairLift("carrier", 2.4f, 20), "an unnamed carrier as wide as a chair");
    }

    [Test]
    static void TheSittingPoseBendsBothLegs()
    {
        var legs = SitPose.Legs(85, 55).ToDictionary(p => p.Part, p => p.Value);
        Assert.True(Near(legs[0], 0.85f) && Near(legs[3], 0.85f), "hips");
        Assert.True(Near(legs[1], 0.45f) && Near(legs[4], 0.45f), "knees: 55% bent is 0.45 of straight");
        Assert.True(Near(legs[2], 0.5f) && Near(legs[5], 0.5f), "feet relaxed");
        Assert.True(SitPose.Legs(150, -20).All(p => p.Value is >= 0 and <= 1), "clamped to the joints' range");
    }

    [Test]
    static void ThePhysicsStepLeadsTheSeatAtTheChairsSpeed()
    {
        var at = Hold.SeatAt(new(0, 10, 0), new(2, 0, 0), 0.02f);
        Assert.True(Near(at.X, 0.04f) && Near(at.Y, 10));
        // A stale frame doesn't fling the rider ahead.
        Assert.True(Near(Hold.SeatAt(default, new(2, 0, 0), 5).X, 0.2f));
        Assert.True(Near(Hold.SeatAt(default, new(2, 0, 0), -1).X, 0));
    }

    [Test]
    static void GettingOffWaitsForTheTopAndForTheRideToStart()
    {
        var top = new Vector3(100, 50, 0);
        Assert.True(Hold.ShouldGetOff(new(95, 40, 0), top, 12, 30));
        Assert.True(!Hold.ShouldGetOff(new(50, 30, 0), top, 12, 30), "far from the top");
        Assert.True(!Hold.ShouldGetOff(new(95, 40, 0), top, 12, 2), "just got on (a short lift)");
    }

    [Test]
    static void OnlyYourOwnBodiesAreHeld()
    {
        // You sit at x = 0, a friend 0.6 m along the bench at x = 0.6.
        var parts = new RiderParts(new HashSet<nint> { 1, 2 }, new HashSet<nint> { 3 },
            [new(0, 1, 0), new(0, 0.3f, 0)], [new(0.6f, 1, 0), new(0.6f, 0.3f, 0)]);
        Assert.True(parts.Owns(1, new(0, 1, 0)), "your own part");
        Assert.True(!parts.Owns(3, new(0.6f, 1, 0)), "the friend's part");
        // A part is whoever's it is, wherever it ends up.
        Assert.True(!parts.Owns(3, new(0, 1, 0)), "the friend's part, even right by you");
        Assert.True(parts.Owns(2, new(0.6f, 0.3f, 0)), "your part, even by the friend");
        // Skis and poles aren't parts: they're the nearest rider's.
        Assert.True(parts.Owns(0, new(0.1f, 0, 0)), "your ski");
        Assert.True(!parts.Owns(0, new(0.5f, 0, 0)), "the friend's ski");
    }

    [Test]
    static void RidingAloneEverythingNearbyIsYours()
    {
        var parts = new RiderParts(new HashSet<nint> { 1 }, new HashSet<nint>(), [new(0, 1, 0)], []);
        Assert.True(parts.Owns(0, new(1.5f, 0, 0)));
        Assert.True(parts.Owns(7, new(1.5f, 0, 0)));
    }

    [Test]
    static void YawFacesTheGivenDirection()
    {
        var q = Vec.Yaw(new(1, 0, 0));
        // 90 degrees about Y: sin(45), cos(45).
        Assert.True(Near(q.Y, MathF.Sqrt(0.5f)) && Near(q.W, MathF.Sqrt(0.5f)) && Near(q.X, 0) && Near(q.Z, 0));
        Assert.True(Vec.FlatDirection(new(0, 5, 0)) is null, "straight up has no heading");
    }
}
