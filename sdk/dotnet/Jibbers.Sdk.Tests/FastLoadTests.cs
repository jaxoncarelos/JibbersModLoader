// The Rust FastLoad's tests, run against the C# port. The native ones load the game's own MuJoCo
// (pcore.dll) into this test process; they never touch the running game. Set JIBBERS_TEST_PCORE to
// another pcore.dll, or skip when the game is not installed.

using System.Runtime.InteropServices;
using System.Text;
using FastLoad;

static unsafe class FastLoadTests
{
    [Test]
    static void AsynchronousImportKeepsScopeUntilPhysicsOrTimeout()
    {
        var start = DateTime.UtcNow;
        var c = new LoadClock();
        c.BeginImport(start);
        Assert.True(!c.Ready(start.AddSeconds(10)));
        Assert.True(c.Ready(start.AddSeconds(30)));
        c.BeginPhysics(start.AddSeconds(12));
        Assert.True(c.Ready(start.AddSeconds(12)));
    }

    [Test]
    static void TimingsSeparateImportAndPhysics()
    {
        var start = DateTime.UtcNow;
        var c = new LoadClock();
        c.BeginImport(start);
        c.BeginPhysics(start.AddSeconds(3));
        Assert.Equal((5000.0, 3000.0), ((double, double))(c.Durations(start.AddSeconds(5)) is var (a, b) ? (a!.Value, b!.Value) : default));
    }

    [Test]
    static void NestedRebuildKeepsTheStartOfTheFirstAttempt()
    {
        var now = DateTime.UtcNow;
        var c = new LoadClock();
        c.BeginImport(now);
        c.BeginPhysics(now.AddSeconds(2));
        c.BeginPhysics(now.AddSeconds(4));
        Assert.Equal(2000.0, c.Durations(now.AddSeconds(6)).BeforePhysics);
    }

    [Test]
    static void StandalonePhysicsRebuildHasNoInventedImportTime()
    {
        var now = DateTime.UtcNow;
        var c = new LoadClock();
        c.BeginPhysics(now);
        Assert.True(c.Ready(now));
        Assert.Equal((double?)null, c.Durations(now).Total);
        c.BeginImport(now);
        Assert.True(!c.Ready(now));
    }

    [Test]
    static void OnlySelfContainedXmlIsCacheable()
    {
        PhysicsCache.SelfContained("<mujoco><worldbody><geom type='plane' size='1 1 .1'/></worldbody></mujoco>");
        Assert.Throws<InvalidOperationException>(() => PhysicsCache.SelfContained("<mujoco><include file='x.xml'/></mujoco>"));
        Assert.Throws<InvalidOperationException>(() => PhysicsCache.SelfContained("<mujoco><asset><mesh file='a.stl'/></asset></mujoco>"));
        Assert.Throws<InvalidOperationException>(() => PhysicsCache.SelfContained("<mujoco><extension><plugin plugin='x'/></extension></mujoco>"));
        Assert.Throws<InvalidOperationException>(() => PhysicsCache.SelfContained("<!DOCTYPE m [<!ENTITY e 'x'>]><mujoco>&e;</mujoco>"));
    }

    [Test]
    static void DamagedCacheFilesAreRejected()
    {
        var key = new byte[32];
        key[0] = 7;
        Assert.Throws<InvalidDataException>(() => PhysicsCache.Decode(new byte[10], key));
        var bad = new byte[90];
        "JIBMJB01"u8.CopyTo(bad);
        key.CopyTo(bad, 8);
        BitConverter.GetBytes((ulong)10).CopyTo(bad, 72);
        Assert.Throws<InvalidDataException>(() => PhysicsCache.Decode(bad, key));
    }

    static string? Pcore()
    {
        var p = Environment.GetEnvironmentVariable("JIBBERS_TEST_PCORE")
            ?? @"C:\Program Files (x86)\Steam\steamapps\common\Jibbers Playtest\Jibbers_Data\Plugins\x86_64\pcore.dll";
        return File.Exists(p) ? p : null;
    }

    /// <summary>Compile, save, reload from the cache (a hit gives identical model bytes), step it, and fall back on a damaged entry.</summary>
    [Test]
    static void NativeCacheRoundTripWithTheGamesMujoco()
    {
        if (Pcore() is not { } path)
        {
            Console.WriteLine("      (no pcore.dll: skipped)");
            return;
        }
        var native = MujocoNative.Open(path);
        nint E(string n) => NativeLibrary.GetExport(native.Module, n);
        var parse = (delegate* unmanaged<byte*, nint, byte*, int, nint>)E("mj_parseXMLString");
        var compile = (delegate* unmanaged<nint, nint, nint>)E("mj_compile");
        var deleteSpec = (delegate* unmanaged<nint, void>)E("mj_deleteSpec");
        var deleteModel = (delegate* unmanaged<nint, void>)E("mj_deleteModel");
        var makeData = (delegate* unmanaged<nint, nint>)E("mj_makeData");
        var deleteData = (delegate* unmanaged<nint, void>)E("mj_deleteData");
        var step = (delegate* unmanaged<nint, nint, void>)E("mj_step");
        const string xml = "<mujoco><worldbody><geom type='plane' size='100 100 .1'/><body name='rider' pos='0 0 2'><freejoint/><geom type='sphere' size='.1'/></body></worldbody></mujoco>";
        var text = Encoding.UTF8.GetBytes(xml + "\0");
        var error = new byte[2048];
        nint Spec()
        {
            fixed (byte* t = text)
            fixed (byte* e = error)
            {
                var s = parse(t, 0, e, error.Length);
                Assert.True(s != 0, Encoding.UTF8.GetString(error).TrimEnd('\0'));
                return s;
            }
        }
        var dir = Path.Combine(Path.GetTempPath(), "jibbers-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PhysicsCache(native, dir, PhysicsCache.Fingerprint(path));
            var key = cache.Key(xml);
            Assert.Equal(0, (int)cache.Load(key));

            var first = Spec();
            var cold = compile(first, 0);
            Assert.True(cold != 0);
            var expected = native.Bytes(cold);
            cache.Store(key, cold);
            deleteModel(cold);
            deleteSpec(first);

            var cached = cache.Load(key);
            Assert.True(cached != 0, "cache miss after store");
            Assert.True(native.Bytes(cached).AsSpan().SequenceEqual(expected), "cached model differs");
            var data = makeData(cached);
            Assert.True(data != 0);
            for (var i = 0; i < 500; i++)
            {
                step(cached, data);
            }
            deleteData(data);
            deleteModel(cached);

            // A damaged entry fails to load (the detour then compiles normally and repairs it).
            var file = Directory.GetFiles(dir, "*.jmc").Single();
            File.WriteAllBytes(file, "broken"u8.ToArray());
            Assert.Throws<InvalidDataException>(() => cache.Load(key));
            var again = Spec();
            var repairedModel = compile(again, 0);
            cache.Store(key, repairedModel);
            deleteModel(repairedModel);
            deleteSpec(again);
            var repaired = cache.Load(key);
            Assert.True(repaired != 0 && native.Bytes(repaired).AsSpan().SequenceEqual(expected));
            deleteModel(repaired);

            // Different input, different key; the same input on another build, another key.
            Assert.True(!cache.Key(xml.Replace("2'", "3'")).AsSpan().SequenceEqual(key));
            Assert.True(!new PhysicsCache(native, dir, new byte[32]).Key(xml).AsSpan().SequenceEqual(key));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
