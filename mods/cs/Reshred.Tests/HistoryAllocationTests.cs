using Reshred;

static class HistoryAllocationTests
{
    sealed class State : IRewindSnapshot { }
    sealed class Scene : IRewindScene
    {
        public object Identity { get; } = new object();
        public IRewindSnapshot Capture(IRewindSnapshot? reusable) => reusable ?? new State();
        public void Restore(IRewindSnapshot snapshot) { }
    }

    [Test]
    static void WarmRecordingReusesHistoryNodesAndSnapshots()
    {
        var history = new RewindHistory(_ => { });
        var scene = new Scene();
        for (var i = 0; i < 3000; i++) history.Advance(scene, 0.003f, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20000; i++) history.Advance(scene, 0.003f, 1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4096, $"warm history allocated {allocated} bytes");
        Assert.Equal(Rewind.Capacity(0.003f), history.Count);
    }
}
