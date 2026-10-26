// The Rust Reshred's tests, run against the C# port.

using Reshred;

static class ReshredTests
{
    [Test]
    static void ScrubSpeedCarriesFractions()
    {
        var acc = 0f;
        var steps = Enumerable.Range(0, 4).Select(_ => Rewind.ScrubSteps(ref acc, 1.5f)).ToArray();
        Assert.True(steps.SequenceEqual(new[] { 1, 2, 1, 2 }), string.Join(",", steps));
        acc = 0;
        Assert.Equal(2, Enumerable.Range(0, 4).Sum(_ => Rewind.ScrubSteps(ref acc, 0.5f)));
        var z = 0f;
        Assert.Equal(0, Rewind.ScrubSteps(ref z, -3));
        z = 0;
        Assert.Equal(2, Rewind.ScrubSteps(ref z, 2));
    }

    [Test]
    static void ScrubStopsAtTheOldestFrame()
    {
        Assert.Equal(null, Rewind.ScrubTarget(0, 2));
        Assert.Equal(9, Rewind.ScrubTarget(10, 0));
        Assert.Equal(6, Rewind.ScrubTarget(10, 3));
        Assert.Equal(0, Rewind.ScrubTarget(10, 99));
        Assert.Equal(0, Rewind.ScrubTarget(1, 5));
    }

    [Test]
    static void SixSecondsOfHistoryIsKept()
    {
        Assert.Equal(6.0f, Rewind.History);
        Assert.Equal(2001, Rewind.Capacity(0.003f));
    }

    [Test]
    static void LeavesNonGameplayContextsUntouched()
    {
        foreach (var c in new[] { "Gameplay", "Ragdoll", "Skiing", "In Game" })
        {
            Assert.True(RewiredPolicy.GameplayCategory(c), c);
        }
        foreach (var c in new[] { "UI", "Replay", "Replay Gameplay", "Menu", "Default", "Default - Debug", "SpawnSelector", "", "Photo Mode" })
        {
            Assert.True(!RewiredPolicy.GameplayCategory(c), c);
        }
    }

    [Test]
    static void NeverGuessesXOnUnknownOrNintendoControllers()
    {
        Assert.True(RewiredPolicy.WestName("X", "Xbox One Controller"));
        Assert.True(RewiredPolicy.WestName("X Button", "XInput Gamepad"));
        Assert.True(RewiredPolicy.WestName("Square", "DualShock 4"));
        Assert.True(RewiredPolicy.WestName("Button West", "Generic Gamepad"));
        Assert.True(!RewiredPolicy.WestName("X", "Nintendo Switch Pro Controller"));
        Assert.True(!RewiredPolicy.WestName("X", "Generic Gamepad"));
        Assert.True(!RewiredPolicy.WestName("Button 2", "Xbox One Controller"));
        Assert.True(!RewiredPolicy.WestName("Left Stick X", "Xbox One Controller"));
    }
}
