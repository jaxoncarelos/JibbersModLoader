// The Rust CollisionStream's tests (planner, step gate, native worker), run against the C# port. The
// native one loads the game's MuJoCo (pcore.dll) into this test process; it never touches the game.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CollisionStream;

static unsafe class CollisionStreamTests
{
    static Bound B(float x, float z, float r) => new(new Point(x, 5000, z), r);

    static Planner With(params Bound[] bounds)
    {
        var p = new Planner();
        for (var i = 0; i < bounds.Length; i++)
        {
            p.Add(i, bounds[i]);
        }
        return p;
    }

    [Test]
    static void InitialCollisionProtectsSelectedSpawnAndLivePlayer()
    {
        var anchors = new[] { new Point(3000, 0, 0), new Point(-1500, 0, 0) };
        Assert.True(Planner.KeepInitial(new[] { B(3020, 0, 10) }, anchors, 350));
        Assert.True(Planner.KeepInitial(new[] { B(-1490, 0, 10) }, anchors, 350));
        Assert.True(!Planner.KeepInitial(new[] { B(0, 0, 10) }, anchors, 350));
        Assert.True(Planner.KeepInitial(new[] { B(9000, 0, 10) }, Array.Empty<Point>(), 350));
    }

    [Test]
    static void VisitedCellsStayLoadedAndAreNotRequestedAgain()
    {
        var bounds = new[] { B(0, 0, 1), B(1000, 0, 1) };
        var p = With(bounds);
        var first = p.Next(bounds, default, 0.1f, 350, 4);
        Assert.True(first.SequenceEqual(new[] { (0, 0) }));
        p.Commit(first);
        p.Commit(p.Next(bounds, new Point(1000, 0, 0), 0.1f, 350, 4));
        Assert.Equal(2, p.Loaded.Count);
        Assert.Equal(0, p.Next(bounds, default, 0.1f, 350, 4).Count);
    }

    [Test]
    static void BoundsNotPivotsDetermineWhenALongRailIsNeeded() => Assert.True(B(500, 0, 200).Near(default, 350));

    [Test]
    static void RespawnResetsPredictionButLoadsDestinationImmediately()
    {
        var bounds = new[] { B(0, 0, 1), B(8000, 0, 1) };
        var p = With(bounds);
        p.Next(bounds, default, 0.1f, 350, 4);
        Assert.True(p.Next(bounds, new Point(8000, 0, 0), 0.1f, 350, 4).SequenceEqual(new[] { (40, 0) }));
        Assert.Equal(default(Point), p.Velocity);
    }

    [Test]
    static void TravelPrefetchesAhead()
    {
        var bounds = new[] { B(450, 0, 1) };
        var p = With(bounds);
        Assert.Equal(0, p.Next(bounds, default, 0.1f, 350, 4).Count);
        Assert.True(p.Next(bounds, new Point(10, 0, 0), 0.1f, 350, 4).SequenceEqual(new[] { (2, 0) }));
    }

    [Test]
    static void NegativeCoordinatesAndInvalidPositionsAreSafe()
    {
        Assert.Equal((-1, -2), B(-1, -201, 1).CellKey);
        Assert.Equal(0, new Planner().Next(Array.Empty<Bound>(), new Point(float.NaN, 0, 0), 0.1f, 350, 4).Count);
    }

    [Test]
    static void UncommittedCellsRetryAfterFailedCompilation()
    {
        var bounds = new[] { B(0, 0, 1) };
        var p = With(bounds);
        Assert.True(p.Next(bounds, default, 0.1f, 350, 4).SequenceEqual(p.Next(bounds, default, 0.1f, 350, 4)));
    }

    [Test]
    static void AHoldNeverBlocksADifferentSceneOrCompletionPoll()
    {
        var gate = new StepGate();
        gate.Hold(12);
        int physics = 0, other = 0, completions = 0;
        for (var frame = 0; frame < 5; frame++)
        {
            if (!gate.Held(12))
            {
                physics++;
            }
            if (!gate.Held(13))
            {
                other++;
            }
            completions++; // render-frame completion is independent of the gated integration call
            if (frame == 1)
            {
                gate.Release(12);
            }
        }
        Assert.Equal((3, 5, 5), (physics, other, completions));
    }

    [Test]
    static void StaleCleanupCannotReleaseTheNewScenesHold()
    {
        var gate = new StepGate();
        gate.Hold(12);
        gate.Hold(13);
        gate.Release(12);
        Assert.True(gate.Held(13));
        gate.Clear();
        Assert.True(!gate.Held(13));
        Assert.True(!gate.Held(0));
    }

