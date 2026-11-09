namespace Jibbers.Sdk;

/// <summary>
/// Keeps a discovered Unity object alive instead of searching the whole scene on every update.
/// Main thread only. Invalidate on scene changes; dispose when the owning mod unloads.
/// Missing objects are retried at most once per second.
/// </summary>
public sealed class SceneObjectCache<T> : IDisposable where T : struct, IGameObject
{
    readonly Func<T?> find;
    readonly Func<T, bool> alive;
    readonly Func<T, IDisposable> keep;
    readonly Func<long> clock;
    T? value;
    IDisposable? lease;
    long nextSearch = long.MinValue;
    bool disposed;

    /// <summary>Creates a cache around an existing friendly binding's lookup.</summary>
    public SceneObjectCache(Func<T?> find) : this(find,
        obj => obj.Ptr != 0 && !Unity.IsNull(obj.Ptr), obj => new GameRef(obj.Ptr), () => Environment.TickCount64) { }

    internal SceneObjectCache(Func<T?> find, Func<T, bool> alive, Func<T, IDisposable> keep, Func<long> clock)
    { this.find = find; this.alive = alive; this.keep = keep; this.clock = clock; }

    /// <summary>The live cached object, or null while the scene has none.</summary>
    public T? Get()
    {
        if (disposed) return null;
        if (value is { } existing && alive(existing)) return existing;
        if (value.HasValue) nextSearch = long.MinValue;
        lease?.Dispose(); lease = null; value = null;
        var now = clock();
        if (now < nextSearch) return null;
        nextSearch = now + 1000;
        if (find() is not { } found || !alive(found)) return null;
        lease = keep(found);
        value = found;
        return found;
    }

    /// <summary>Releases the old object and permits immediate discovery in a new scene.</summary>
    public void Invalidate()
    { lease?.Dispose(); lease = null; value = null; nextSearch = long.MinValue; }

    /// <summary>Releases the object and makes subsequent reads inert.</summary>
    public void Dispose() { disposed = true; Invalidate(); }
}
