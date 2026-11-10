using System.Collections.Concurrent;
using System.Diagnostics;

namespace Jibbers.Sdk;

// Exclusive callback time on the game thread. Native original calls and nested owners are
// charged separately. Scopes are structs; stable callbacks do not allocate profiler samples.
internal static class ModTiming
{
    internal sealed class Counter(string id)
    {
        public string Id { get; } = id;
        public long Ticks;
        public int ActiveCalls;
    }
    static readonly ConcurrentDictionary<string, Counter> counters = new(StringComparer.Ordinal);
    [ThreadStatic] static Counter? active;
    [ThreadStatic] static long started, pauseStarted;
    static void Accrue(long now, long paused)
    {
        if (active is { } counter)
        {
            var pause = (long)((paused - pauseStarted) * ((double)Stopwatch.Frequency / TimeSpan.TicksPerSecond));
            Interlocked.Add(ref counter.Ticks, Math.Max(0, now - started - pause));
        }
        started = now; pauseStarted = paused;
    }
    internal static Func<long> Timestamp = Stopwatch.GetTimestamp;
    internal static bool Collecting;
    static Counter[] snapshot = [];
    static readonly object gate = new();
    public static Counter For(string owner)
    {
        if (counters.TryGetValue(owner, out var found)) return found;
        lock (gate)
        {
            found = counters.GetOrAdd(owner, static id => new(id));
            Volatile.Write(ref snapshot, counters.Values.ToArray());
            return found;
        }
    }
    public static Scope Begin(string? owner, bool exclude = false)
    {
        var counter = owner is null ? null : For(owner);
        if (counter is not null) Interlocked.Increment(ref counter.ActiveCalls);
        var measure = Collecting && (MainThread.IsCurrent || MainThread.IsKnownMainThread);
        if (!measure) return new(counter, null, false);
        var now = Timestamp();
        Accrue(now, GC.GetTotalPauseDuration().Ticks);
        var parent = active;
        active = exclude ? null : counter;
        started = now;
        return new(counter, parent, true);
    }
    internal readonly struct Scope(Counter? lease, Counter? parent, bool measure) : IDisposable
    {
        public void Dispose()
        {
            if (measure)
            {
                var now = Timestamp();
                Accrue(now, GC.GetTotalPauseDuration().Ticks);
                active = parent;
            }
            if (lease is not null) Interlocked.Decrement(ref lease.ActiveCalls);
        }
    }
    public static void Drain(Dictionary<string, double> result)
    {
        result.Clear();
        foreach (var counter in Volatile.Read(ref snapshot))
        {
            var ticks = Interlocked.Exchange(ref counter.Ticks, 0);
            if (ticks > 0) result[counter.Id] = ticks * 1000d / Stopwatch.Frequency;
        }
    }
    public static bool Idle(string owner) => !counters.TryGetValue(owner, out var counter) || Volatile.Read(ref counter.ActiveCalls) == 0;
}
