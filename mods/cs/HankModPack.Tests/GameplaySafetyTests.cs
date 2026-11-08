using System.Reflection;
using System.Runtime.CompilerServices;
using HankModPack;
using Jibbers.Sdk;
using RickBackflip;

static class GameplaySafetyTests
{
    [Test]
    static void CatalogDoesNotRegisterAnotherJoinSpawn()
    {
        var mods = typeof(HankModPackInfoMod).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(JibbersMod).IsAssignableFrom(type))
            .Select(type => (JibbersMod)RuntimeHelpers.GetUninitializedObject(type)).ToArray();
        Assert.Equal(9, mods.Length); // Eight features and one non-toggleable coordinator.
        Assert.Equal(mods.Length, mods.Select(mod => mod.Id).Distinct().Count());
        Assert.True(mods.All(mod => mod.Id != "join_spawn"));
        Assert.True(!mods.Single(mod => mod.Id == "hank_mod_pack_info").EnabledToggle);
    }

    [Test]
    static void OverlappingMenusKeepControlsMutedUntilLastCloses()
    {
        var state = new StateOverrides<int, bool>();
        var enabled = true;
        var first = state.Acquire(1, () => enabled, value => enabled = value, false);
        var second = state.Acquire(1, () => enabled, value => enabled = value, false);
        first.Dispose();
        Assert.True(!enabled);
        second.Dispose();
        Assert.True(enabled);
        second.Dispose();
        Assert.True(enabled);
    }

    [Test]
    static void ClosingMenusInReverseOrderRestoresOriginalDisabledControls()
    {
        var state = new StateOverrides<int, bool>();
        var enabled = false;
        var first = state.Acquire(1, () => enabled, value => enabled = value, false);
        var second = state.Acquire(1, () => enabled, value => enabled = value, true);
        Assert.True(enabled);
        second.Dispose();
        Assert.True(!enabled);
        first.Dispose();
        Assert.True(!enabled);
    }

    [Test]
    static void FailedControlOverrideDoesNotLeaveAnOwnerBehind()
    {
        var state = new StateOverrides<int, bool>();
        Assert.Throws<InvalidOperationException>(() => state.Acquire(1, () => false,
            _ => throw new InvalidOperationException(), true));
        var enabled = true;
        using (state.Acquire(1, () => enabled, value => enabled = value, false)) Assert.True(!enabled);
        Assert.True(enabled);
    }

    [Test]
    static void DifferentControllerMapsAreRestoredIndependently()
    {
        var state = new StateOverrides<int, bool>();
        var first = true; var second = false;
        var firstLease = state.Acquire(1, () => first, value => first = value, false);
        var secondLease = state.Acquire(2, () => second, value => second = value, true);
        firstLease.Dispose();
        Assert.True(first && second);
        secondLease.Dispose();
        Assert.True(!second);
    }

    [Test]
    static void CameraUpdateRunsExactlyOnceDespiteBrokenFeatureAndLogger()
    {
        var callbacks = new CameraCallbacks();
        var originalCalls = 0; var afterCalls = 0;
        callbacks.Register(new(new object(), () => true,
            _ => throw new InvalidOperationException(), _ => afterCalls++, _ => throw new Exception()));
        callbacks.Invoke(123, () => originalCalls++);
        Assert.Equal(1, originalCalls);
        Assert.Equal(1, afterCalls);
    }

    [Test]
    static void DisabledCameraFeaturesLeaveGameUpdateAndOtherFeaturesRunning()
    {
        var callbacks = new CameraCallbacks();
        var calls = new List<string>();
        callbacks.Register(new(new object(), () => false, _ => calls.Add("bad"), _ => calls.Add("bad"), _ => { }));
        callbacks.Register(new(new object(), () => true, _ => calls.Add("before"), _ => calls.Add("after"), _ => { }));
        callbacks.Invoke(1, () => calls.Add("game"));
        Assert.Equal("before,game,after", string.Join(',', calls));
    }

    [Test]
    static void UnloadingFeatureDoesNotRemoveOtherCameraCallbacks()
    {
        var callbacks = new CameraCallbacks(); var owner = new object(); var calls = 0;
        callbacks.Register(new(owner, () => true, _ => calls += 100, null, _ => { }));
        callbacks.Register(new(new object(), () => true, null, _ => calls++, _ => { }));
        callbacks.Remove(owner);
        callbacks.Invoke(1, () => calls++);
        Assert.Equal(2, calls);
    }

    [Test]
    static void OriginalCameraFailureIsNotRetried()
    {
        var callbacks = new CameraCallbacks(); var calls = 0;
        Assert.Throws<InvalidOperationException>(() => callbacks.Invoke(1, () => { calls++; throw new InvalidOperationException(); }));
        Assert.Equal(1, calls);
    }

    [Test]
    static void ReplayPoseRunsBeforeCameraEffectsRegardlessOfRegistrationOrder()
    {
        var callbacks = new CameraCallbacks(); var calls = new List<string>();
        callbacks.Register(new(new object(), () => true, _ => calls.Add("undo shake"),
            _ => calls.Add("shake"), _ => { }, 20));
        callbacks.Register(new(new object(), () => true, null, _ => calls.Add("pose"), _ => { }, 10));
        callbacks.Invoke(1, () => calls.Add("game"));
        Assert.Equal("undo shake,game,pose,shake", string.Join(',', calls));
    }

    [Test]
    static void CameraChainPreservesOtherModAndGameUpdates()
    {
        var pack = new CameraCallbacks(); var other = new CameraCallbacks(); var calls = new List<string>();
        other.Register(new(new object(), () => true, null, _ => calls.Add("other mod"), _ => { }));
        pack.Register(new(new object(), () => true, null, _ => calls.Add("pack"), _ => { }));
        pack.Invoke(1, () => other.Invoke(1, () => calls.Add("game")));
        Assert.Equal("game,other mod,pack", string.Join(',', calls));
    }

    [Test]
    static void SlowSettingWriteCannotRunAfterRestoration()
    {
        var queue = new BackgroundActions(); var calls = new List<string>();
        using var gate = new ManualResetEventSlim();
        var change = queue.Enqueue(() => { gate.Wait(); calls.Add("change"); });
        var restore = queue.Enqueue(() => calls.Add("restore"));
        Assert.True(!restore.IsCompleted);
        gate.Set();
        Assert.True(restore.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("change,restore", string.Join(',', calls));
    }

    [Test]
    static void FailedSettingWriteDoesNotSkipRestoration()
    {
        var queue = new BackgroundActions(); var restored = false;
        var change = queue.Enqueue(() => throw new InvalidOperationException());
        var restore = queue.Enqueue(() => restored = true);
        Assert.True(restore.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(restored);
        Assert.True(change.Exception is not null);
    }

    [Test]
    static void CleanupFailureCannotStrandControlsOrPausedTime()
    {
        var calls = new List<string>();
        CleanupActions.Run(_ => throw new Exception("logger also unavailable"),
            () => throw new Exception("panel destroyed"), () => calls.Add("controls"),
            () => calls.Add("clock"), () => calls.Add("cursor"));
        Assert.Equal("controls,clock,cursor", string.Join(',', calls));
    }

    [Test]
    static void FeatureCannotBypassDisableAndUnloadCleanup()
    {
        foreach (var name in new[] { nameof(PackPart.OnSettingChanged), nameof(PackPart.OnUnload) })
            Assert.True(typeof(PackPart).GetMethod(name)!.IsFinal);
    }

    [Test]
    static void TrickRulesDoNotMistakeDoubleForSingleRotation()
    {
        var rule = new EventRule { Tricks = new() { "Backflip" } };
        rule.Prepare(_ => { });
        Assert.True(rule.Matches("Back Flip"));
        Assert.True(!rule.Matches("Dub Backflip"));
        rule.Enabled = false;
        Assert.True(!rule.Matches("Backflip"));
    }

    [Test]
    static void InvalidTrickRegexDoesNotEscapeIntoGameplay()
    {
        var rule = new EventRule { Regex = "[" };
        rule.Prepare(_ => { });
        Assert.True(!rule.Matches("Backflip"));
    }

    [Test]
    static void AudioRejectsMalformedRatesBeforeAllocatingSampleBuffer()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            using (var stream = File.Create(file))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write("RIFF"u8); writer.Write(36); writer.Write("WAVEfmt "u8); writer.Write(16);
                writer.Write((short)1); writer.Write((short)2); writer.Write(int.MaxValue);
                writer.Write(0); writer.Write((short)4); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(0);
            }
            Assert.Throws<InvalidDataException>(() => SoundClip.Load(file));
        }
        finally { File.Delete(file); }
    }
}
