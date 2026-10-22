namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Owns bounded snapshot storage and backward playback; knows nothing about native physics or UI.</summary>
internal sealed class RewindHistory(Action<string> log)
{
    readonly object synchronization = new();
    readonly LinkedList<IRewindSnapshot> frames = new();
    object? sceneIdentity;
    float fractionalPlaybackSteps;
    volatile bool rewinding;
    public bool IsRewinding { get => rewinding; set => rewinding = value; }

    public int Count
    {
        get
        {
            if (!Monitor.TryEnter(synchronization))
                return 0;
            try
            {
                return frames.Count;
            }
            finally
            {
                Monitor.Exit(synchronization);
            }
        }
    }

    public void Advance(IRewindScene scene, float fixedStepSeconds, float playbackSpeed)
    {
        if (!Monitor.TryEnter(synchronization))
            return;
        try
        {
            if (!Equals(sceneIdentity, scene.Identity))
            {
                frames.Clear();
                fractionalPlaybackSteps = 0;
                sceneIdentity = scene.Identity;
            }

            if (IsRewinding)
                RestoreEarlierFrame(scene, playbackSpeed);
            else
                CaptureNextFrame(scene, Rewind.Capacity(fixedStepSeconds));
        }
        finally
        {
            Monitor.Exit(synchronization);
        }
    }

    void CaptureNextFrame(IRewindScene scene, int capacity)
    {
        fractionalPlaybackSteps = 0;
        IRewindSnapshot? reusableSnapshot = null;
        if (frames.Count >= capacity)
        {
            reusableSnapshot = frames.First!.Value;
            frames.RemoveFirst();
        }

        frames.AddLast(scene.Capture(reusableSnapshot));
    }

    void RestoreEarlierFrame(IRewindScene scene, float playbackSpeed)
    {
        var stepsBack = Rewind.ScrubSteps(ref fractionalPlaybackSteps, playbackSpeed);
        if (Rewind.ScrubTarget(frames.Count, stepsBack)is not { } targetIndex)
            return;
        while (frames.Count > targetIndex + 1)
            frames.RemoveLast();
        try
        {
            scene.Restore(frames.Last!.Value);
        }
        catch (Exception exception)
        {
            log("rewind restore: " + exception.Message);
        }
    }

    public void Clear()
    {
        lock (synchronization)
        {
            frames.Clear();
            sceneIdentity = null;
            fractionalPlaybackSteps = 0;
            IsRewinding = false;
        }
    }
}
