namespace Slvsh;

/// <summary>
/// What the game reports, kept briefly: landed tricks (TrickPerformed) and respawns. A landed trick
/// fires one event with its name ("Left 180"); a bail fires none. Rails and combos fire "... On"
/// while still going, then an update with the full name when you come off ("Left 180 On - Front
/// 270 Out"), all with one sequence id. Fed on the main thread, read on the game thread.
/// </summary>
internal sealed class TrickLog
{
    internal sealed record Trick(double At, string Name, int Sequence)
    {
        /// <summary>Still on a rail: the name ends in " On" until the rider comes off.</summary>
        public bool Going => Name.EndsWith(" On", StringComparison.OrdinalIgnoreCase);
    }

    readonly object gate = new();
    readonly List<Trick> tricks = new();
    readonly List<double> resets = new();

    public void AddTrick(double at, string name, int sequence)
    {
        lock (gate)
        {
            tricks.Add(new Trick(at, name.Trim(), sequence));
            if (tricks.Count > 64) tricks.RemoveAt(0);
        }
    }

    public void AddReset(double at)
    {
        lock (gate)
        {
            resets.Add(at);
            if (resets.Count > 16) resets.RemoveAt(0);
        }
    }

    public (List<Trick> Tricks, List<double> Resets) Since(double at)
    {
        lock (gate) return (tricks.Where(t => t.At >= at).ToList(), resets.Where(r => r >= at).ToList());
    }
}

/// <summary>
/// Watches one attempt: the first trick after it starts is the try. It counts as landed once the
/// rider rides away clean (no respawn, no more combo for <see cref="Settle"/>); respawning after the
/// trick started, or a new trick while still on a rail, is a miss (still naming the trick, for the vote). Respawning before the trick (to
/// get back up to the jump) is fine.
/// </summary>
internal sealed class TrickJudge
{
    public const double Settle = 1.8, Rail = 10;
    readonly TrickLog log;
    readonly double started;
    int? sequence;
    string name = "";
    bool going;
    double last;
    int seen;

    public TrickJudge(TrickLog log, double now)
    {
        this.log = log;
        started = now;
    }

    /// <summary>The trick so far, once one has started.</summary>
    public string? Current => sequence is null ? null : name;
    /// <summary>When the game first reported the trick (it reports on landing), once one has started.</summary>
    public double? TrickAt { get; private set; }
    /// <summary>When this attempt started.</summary>
    public double Started => started;

    /// <summary>The outcome once it is decided, else null.</summary>
    public Outcome? Step(double now)
    {
        var (tricks, resets) = log.Since(started);
        foreach (var t in tricks.Skip(seen))
        {
            if (sequence is null || sequence == t.Sequence)
            {
                TrickAt ??= t.At;
                (sequence, name, going, last) = (t.Sequence, t.Name, t.Going, t.At);
                continue;
            }
            // A new trick began: the first one was landed if it wasn't still going.
            return going ? new Outcome(false, name) : Outcome.Land(name);
        }
        seen = tricks.Count;
        if (sequence is null) return null;
        if (resets.Any(r => r >= last)) return new Outcome(false, name);
        if (!going && now - last >= Settle) return Outcome.Land(name);
        if (going && now - last >= Rail) return new Outcome(false, name);
        return null;
    }
}
