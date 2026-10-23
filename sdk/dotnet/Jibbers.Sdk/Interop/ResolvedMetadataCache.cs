using System.Collections.Concurrent;

namespace Jibbers.Sdk.Interop;

/// <summary>Caches successful, process-lifetime IL2CPP metadata. Missing classes can resolve after later loading.</summary>
internal sealed class ResolvedMetadataCache<TKey, TValue>(Func<TValue, bool> resolved) where TKey : notnull
{
    readonly ConcurrentDictionary<TKey, TValue> values = new();
    public TValue GetOrResolve(TKey key, Func<TKey, TValue> lookup)
    {
        if (values.TryGetValue(key, out var cached)) return cached;
        var value = lookup(key);
        if (resolved(value)) values.TryAdd(key, value);
        return value;
    }
}
