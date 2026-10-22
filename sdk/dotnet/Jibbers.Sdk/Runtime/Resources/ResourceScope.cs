namespace Jibbers.Sdk;
/// <summary>A construction transaction: resources are released if construction fails before ownership transfers.</summary>
internal sealed class ResourceScope : IDisposable
{
    readonly List<IDisposable> resources = new();
    bool completed;
    public T Own<T>(T resource)
        where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(completed, this);
        resources.Add(resource);
        return resource;
    }

    public void TransferOwnership()
    {
        ObjectDisposedException.ThrowIf(completed, this);
        resources.Clear();
        completed = true;
    }

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
