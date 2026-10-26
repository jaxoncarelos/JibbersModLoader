using Reshred;

static class RewindRuntimeTests
{
    static readonly RewindOptions Options = new(true, 0, false, 0.2f, 1, true);
    sealed class Snapshot : IRewindSnapshot
    {
        public int Value;
    }

    sealed class Scene : IRewindScene
    {
        public object Identity { get; set; } = new object ();

        public int Value, Allocations, Restored;
        public bool FailCapture, FailRestore;
        public IRewindSnapshot Capture(IRewindSnapshot? reusable)
        {
            if (FailCapture)
                throw new InvalidOperationException("capture");
            var snapshot = reusable as Snapshot;
            if (snapshot is null)
            {
                snapshot = new();
                Allocations++;
            }

            snapshot.Value = Value++;
            return snapshot;
        }

        public void Restore(IRewindSnapshot snapshot)
        {
            if (FailRestore)
                throw new InvalidOperationException("restore");
            Restored = ((Snapshot)snapshot).Value;
        }
    }

    sealed class Loop : IRewindGameLoop
    {
        public float FixedStepSeconds => 0.1f;
        public bool IsPaused { get; set; }

        public Action<IRewindScene> Physics = null !;
        public Action<float> Frame = null !;
        public bool Disposed, FailStart;
        public void Start(Action<IRewindScene> physics, Action<float> frame)
        {
            Physics = physics;
            Frame = frame;
            if (FailStart)
                throw new InvalidOperationException();
        }

        public void Dispose() => Disposed = true;
    }

    sealed class Controls : IRewindInput
    {
        public RewindInputStatus Status => new(0, 0, null);

        public bool Held, Disposed;
        public int Restorations;
        public RewindInputState Read(RewindOptions options) => new(Held, false);
        public void RestoreBindings() => Restorations++;
        public void Dispose() => Disposed = true;
    }

    sealed class Indicator : IRewindIndicator
    {
        public string? Message;
        public bool Fail, Disposed;
        public void Show(string message)
        {
            Message = message;
            if (Fail)
                throw new InvalidOperationException("label");
        }

        public void Hide()
        {
            Message = null;
            if (Fail)
                throw new InvalidOperationException("label");
        }

        public void Dispose() => Disposed = true;
    }

    [Test]
    static void HistoryIsBoundedAndReusesSnapshots()
    {
        var scene = new Scene();
        var history = new RewindHistory(_ =>
        {
        });
        for (var step = 0; step < 20; step++)
            history.Advance(scene, 1, 1);
        Assert.Equal(7, history.Count);
        Assert.Equal(7, scene.Allocations);
        history.IsRewinding = true;
        history.Advance(scene, 1, 100);
        Assert.Equal(1, history.Count);
        Assert.Equal(13, scene.Restored);
    }

    [Test]
    static void FractionalPlaybackDiscardsFutureBeforeRecordingAgain()
    {
        var scene = new Scene();
        var history = new RewindHistory(_ =>
        {
        });
        for (var step = 0; step < 4; step++)
            history.Advance(scene, 1, 1);
        history.IsRewinding = true;
        history.Advance(scene, 1, 0.5f);
        Assert.Equal(3, scene.Restored);
        history.Advance(scene, 1, 0.5f);
        Assert.Equal(2, scene.Restored);
        Assert.Equal(3, history.Count);
        history.IsRewinding = false;
        history.Advance(scene, 1, 1);
        Assert.Equal(4, history.Count);
        history.IsRewinding = true;
        history.Advance(scene, 1, 1);
        Assert.Equal(2, scene.Restored);
    }

    [Test]
    static void HeldInputSendsProgressToIndicatorAndReleaseHidesIt()
    {
        var loop = new Loop();
        var controls = new Controls();
        var indicator = new Indicator();
        using var runtime = new RewindRuntime(loop, controls, indicator, () => Options, _ => { });
        runtime.Start();
        controls.Held = true;
        loop.Frame(0.1f);
        Assert.True(indicator.Message?.Contains("waiting") == true);
        var scene = new Scene();
        for (var frame = 0; frame < 20; frame++) loop.Physics(scene);
        loop.Frame(0.1f);
        Assert.True(indicator.Message?.Contains("Hold to rewind") == true);
        loop.Frame(0.1f);
        Assert.True(indicator.Message?.Contains("Rewinding") == true);
        controls.Held = false;
        loop.Frame(0.1f);
        Assert.True(indicator.Message is null);
    }

    [Test]
    static void NewSceneInvalidatesOldHistory()
    {
        var scene = new Scene();
        var history = new RewindHistory(_ =>
        {
        });
        history.Advance(scene, 1, 1);
        history.Advance(scene, 1, 1);
        scene.Identity = new object ();
        history.Advance(scene, 1, 1);
        Assert.Equal(1, history.Count);
    }