    static string? Pcore()
    {
        var p = Environment.GetEnvironmentVariable("JIBBERS_TEST_PCORE")
            ?? @"C:\Program Files (x86)\Steam\steamapps\common\Jibbers Playtest\Jibbers_Data\Plugins\x86_64\pcore.dll";
        return File.Exists(p) ? p : null;
    }

    static Model Wait(Job job, Action step, DateTime deadline)
    {
        while (true)
        {
            step();
            if (job.Poll() is { } m)
            {
                return m;
            }
            Assert.True(DateTime.UtcNow < deadline, "background compile timed out");
            Thread.Sleep(1);
        }
    }

    /// <summary>The existing model keeps stepping while the worker compiles; ownership transfers once; errors and cancels are clean.</summary>
    [Test]
    static void NativeWorkerKeepsExistingModelRunning()
    {
        if (Pcore() is not { } path)
        {
            Console.WriteLine("      (no pcore.dll: skipped)");
            return;
        }
        Assert.Throws<FileNotFoundException>(() => Compiler.InitFrom(Path.Combine(Path.GetDirectoryName(path)!, "missing-pcore-test.dll")));
        Compiler.InitFrom(path);
        Compiler.InitFrom(path); // repeated initialisation reuses the retained library
        var module = NativeLibrary.Load(path);
        nint E(string n) => NativeLibrary.GetExport(module, n);
        var makeData = (delegate* unmanaged<nint, nint>)E("mj_makeData");
        var step = (delegate* unmanaged<nint, nint, void>)E("mj_step");
        var deleteData = (delegate* unmanaged<nint, void>)E("mj_deleteData");
        var deleteModel = (delegate* unmanaged<nint, void>)E("mj_deleteModel");

        using var old = Compiler.Build(Compiler.Api, Encoding.UTF8.GetBytes("<mujoco><worldbody><geom type='plane' size='20 20 .1'/><body pos='0 0 2'><freejoint/><geom type='sphere' size='.1'/></body></worldbody></mujoco>\0"));
        var oldModel = old.Take();
        var data = makeData(oldModel);
        Assert.True(data != 0);
        var xml = new StringBuilder("<mujoco><worldbody>");
        for (var i = 0; i < 6000; i++)
        {
            xml.Append($"<geom type='box' size='.1 .1 .1' pos='{i % 100 * 2} {i / 100 * 2} 0'/>");
        }
        xml.Append("</worldbody></mujoco>");
        var deadline = DateTime.UtcNow.AddSeconds(30);

        var steps = 0;
        var ready = Wait(Compiler.Start(xml.ToString()), () =>
        {
            step(oldModel, data);
            steps++;
        }, deadline);
        Assert.True(steps > 1, "existing physics must advance during native compilation");
        Console.WriteLine($"      existing model advanced {steps} steps during {ready.Elapsed.TotalMilliseconds:0} ms worker compile");
        var transferred = ready.Take();
        Assert.True(transferred != 0);
        Assert.Equal((nint)0, ready.Take()); // a prepared model can only transfer ownership once
        deleteModel(transferred);
        Compiler.Shutdown();

        // A scene integration hold must not gate render-frame job completion.
        var gate = new StepGate();
        gate.Hold(oldModel);
        int heldSteps = 0, polls = 0;
        using (Wait(Compiler.Start(xml.ToString()), () =>
        {
            if (!gate.Held(oldModel))
            {
                step(oldModel, data);
                heldSteps++;
            }
            polls++;
        }, deadline))
        {
            gate.Release(oldModel);
        }
        Assert.Equal(0, heldSteps);
        Assert.True(polls > 0 && !gate.Held(oldModel));
        step(oldModel, data);
        Compiler.Shutdown();

        var invalid = Compiler.Start("<mujoco><worldbody><geom type='unknown'/></worldbody></mujoco>");
        while (true)
        {
            step(oldModel, data);
            try
            {
                if (invalid.Poll() is not null)
                {
                    throw new Exception("invalid XML must not replace the running model");
                }
            }
            catch (InvalidOperationException e)
            {
                Assert.True(e.Message.Length > 0);
                break;
            }
            Assert.True(DateTime.UtcNow < deadline);
            Thread.Sleep(1);
        }
        Compiler.Shutdown();

        Compiler.Start(xml.ToString()).Cancel();
        Compiler.Shutdown(); // a discarded result frees itself without accessing any scene
        Assert.True(Compiler.Available());
        step(oldModel, data);
        deleteData(data);
        deleteModel(oldModel);
        NativeLibrary.Free(module);
    }
}
