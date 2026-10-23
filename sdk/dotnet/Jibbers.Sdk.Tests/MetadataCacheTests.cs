using Jibbers.Sdk.Interop;

static class MetadataCacheTests
{
    [Test]
    static void MissingMetadataIsRetriedAfterAnAssemblyLoads()
    {
        var cache = new ResolvedMetadataCache<string, int>(value => value != 0);
        var available = false;
        var lookups = 0;
        int Resolve(string _) { lookups++; return available ? 42 : 0; }
        Assert.Equal(0, cache.GetOrResolve("Camera", Resolve));
        available = true;
        Assert.Equal(42, cache.GetOrResolve("Camera", Resolve));
        Assert.Equal(42, cache.GetOrResolve("Camera", Resolve));
        Assert.Equal(2, lookups);
    }

    [Test]
    static void ClassAndArgumentCountKeepOverloadsSeparate()
    {
        var cache = new ResolvedMetadataCache<(int Class, string Name, int Count), int>(value => value != 0);
        Assert.Equal(10, cache.GetOrResolve((1, "Set", 1), _ => 10));
        Assert.Equal(20, cache.GetOrResolve((1, "Set", 2), _ => 20));
        Assert.Equal(30, cache.GetOrResolve((2, "Set", 1), _ => 30));
        Assert.Equal(10, cache.GetOrResolve((1, "Set", 1), _ => throw new Exception("already resolved")));
    }

    [Test]
    static void WarmLookupsDoNotAllocatePerFrame()
    {
        var cache = new ResolvedMetadataCache<string, int>(value => value != 0);
        Func<string, int> lookup = static _ => 42;
        for (var warmup = 0; warmup < 100; warmup++) cache.GetOrResolve("RenderSettings", lookup);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = 0;
        for (var frame = 0; frame < 10_000; frame++) result = cache.GetOrResolve("RenderSettings", lookup);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(42, result);
        Assert.True(allocated < 1024, $"10,000 warm lookups allocated {allocated} bytes");
    }

    [Test]
    static void ConcurrentReadersShareResolvedMetadata()
    {
        var cache = new ResolvedMetadataCache<string, int>(value => value != 0);
        Parallel.For(0, 100, _ => Assert.Equal(42, cache.GetOrResolve("Camera", static _ => 42)));
        Assert.Equal(42, cache.GetOrResolve("Camera", _ => throw new Exception("cached")));
    }
}
