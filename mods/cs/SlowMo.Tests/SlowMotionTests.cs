using SlowMo;

static class SlowMotionTests
{
    static readonly SlowMotionOptions Toggle = new(SlowMotionMode.Toggle, 0.25, 0);
    static readonly SlowMotionOptions Hold = Toggle with { Mode = SlowMotionMode.Hold };

    static void Click(SlowMotion slow, SlowMotionOptions options)
    {
        slow.Update(true, 0.016, options);
        slow.Update(false, 0.016, options);
    }

    [Test]
    static void ClickTogglesOnAndOff()
    {
        var slow = new SlowMotion();
        Click(slow, Toggle);
        Assert.True(slow.Engaged);
        Assert.Equal(0.25, slow.Speed);
        Click(slow, Toggle);
        Assert.True(!slow.Engaged);
        Assert.Equal(1.0, slow.Speed);
    }

    [Test]
    static void TogglesWhenTheButtonIsReleased()
    {
        var slow = new SlowMotion();
        for (var i = 0; i < 30; i++)
            slow.Update(true, 0.016, Toggle);
        Assert.True(!slow.Engaged, "holding doesn't toggle yet");
        slow.Update(false, 0.016, Toggle);
        Assert.True(slow.Engaged);
    }

    [Test]
    static void ClickingBothSticksIsTheDronesNotOurs()
    {
        var slow = new SlowMotion();
        slow.Update(true, 0.016, Toggle);
        slow.Update(true, 0.016, Toggle, chord: true);
        slow.Update(false, 0.016, Toggle);
        Assert.True(!slow.Engaged, "the FPV drone's both-sticks click must not toggle slow motion");
        slow.Update(true, 0.016, Hold, chord: true);
        Assert.True(!slow.Engaged, "nor hold it");
        slow.Update(false, 0.016, Hold);
        Click(slow, Toggle);
        Assert.True(slow.Engaged, "a lone click still works afterwards");
    }

    [Test]
    static void HoldModeSlowsOnlyWhileHeld()
    {
        var slow = new SlowMotion();
        slow.Update(true, 0.016, Hold);
        Assert.True(slow.Slowing);
        slow.Update(false, 0.016, Hold);
        Assert.True(!slow.Engaged && !slow.Slowing);
    }

    [Test]
    static void EasesInAndOutOverTheEaseTime()
    {
        var slow = new SlowMotion();
        var eased = Toggle with { EaseSeconds = 0.3 };
        slow.Update(true, 0, eased);
        slow.Update(false, 0, eased);
        slow.Update(false, 0.15, eased);
        Assert.True(Math.Abs(slow.Speed - 0.625) < 1e-9, slow.Speed.ToString());
        slow.Update(false, 0.15, eased);
        Assert.Equal(0.25, slow.Speed);
        Click(slow, eased);
        Assert.True(!slow.Engaged && slow.Slowing, "still easing back out");
        slow.Update(false, 1, eased);
        Assert.Equal(1.0, slow.Speed);
        Assert.True(!slow.Slowing);
    }

    [Test]
    static void PressesWhilePausedAreIgnored()
    {
        var slow = new SlowMotion();
        slow.Update(true, 0.016, Toggle, paused: true);
        Assert.True(!slow.Engaged);
        slow.Update(true, 0.016, Toggle);
        Assert.True(!slow.Engaged, "a press that began in the pause menu must not toggle on unpause");
        slow.Update(false, 0.016, Toggle);
        Click(slow, Toggle);
        Assert.True(slow.Engaged);
        slow.Update(false, 5, Toggle with { EaseSeconds = 0.3 }, paused: true);
        Assert.Equal(0.25, slow.Speed);
    }

    [Test]
    static void SpeedIsClampedForTheStepLimiter()
    {
        var slow = new SlowMotion();
        Click(slow, Toggle with { Speed = 0 });
        Assert.Equal(SlowMotionOptions.MinSpeed, slow.Speed);
        slow.Reset();
        Click(slow, Toggle with { Speed = double.NaN });
        Assert.Equal(1.0, slow.Speed);
        Assert.True(!slow.Slowing);
    }

    [Test]
    static void ResetReturnsToNormalAndWaitsForRelease()
    {
        var slow = new SlowMotion();
        slow.Update(true, 0.016, Toggle);
        slow.Reset();
        Assert.True(!slow.Engaged && slow.Speed == 1);
        slow.Update(true, 0.016, Toggle);
        Assert.True(!slow.Engaged);
    }
}
