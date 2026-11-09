using Jibbers.Sdk;

static class LookupEfficiencyTests
{
    sealed class Lease(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    readonly record struct ObjectHandle(nint Ptr) : IGameObject;

    [Test]
    static void SceneObjectsAreRootedOnceAndRediscoveredAfterDeathOrInvalidation()
    {
        long now = 0;
        var found = 1;
        var searches = 0;
        var kept = 0;
        var released = 0;
        var dead = new HashSet<nint>();
        using var cache = new SceneObjectCache<ObjectHandle>(() => { searches++; return new(found); },
            obj => !dead.Contains(obj.Ptr), obj => { kept++; return new Lease(() => released++); }, () => now);
        for (var frame = 0; frame < 10000; frame++) Assert.Equal((nint)1, cache.Get()!.Value.Ptr);
        Assert.Equal(1, searches);
        Assert.Equal(1, kept);
        dead.Add(1); found = 2;
        Assert.Equal((nint)2, cache.Get()!.Value.Ptr);
        Assert.Equal(1, released);
        cache.Invalidate();
        Assert.Equal(2, released);
        Assert.Equal((nint)2, cache.Get()!.Value.Ptr);
        cache.Dispose();
        Assert.Equal(3, released);
        Assert.Equal(null, cache.Get());
        Assert.Equal(3, searches);
    }

    [Test]
    static void MissingSceneObjectsBackOffButSceneChangesPermitImmediateSearch()
    {
        long now = 0;
        var searches = 0;
        using var cache = new SceneObjectCache<ObjectHandle>(() => { searches++; return null; },
            _ => true, _ => new Lease(() => { }), () => now);
        for (now = 0; now < 1000; now++) Assert.Equal(null, cache.Get());
        Assert.Equal(1, searches);
        cache.Get();
        Assert.Equal(2, searches);
        cache.Invalidate(); cache.Get();
        Assert.Equal(3, searches);
    }

    [Test]
    static void TypingReadsCurrentFocusWithoutRediscoveringUnchangedSelection()
    {
        var resolutions = 0;
        var released = 0;
        var focus = true;
        var field = (nint)11;
        var dead = new HashSet<nint>();
        using var lookup = new FocusedInputLookup(selected => { resolutions++; return selected == 1 ? field : 0; },
            obj => !dead.Contains(obj), _ => focus, _ => new Lease(() => released++));
        for (var frame = 0; frame < 10000; frame++) Assert.True(lookup.Read(1));
        Assert.Equal(1, resolutions);
        focus = false;
        Assert.True(!lookup.Read(1));
        Assert.Equal(1, resolutions);
        dead.Add(11); field = 12; focus = true;
        Assert.True(lookup.Read(1));
        Assert.Equal(2, resolutions);
        Assert.Equal(2, released);
        Assert.True(!lookup.Read(2));
        Assert.True(!lookup.Read(2));
        Assert.Equal(3, resolutions);
        Assert.True(!lookup.Read(0));
        Assert.Equal(5, released);
    }

    [Test]
    static void GamepadCallersShareEmptySlotProbesAndDiscoverHotplug()
    {
        long now = 0;
        var calls = new int[4];
        var connected = false;
        var buttons = PadButton.None;
        var cache = new GamepadStateCache(index =>
        {
            calls[index]++;
            return connected && index == 2 ? new(true, index, 0, 0, 0, 0, 0, 0, buttons) : default;
        }, () => now);
        for (now = 0; now < 2000; now++)
        {
            cache.ReadAny();
            for (var index = 0; index < 4; index++) cache.Read(index);
        }
        Assert.True(calls.All(count => count == 1));
        connected = true;
        Assert.Equal(2, cache.ReadAny().Slot);
        var before = calls[2];
        buttons = PadButton.X;
        Assert.True(!cache.Read(2).Held(PadButton.X), "same-millisecond callers share a reading");
        now++;
        Assert.True(cache.ReadAny().Held(PadButton.X));
        Assert.Equal(before + 1, calls[2]);
        connected = false; now++;
        Assert.True(!cache.ReadAny().Connected);
        Assert.True(!cache.Read(-1).Connected && !cache.Read(4).Connected);
        Assert.Equal(4, calls[2]);
    }
}
