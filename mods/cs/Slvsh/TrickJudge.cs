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
/// Watches one attempt at a called line: the tricks must be landed in order in one run, and other
/// tricks in between don't matter. A trick is landed once the rider rides away clean (no respawn,
/// no more combo for <see cref="Settle"/>) or starts the next trick; a rail trick still going when
/// the next one starts, or for <see cref="Rail"/> seconds, wasn't landed. Respawning before the
/// first trick (to get back up to the jump) is fine; respawning after it ends the run, a miss
/// unless the whole line was already landed and settled.
/// </summary>
internal sealed class TrickJudge
{
    public const double Settle = 1.8, Rail = 10;
    readonly TrickLog log;
    readonly IReadOnlyList<CalledTrick> line;
    readonly double started;

    public TrickJudge(TrickLog log, double now, IReadOnlyList<CalledTrick> line)
    {
        this.log = log;
        this.line = line;
        started = now;
    }

    /// <summary>The tricks so far, once one has started ("Left 540  /  Regular On - Slide").</summary>
    public string? Current { get; private set; }
    /// <summary>When the game first reported a trick of the line (it reports on landing).</summary>
    public double? TrickAt { get; private set; }
    /// <summary>When this attempt started.</summary>
    public double Started => started;
    /// <summary>How many of the line's tricks are landed so far.</summary>
    public int Matched { get; private set; }

    sealed record Run(int Sequence, double First, double Last, string Name, bool Going);

    /// <summary>The outcome once it is decided, else null.</summary>
    public Outcome? Step(double now)
    {
        var (tricks, resets) = log.Since(started);
        // One entry per trick (sequence id): rails update their name as they go.
        var runs = new List<Run>();
        foreach (var t in tricks)
        {
            if (runs.Count > 0 && runs[^1].Sequence == t.Sequence) runs[^1] = runs[^1] with { Last = t.At, Name = t.Name, Going = t.Going };
            else runs.Add(new Run(t.Sequence, t.At, t.At, t.Name, t.Going));
        }
        if (runs.Count == 0) return null;
        var landed = new List<Run>();
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            var next = i + 1 < runs.Count ? runs[i + 1].First : (double?)null;
            var end = next ?? now;
            if (resets.Any(x => x >= r.First && x < end)) break; // the run ended on this trick
            if (r.Going) { if (next is null) break; continue; } // still on the rail, or bailed off it
            if (next is null && now - r.Last < Settle) break; // not ridden away yet
            landed.Add(r);
        }
        Current = string.Join("  /  ", runs.Select(r => r.Name));
        Matched = 0;
        foreach (var r in landed)
            if (Matched < line.Count && line[Matched].Matches(r.Name))
            {
                if (Matched == 0) TrickAt = r.First;
                Matched++;
            }
        var tried = Clip(string.Join("  /  ", landed.Select(r => r.Name)));
        if (Matched == line.Count) return Outcome.Land(tried);
        var last = runs[^1];
        if (resets.Any(x => x >= runs[0].First)) return new Outcome(false, Clip(Current));
        if (last.Going && now - last.Last >= Rail) return new Outcome(false, Clip(Current));
        return null;
    }

    static string Clip(string s) => s.Length <= GameState.MaxTried ? s : s[..(GameState.MaxTried - 1)] + "…";
}
