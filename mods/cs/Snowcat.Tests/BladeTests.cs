using Jibbers.Sdk;
using Snowcat;

static class BladeTests
{
    [Test]
    static void ThePushFollowsTheBladeTheWayItsDrawn()
    {
        // A point on the straight blade, swung round as the model is drawn, lands back on the blade in the push's frame.
        var pivot = Parts.BladePivot;
        foreach (var angle in new[] { -30f, -10f, 0f, 25f })
        {
            var onBlade = new Vector3(1.8f, 0, Plow.Edge);
            var (drawn, _) = Parts.Bend(new[] { onBlade }, null, new[] { 1f }, pivot with { Y = 0 }, new Parts.Pose(default, angle));
            var (bx, bz) = Plow.BladeFrame(drawn[0].X, drawn[0].Z, angle, pivot.Z);
            Assert.True(MathF.Abs(bx - onBlade.X) < 1e-4f && MathF.Abs(bz - onBlade.Z) < 1e-4f, $"{angle}: ({bx}, {bz})");
            var (cx, cz) = Plow.FromBlade(bx, bz, angle, pivot.Z);
            Assert.True(MathF.Abs(cx - drawn[0].X) < 1e-4f && MathF.Abs(cz - drawn[0].Z) < 1e-4f);
        }
        // Angled right, the blade's right end swings back (towards the snowcat).
        var (right, _) = Parts.Bend(new[] { new Vector3(2, 0, Plow.Edge) }, null, new[] { 1f }, pivot, new Parts.Pose(default, 20));
        Assert.True(right[0].Z < Plow.Edge, $"{right[0]}");
    }

    [Test]
    static void TiltingLiftsOneEnd()
    {
        var pivot = Parts.BladePivot;
        var ends = new[] { new Vector3(-2, pivot.Y, pivot.Z), new Vector3(2, pivot.Y, pivot.Z) };
        var (tilted, _) = Parts.Bend(ends, null, new[] { 1f, 1f }, pivot, new Parts.Pose(default, 0, 10));
        Assert.True(tilted[1].Y > pivot.Y + 0.3f && tilted[0].Y < pivot.Y - 0.3f, $"{tilted[0]} {tilted[1]}");
        // The arms only follow part of the way: half weight, half the lift.
        var (half, _) = Parts.Bend(new[] { ends[1] }, null, new[] { 0.5f }, pivot, new Parts.Pose(default, 0, 10));
        Assert.True(MathF.Abs((half[0].Y - pivot.Y) - (tilted[1].Y - pivot.Y) / 2) < 1e-4f);
        // Normals turn with it, and stay unit length.
        var (_, n) = Parts.Bend(ends, new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 0) }, new[] { 1f, 1f }, pivot, new Parts.Pose(default, 15, 5));
        Assert.True(n is not null && n.All(v => MathF.Abs(v.X * v.X + v.Y * v.Y + v.Z * v.Z - 1) < 1e-4f));
    }

    [Test]
    static void TheAngleAndTiltGoToOtherPlayers()
    {
        Assert.True(BladePacket.Decode(BladePacket.Encode(-22.5f, 7)) == (-22.5f, 7));
        Assert.True(BladePacket.Decode(BladePacket.Encode(float.NaN, 0)) is null);
        Assert.True(BladePacket.Decode(UndoPacket.Encode(1, 2)) is null);
        Assert.True(CatPacket.Decode(BladePacket.Encode(1, 1)) is null);
    }
}
