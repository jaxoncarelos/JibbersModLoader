using Reshred;

static class BindingRefreshTests
{
    [Test]
    static void StableRidingDoesNotRepeatBindingInventory()
    {
        var policy = new BindingRefreshPolicy();
        var probes = 0;
        var topology = new object();
        Func<object> probe = () => { probes++; return topology; };
        var inventories = 0;
        for (long now = 0; now < 60000; now += 8)
            if (policy.ShouldRefresh(now, probe)) inventories++;
        Assert.Equal(1, inventories);
        Assert.True(probes < 81, "only the cheap topology check repeats");
    }

    [Test]
    static void RebindingHotplugAndSceneResetRequestFreshBindings()
    {
        var policy = new BindingRefreshPolicy();
        object topology = (1, false);
        Func<object> probe = () => topology;
        Assert.True(policy.ShouldRefresh(0, probe));
        Assert.True(!policy.ShouldRefresh(750, probe));
        topology = (2, false); // controller arrival
        Assert.True(policy.ShouldRefresh(1500, probe));
        topology = (2, true); // controls/pause context
        Assert.True(policy.ShouldRefresh(2250, probe));
        policy.Invalidate();
        Assert.True(policy.ShouldRefresh(2251, probe), "scene changes are immediate");
    }

    [Test]
    static void RetainedDisconnectedControllersCannotCauseInventoryScanLoops()
    {
        var policy = new BindingRefreshPolicy();
        Func<object> probe = () => 1;
        Assert.True(policy.ShouldRefresh(0, probe));
        policy.ObserveController(42, false);
        Assert.True(policy.ShouldRefresh(1, probe));
        for (var now = 2; now < 10000; now++)
        {
            policy.ObserveController(42, false);
            Assert.True(!policy.ShouldRefresh(now, probe));
        }
        policy.ObserveController(42, true);
        Assert.True(policy.ShouldRefresh(10000, probe));
        policy.ObserveController(42, true);
        Assert.True(!policy.ShouldRefresh(10001, probe));
    }

    [Test]
    static void InventoryFailureCannotBecomeAPerFrameRetryLoop()
    {
        var policy = new BindingRefreshPolicy();
        Func<object> probe = () => 1;
        Assert.True(policy.ShouldRefresh(0, probe));
        policy.RetryAfter(0);
        for (var now = 1; now < 5000; now++) Assert.True(!policy.ShouldRefresh(now, probe));
        Assert.True(policy.ShouldRefresh(5000, probe));
    }
}
