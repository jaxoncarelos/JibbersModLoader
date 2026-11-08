namespace HankModPack;

/// <summary>Orders setting changes and their restoration without waiting on the Unity thread.</summary>
internal sealed class BackgroundActions
{
    readonly object gate = new();
    Task tail = Task.CompletedTask;

    public Task Enqueue(Action action)
    {
        lock (gate)
            return tail = tail.ContinueWith(_ => action(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
