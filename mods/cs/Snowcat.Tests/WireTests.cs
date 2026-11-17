using Jibbers.Sdk;
using Snowcat;

static class WireTests
{
    [Test]
    static void ASnowcatSurvivesTheTripToAnotherPlayer()
    {
        var p = new CatPacket(CatPacket.MapId("Terrain@0,0/1000x1000"), new Vector3(-213.5f, -109.25f, 264.125f), 33, 4, -2, 5.5f, 12.25f, -3.5f, 0.75f, true, false);
        var back = CatPacket.Decode(p.Encode());
        Assert.True(back == p, $"{back}");
        Assert.True(CatPacket.Decode(new byte[CatPacket.Size]) is null);
        var bad = p with { Speed = float.NaN };
        Assert.True(CatPacket.Decode(bad.Encode()) is null);
    }

    [Test]
    static void TheDriverAndRunGoAlong()
    {
        var p = new CatPacket(7, new Vector3(1, 2, 3), 0, 0, 0, 0, 0, 0, 0, true, true, true, new Vector3(-4.5f, 6, 7.25f), 123456);
        var back = CatPacket.Decode(p.Encode());
        Assert.True(back == p, $"{back}");
    }

    [Test]
    static void UndoAndResetPacketsRoundTrip()
    {
        Assert.True(UndoPacket.Decode(UndoPacket.Encode(5, 99)) == (5, 99));
        Assert.True(UndoPacket.Decode(new byte[UndoPacket.Size]) is null);
        var reset = ResetPacket.Decode(ResetPacket.Encode(-3, "Snow 123!"));
        Assert.True(reset is { Map: -3, Password: "Snow 123!" }, $"{reset}");
        Assert.True(ResetPacket.Decode(UndoPacket.Encode(1, 2)) is null);
    }

    [Test]
    static void OnlyTheRightPasswordMatchesItsFingerprint()
    {
        var f = AdminPassword.Fingerprint("groomer");
        Assert.True(AdminPassword.IsFingerprint(f) && !f.Contains("groomer"));
        Assert.True(AdminPassword.Matches("groomer", f));
        Assert.True(AdminPassword.Matches("groomer", f.ToUpperInvariant()));
        Assert.True(!AdminPassword.Matches("Groomer", f));
        Assert.True(!AdminPassword.Matches("", f));
        Assert.True(!AdminPassword.Matches("groomer", ""));
    }

    [Test]
    static void AHelloSaysWhichMapAndWhere()
    {
        var back = HelloPacket.Decode(HelloPacket.Encode(-42, new Vector3(1.5f, -2, 300)));
        Assert.True(back is { Map: -42 } h && h.At.X == 1.5f && h.At.Y == -2 && h.At.Z == 300, $"{back}");
        Assert.True(HelloPacket.Decode(UndoPacket.Encode(1, 2)) is null && CatPacket.Decode(HelloPacket.Encode(1, default)) is null);
        Assert.True(BladePacket.Decode(HelloPacket.Encode(1, default)) is null && UndoPacket.Decode(HelloPacket.Encode(1, default)) is null);
    }

    [Test]
    static void BetweenUpdatesItTurnsTheShortWayRound()
    {
        var a = new CatPacket(1, new Vector3(0, 0, 0), 350, 0, 0, 0, 0, 0, 0, false, false);
        var b = a with { At = new Vector3(10, 0, 0), Yaw = 10 };
        var mid = CatPacket.Between(a, b, 0.5f);
        Assert.True(MathF.Abs(mid.At.X - 5) < 1e-4f);
        var yaw = (mid.Yaw % 360 + 360) % 360;
        Assert.True(MathF.Abs(yaw - 0) < 1e-3f || MathF.Abs(yaw - 360) < 1e-3f, $"yaw {mid.Yaw}");
        Assert.True(CatPacket.MapId("a") != CatPacket.MapId("b") && CatPacket.MapId("x") == CatPacket.MapId("x"));
    }
}
