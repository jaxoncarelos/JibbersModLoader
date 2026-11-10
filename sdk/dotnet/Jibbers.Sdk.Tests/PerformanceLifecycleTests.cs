using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Jibbers.Sdk;

static unsafe class PerformanceLifecycleTests
{
    sealed class Mod : JibbersMod
    {
        public override string Id => "suspension_lifecycle_test";
        public override string Name => "Lifecycle test";
        public int Unloads;
        public override void OnUnload() => Unloads++;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Callback(int value);
    [Test] static void TypedNativeAbiAndManualDisabledCallbackBehaviorArePreserved()
    {
        var mod = new Mod { Store = new SettingsStore([], "Test", null) };
        mod.Store.Set("enabled", JsonValue.Create(false)!);
        ModContext.Add(mod);
        var invoked = 0;
        var wrapper = new NativeCallback<Callback>(value => { invoked++; return value + 7; }, mod.Id);
        wrapper.Original = value => value + 1;
        var callback = wrapper.Build();
        var native = (delegate* unmanaged[Cdecl]<int,int>)Marshal.GetFunctionPointerForDelegate(callback);
        Assert.Equal(12, native(5)); // Existing typed mods own their normal disabled-state cleanup.
        Assert.Equal(1, invoked);
        mod.PerformanceSuspended = true;
        Assert.Equal(6, native(5)); // Session suspension always bypasses the custom callback.
        Assert.Equal(1, invoked);
        GC.KeepAlive(callback);
    }
    [Test] static void QueuedWorkCannotTouchUnloadedResourcesButSynchronousCleanupStillRuns()
    {
        var mod = new Mod { PerformanceSuspended = true };
        ModContext.Add(mod);
        var touched = 0;
        Assert.Throws<OperationCanceledException>(() => MainThread.RunQueuedOwner(mod.Id, () => touched++));
        Assert.Equal(0, touched);
        MainThread.Enter(() => ModContext.With(mod, () => { MainThread.Run(() => touched++); return 0; }));
        Assert.Equal(1, touched);
    }
    [Test] static void NextLaunchLoadsTheSavedEnableSettingAfterASessionSuspension()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jibbers-performance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "mod.settings.json");
            var first = new Mod { Store = new SettingsStore([], "Test", file) };
            first.SetSetting("enabled", JsonValue.Create(true)!);
            var original = File.ReadAllText(file);
            first.PerformanceSuspended = true;
            Assert.True(!first.Enabled);
            var plugin = new ModPlugin(first);
            plugin.Handle(new JsonObject { ["cmd"] = "plugin.get" });
            Assert.Equal(original, File.ReadAllText(file));
            var next = new Mod { Store = new SettingsStore([], "Test", file) };
            Assert.True(next.Enabled);
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(directory, true);
        }
    }
    [Test] static void TimingScopesDoNotAllocateSamplesAfterWarmup()
    {
        ModTiming.Collecting = true;
        try
        {
            MainThread.Enter(() =>
            {
                for (var index = 0; index < 100; index++) { using var scope = ModTiming.Begin("allocation-test"); }
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 10000; index++) { using var scope = ModTiming.Begin("allocation-test"); }
                Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
                return 0;
            });
        }
        finally { ModTiming.Collecting = false; }
    }
    [Test] static void SessionCleanupAndLaterHostShutdownDoNotUnloadTheModTwice()
    {
        var mod = new Mod { PerformanceSuspended = true };
        MainThread.Enter(() =>
        {
            ModHost.UnloadMod(mod);
            ModHost.UnloadMod(mod);
            return 0;
        });
        Assert.Equal(1, mod.Unloads);
    }
}
