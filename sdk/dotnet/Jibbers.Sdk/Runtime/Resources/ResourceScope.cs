namespace Jibbers.Sdk;
/// <summary>A construction transaction: resources are released if construction fails before ownership transfers.</summary>
public sealed class ResourceScope : IDisposable
{
    readonly List<IDisposable> resources = new();
    bool completed;
    /// <summary>Releases <paramref name="resource"/> if construction fails; returns it unchanged.</summary>
    public T Own<T>(T resource)
        where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(completed, this);
        resources.Add(resource);
        return resource;
    }

    /// <summary>Construction succeeded: the caller now owns every resource and disposing the scope does nothing.</summary>
    public void TransferOwnership()
    {
        ObjectDisposedException.ThrowIf(completed, this);
        resources.Clear();
        completed = true;
    }

    /// <summary>Releases owned resources in reverse order unless ownership was transferred.</summary>
    public void Dispose()
    {
        if (completed)
            return;
        completed = true;
        var release = resources.AsEnumerable().Reverse().Select(resource => (Action)resource.Dispose).ToArray();
        resources.Clear();
        Cleanup.Run(release);
    }
}
