namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Combines physics and rider/replay snapshots behind the history buffer's scene contract.</summary>
internal sealed class NativeRewindScene(MujocoStateAccess physics, MujocoStateAccess.Context context, RiderStateCapture riderState, RiderStateCapture.LiveObjects liveObjects) : IRewindScene
{
    sealed record SceneIdentity(MujocoStateAccess.Context Physics, nint Rider, nint Clip);
    sealed class Snapshot(int stateLength) : IRewindSnapshot
    {
        public double[] PhysicsState { get; } = new double[stateLength];
        public List<RiderStateCapture.ObjectSnapshot> Objects { get; } = new();
    }

    public object Identity { get; } = new SceneIdentity(context, liveObjects.Rider, liveObjects.Clip);

    public IRewindSnapshot Capture(IRewindSnapshot? reusableSnapshot)
    {
        var snapshot = reusableSnapshot as Snapshot ?? new Snapshot(context.StateLength);
        physics.Capture(context, snapshot.PhysicsState);
        riderState.Capture(liveObjects, snapshot.Objects);
        return snapshot;
    }

    public void Restore(IRewindSnapshot frame)
    {
        if (frame is not Snapshot snapshot)
            throw new ArgumentException("Snapshot belongs to a different scene adapter.");
        physics.Restore(context, snapshot.PhysicsState);
        riderState.Restore(snapshot.Objects, liveObjects);
    }
}
