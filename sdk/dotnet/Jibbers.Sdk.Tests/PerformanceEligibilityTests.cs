using Jibbers.Sdk;

static class PerformanceEligibilityTests
{
    sealed class Mod(string id, bool allow = true, bool toggle = true) : JibbersMod
    {
        public override string Id => id;
        public override string Name => id;
        public override bool AllowPerformanceSuspension => allow;
        public override bool EnabledToggle => toggle;
    }
    [Test] static void CoreServicesCoordinatorsOptOutAndDisabledModsAreProtected()
    {
        var guard = new PerformanceGuard();
        foreach (var id in new[] { "jibbers_play", "jibbers_party", "jibbers_performance", "async_content" })
        {
            var mod = new Mod(id) { Store = new SettingsStore([], id, null) };
            ModContext.Add(mod);
            Assert.True(mod.Enabled);
            Assert.True(!guard.Eligible(id), id);
        }
        foreach (var mod in new[] { new Mod("optout-test", false), new Mod("coordinator-test", true, false), new Mod("disabled-test") })
        {
            ModContext.Add(mod);
            Assert.True(!guard.Eligible(mod.Id));
        }
        var optional = new Mod("jibbers_optional_example") { Store = new SettingsStore([], "Optional", null) };
        ModContext.Add(optional);
        Assert.True(guard.Eligible(optional.Id), "optional third-party mods are not mistaken for core services by their prefix");
    }
}
