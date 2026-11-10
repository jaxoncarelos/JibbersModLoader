using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Jibbers.Sdk;

static class PerformanceProtectionTests
{
    sealed class Mod : JibbersMod
    {
        public override string Id => "performance_test";
        public override string Name => "Example mod";
    }
    static Dictionary<string,double> Cost(double value) => new() { ["example"] = value };

    [Test] static void SustainedAttributedStallsPauseOneModThenRespectCooldown()
    {
        var policy = new PerformancePolicy();
        string? selected = null;
        for (var second = 0; second <= 25; second++)
            selected = policy.Frame(second, 180, Cost(120), _ => true, true) ?? selected;
        Assert.Equal("example", selected);
        for (var second = 26; second < 37; second++)
            Assert.Equal<string?>(null, policy.Frame(second, 180, Cost(120), _ => true, true));
    }
    [Test] static void LoadingUnfocusedBaseGameStallsAndProtectedServicesAreNotBlamed()
    {
        foreach (var scenario in new[] { "loading", "unfocused", "base-game", "protected" })
        {
            var policy = new PerformancePolicy();
            for (var second = 0; second < 100; second++)
            {
                var elapsed = scenario == "loading" ? 2500 : 200;
                var cost = scenario == "base-game" ? 1 : 150;
                Assert.Equal<string?>(null, policy.Frame(second, elapsed, Cost(cost), _ => scenario != "protected", scenario != "unfocused"));
            }
        }
    }
    [Test] static void AHealthyFrameRateAndOneBadFrameDoNotDisableMods()
    {
        var policy = new PerformancePolicy();
        for (var second = 0; second < 80; second++)
            Assert.Equal<string?>(null, policy.Frame(second, second == 30 ? 300 : 8, Cost(second == 30 ? 250 : 2), _ => true, true));
    }
    [Test] static void SuspensionPreservesSettingsAndWaitsForNativeStackBeforeUnloadingOnce()
    {
        var mod = new Mod();
        mod.Store = new SettingsStore([], mod.Name, null);
        var saved = mod.Store.Values().ToJsonString();
        var disabled = 0; var unloaded = 0; var idle = false;
        var suspensions = new PerformanceSuspensions(_ => disabled++, _ => unloaded++, _ => idle, error => throw error);
        Assert.True(suspensions.Suspend(mod, 10, "Disabled for performance; returns next launch."));
        Assert.True(!mod.Enabled);
        Assert.Equal(saved, mod.Store.Values().ToJsonString());
        Assert.Equal(1, disabled);
        suspensions.Frame(11); idle = true; suspensions.Frame(11);
        Assert.Equal(0, unloaded);
        idle = false; suspensions.Frame(12); Assert.Equal(0, unloaded);
        idle = true; suspensions.Frame(13); suspensions.Frame(14);
        Assert.Equal(1, unloaded);
        Assert.True(!suspensions.Suspend(mod, 15, "Again"));
        var plugin = new ModPlugin(mod);
        Assert.Equal(false, plugin.Handle(new JsonObject { ["cmd"] = "plugin.get" })["values"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(saved, mod.Store.Values().ToJsonString());
        Assert.Throws<ArgumentException>(() => plugin.Handle(new JsonObject { ["cmd"] = "plugin.enable", ["on"] = true }));
        Assert.True(!new Mod().PerformanceSuspended, "a new game/mod instance is not suspended");
    }
    [Test] static void FailedNativeDisableDoesNotCommitSuspensionOrSavedChanges()
    {
        var mod = new Mod(); var failed = 0;
        var suspensions = new PerformanceSuspensions(_ => throw new InvalidOperationException(), _ => throw new Exception("must not unload"), _ => true, _ => failed++);
        Assert.True(!suspensions.Suspend(mod, 0, "Reason"));
        Assert.True(!mod.PerformanceSuspended);
        Assert.Equal(1, failed);
        suspensions.Frame(10);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Operation(int value);
    [Test] static void TypedCallbackPreservesReturnValuesAndExcludesOriginalGameTime()
    {
        var prior = ModTiming.Timestamp;
        long now = 0; var result = new Dictionary<string,double>();
        try
        {
            ModTiming.Collecting = true; ModTiming.Timestamp = () => now;
            MainThread.Enter(() =>
            {
                Operation original = NativeCallback<Operation>.ExcludeOriginal(value => { now += Stopwatch.Frequency; return value + 1; }, "original-test");
                var wrapper = new NativeCallback<Operation>(value => { now += Stopwatch.Frequency / 100; return original(value) + 2; }, "original-test");
                wrapper.Original = original;
                Assert.Equal(8, wrapper.Build()(5));
                return 0;
            });
            ModTiming.Drain(result);
            Assert.True(result["original-test"] is >= 9 and <= 11, "the one-second original is not charged to the mod");
            Assert.True(ModTiming.Idle("original-test"));
        }
        finally { ModTiming.Timestamp = prior; ModTiming.Collecting = false; }
    }
    [Test] static void NestedFeatureCallbacksAreNotChargedToTheCoordinator()
    {
        var prior = ModTiming.Timestamp; long now = 0; var result = new Dictionary<string,double>();
        try
        {
            ModTiming.Collecting = true; ModTiming.Timestamp = () => now;
            MainThread.Enter(() =>
            {
                ModContext.Run("coordinator-test", () =>
                {
                    now += Stopwatch.Frequency / 1000;
                    ModContext.Run("feature-test", () => now += Stopwatch.Frequency / 10);
                });
                return 0;
            });
            ModTiming.Drain(result);
            Assert.True(result["coordinator-test"] < 2);
            Assert.True(result["feature-test"] is > 99 and < 101);
        }
        finally { ModTiming.Timestamp = prior; ModTiming.Collecting = false; }
    }
}