    [Test]
    static void CaptureAndRestoreFailuresReleaseHistoryLock()
    {
        var logs = new List<string>();
        var scene = new Scene();
        var history = new RewindHistory(logs.Add);
        scene.FailCapture = true;
        Assert.Throws<InvalidOperationException>(() => history.Advance(scene, 1, 1));
        scene.FailCapture = false;
        history.Advance(scene, 1, 1);
        // Check from another thread: Monitor is reentrant on the capturing thread.
        Assert.Equal(1, Task.Run(() => history.Count).GetAwaiter().GetResult());
        scene.FailRestore = true;
        history.IsRewinding = true;
        history.Advance(scene, 1, 1);
        Assert.Equal(1, history.Count);
        Assert.Equal(1, logs.Count);
        scene.FailRestore = false;
        history.Advance(scene, 1, 1);
        Assert.Equal(0, scene.Restored);
    }

    [Test]
    static void HoldThresholdAndReleaseReportRewoundTime()
    {
        var playback = new RewindPlayback();
        Assert.True(!playback.Update(new(true, false), 20, 0.1f, 0.1f, false, Options, null).IsRewinding);
        Assert.True(playback.Update(new(true, false), 20, 0.1f, 0.1f, false, Options, null).IsRewinding);
        var release = playback.Update(new(false, false), 15, 0.1f, 0.1f, false, Options, null);
        Assert.True(!release.IsRewinding);
        Assert.Equal(0.5f, release.ResumedSecondsAgo);
    }

    [Test]
    static void TypingPressIsIgnoredUntilRelease()
    {
        var playback = new RewindPlayback();
        playback.Update(new(true, true), 20, 0.1f, 0.25f, false, Options, null);
        Assert.True(!playback.Update(new(true, false), 20, 0.1f, 0.25f, false, Options, null).IsRewinding);
        playback.Update(new(false, false), 20, 0.1f, 0, false, Options, null);
        Assert.True(playback.Update(new(true, false), 20, 0.1f, 0.25f, false, Options, null).IsRewinding);
    }

    [Test]
    static void IndicatorCanBeDisabledWithoutDisablingPlayback()
    {
        var playback = new RewindPlayback();
        var feedback = playback.Update(new(true, false), 20, 0.1f, 0.25f, false, Options with { ShowIndicator = false }, null);
        Assert.True(feedback.IsRewinding);
        Assert.Equal<string?>(null, feedback.Message);
        Assert.True(!playback.Update(new(true, false), 20, 0.1f, 0.25f, true, Options, null).IsRewinding);
    }

    [Test]
    static void BrokenIndicatorDoesNotStrandPlaybackOrRepeatErrors()
    {
        var loop = new Loop();
        var input = new Controls
        {
            Held = true
        };
        var indicator = new Indicator
        {
            Fail = true
        };
        var logs = new List<string>();
        var runtime = new RewindRuntime(loop, input, indicator, () => Options, logs.Add);
        runtime.Start();
        var scene = new Scene();
        for (var step = 0; step < 10; step++)
            loop.Physics(scene);
        loop.Frame(0.25f);
        Assert.True(runtime.Status.IsRewinding);
        input.Held = false;
        loop.Frame(0.1f);
        Assert.True(!runtime.Status.IsRewinding);
        Assert.Equal(1, logs.Count(message => message.StartsWith("indicator")));
        Assert.Throws<AggregateException>(runtime.Dispose);
        Assert.True(loop.Disposed && input.Disposed && indicator.Disposed);
    }

    [Test]
    static void DisabledOptionsStopCaptureAndIgnoreHeldInput()
    {
        var options = Options;
        var loop = new Loop();
        var input = new Controls
        {
            Held = true
        };
        using var runtime = new RewindRuntime(loop, input, new Indicator(), () => options, _ =>
        {
        });
        runtime.Start();
        var scene = new Scene();
        for (var step = 0; step < 10; step++)
            loop.Physics(scene);
        options = options with
        {
            Enabled = false
        };
        loop.Physics(scene);
        loop.Frame(0.25f);
        Assert.Equal(10, runtime.Status.FrameCount);
        Assert.True(!runtime.Status.IsRewinding);
    }

    [Test]
    static void StartFailureAndStaleCallbacksReleaseAllResources()
    {
        var loop = new Loop
        {
            FailStart = true
        };
        var input = new Controls();
        var indicator = new Indicator();
        var runtime = new RewindRuntime(loop, input, indicator, () => Options, _ =>
        {
        });
        Assert.Throws<InvalidOperationException>(runtime.Start);
        Assert.True(loop.Disposed && input.Disposed && indicator.Disposed);
        loop.Physics(new Scene());
        loop.Frame(0.25f);
        Assert.Equal(0, runtime.Status.FrameCount);
        runtime.Dispose();
        Assert.Equal(1, input.Restorations);
    }
}
