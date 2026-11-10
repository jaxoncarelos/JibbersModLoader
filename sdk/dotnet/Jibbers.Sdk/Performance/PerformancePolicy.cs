namespace Jibbers.Sdk;

// Five-second windows, measured attribution, warmup and a cooldown between suspensions.
// One shader-heavy frame, alt-tab or a map-loading gap never starts a removal cascade.
internal sealed class PerformancePolicy
{
    sealed class Cost { public double Total; public int Hitches; }
    readonly Dictionary<string, Cost> costs = new(StringComparer.Ordinal);
    double window, blockedUntil = 20;
    int stalls;
    public void Warmup(double now) { Clear(now); blockedUntil = now + 15; }
    void Clear(double now) { window = now; costs.Clear(); stalls = 0; }
    public string? Frame(double now, double milliseconds, Dictionary<string, double> measured, Func<string, bool> eligible, bool gameplay)
    {
        if (!gameplay || milliseconds > 2000 || milliseconds <= 0) { Warmup(now); return null; }
        if (now < blockedUntil) { Clear(now); return null; }
        if (milliseconds >= 80) stalls++;
        foreach (var pair in measured)
        {
            if (!eligible(pair.Key)) continue;
            if (!costs.TryGetValue(pair.Key, out var cost)) costs[pair.Key] = cost = new();
            cost.Total += pair.Value;
            // Timing must account for a substantial part of that actual stalled frame.
            if (milliseconds >= 80 && pair.Value >= Math.Max(20, milliseconds * 0.35)) cost.Hitches++;
        }
        if (now - window < 5) return null;
        string? worst = null; double largest = 0;
        if (stalls >= 4)
            foreach (var pair in costs)
                if (pair.Value.Hitches >= 3 && pair.Value.Total >= 150 && pair.Value.Total > largest && eligible(pair.Key))
                { worst = pair.Key; largest = pair.Value.Total; }
        Clear(now);
        if (worst is not null) blockedUntil = now + 12;
        return worst;
    }
}
