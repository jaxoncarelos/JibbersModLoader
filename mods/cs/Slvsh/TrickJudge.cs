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
/// Watches one attempt at a called line: the tricks must be landed in order in one run. Small stuff
/// in between (straight airs, zero spins, butters, small spins) doesn't count; every other trick
/// takes the next place in the line, and the try is decided once every place is taken (so a misread
/// trick never ends a run early). A trick is landed once the rider rides away clean (no respawn,
/// no more combo for <see cref="Settle"/>) or starts the next trick; a rail trick still going when
/// the next one starts, or for <see cref="Rail"/> seconds, wasn't landed. The run starts at your
/// first real trick (see <see cref="TrickName.IsReal"/>) or the line's first trick: small stuff on the
/// way to the feature doesn't count. Respawning after that is a miss unless the whole line was landed.
/// </summary>
internal sealed class TrickJudge
{
    public const double Settle = 1.8, Rail = 10;
    /// <summary>A real trick this soon after coming off a rail is how you came off it (a flip or spin out the game reported on its own).</summary>
    public const double OffRail = 2.5;
    /// <summary>
    /// Tricks and resets this soon after the turn starts are ignored: you're placed at the spawn a
    /// little above the snow, and the game can report the drop as a trick (a straight air, a zero
    /// spin, even a 180 if you land turned).
    /// </summary>
    public const double Grace = 1.5;
    readonly TrickLog log;
    readonly IReadOnlyList<CalledTrick> line;
    readonly double started;

    public TrickJudge(TrickLog log, double now, IReadOnlyList<CalledTrick> line)
    {
        this.log = log;
        this.line = line;
        started = now;
    }

    /// <summary>The tricks since your last respawn, once one has started ("Left 540  /  Regular On - Slide").</summary>
    public string? Current { get; private set; }
    /// <summary>When the game first reported the line's first trick (it reports on landing).</summary>
    public double? TrickAt { get; private set; }
    /// <summary>When this attempt started.</summary>
    public double Started => started;
    /// <summary>How many of the line's tricks are landed so far.</summary>
    public int Matched { get; private set; }
    /// <summary>Whether your run has started (a real trick, or the line's first), so ending it now is a try.</summary>
    public bool Begun { get; private set; }

    sealed record Run(int Sequence, double First, double Last, string Name, bool Going);

    /// <summary>The outcome once it is decided, else null.</summary>
    public Outcome? Step(double now)
    {
        var (tricks, resets) = log.Since(started);
        // One entry per trick (sequence id): rails update their name as they go.
        var runs = new List<Run>();
        foreach (var t in tricks)
        {
            if (t.At < started + Grace) continue; // the drop at the spawn
            if (runs.Count > 0 && runs[^1].Sequence == t.Sequence) runs[^1] = runs[^1] with { Last = t.At, Name = t.Name, Going = t.Going };
            else runs.Add(new Run(t.Sequence, t.At, t.At, t.Name, t.Going));
        }
        // The game sometimes ends a rail trick as you come off and reports the flip or spin out as a
        // trick of its own: join them ("Regular On - Slide - To Regular - Backflip Out").
        for (var i = 0; i + 1 < runs.Count; i++)
        {
            var (rail, off) = (runs[i], runs[i + 1]);
            if (rail.Going || !TrickName.Parse(rail.Name).Rail || TrickName.Parse(off.Name).Rail || off.Going) continue;
            if (off.First - rail.Last > OffRail || !TrickName.IsReal(off.Name) || TrickName.Parse(rail.Name).Segments.Any(s => s.Kind == SegmentKind.Out)) continue;
            runs[i] = rail with { Last = off.Last, Name = $"{rail.Name} - {off.Name} Out" };
            runs.RemoveAt(i + 1);
        }
        // Respawns split the tricks into runs; each is judged on its own.
        // A reset in the first moments of the turn is from being placed at the spawn, not a bail.
        var bounds = resets.Where(r => r >= started + Grace).OrderBy(r => r).ToList();
        var from = started;
        foreach (var reset in bounds.Append(double.PositiveInfinity))
        {
            var segment = runs.Where(r => r.First >= from && r.First < reset).ToList();
            var ended = !double.IsPositiveInfinity(reset);
            var (landed, matched, at, done) = Judge(segment, ended ? reset : now);
            Begun = segment.Count > 0 || ended;
            Current = segment.Count > 0 ? string.Join("  /  ", segment.Select(r => r.Name)) : null;
            Matched = matched;
            TrickAt = at;
            var tried = Clip(string.Join("  /  ", landed.Select(r => r.Name)));
            if (matched == line.Count) return Outcome.Land(tried);
            // As many tricks as the line has, but not all as called. Not decided before then, so a
            // misread trick doesn't end your run early; the vote has the last word.
            if (done) return new Outcome(false, tried);
            // You're placed at the spawn when your turn goes live, so a reset now means you bailed (or
            // gave up): the try is over, and the vote decides.
            if (ended) return new Outcome(false, Clip(Current ?? ""));
            if (!ended && segment.Count > 0 && segment[^1] is { Going: true } last && now - last.Last >= Rail && matched > 0)
                return new Outcome(false, Clip(Current!));
            from = reset;
        }
        return null;
    }

    /// <summary>
    /// The tricks landed in one run (up to <paramref name="end"/>). Each real trick (or one matching the
    /// next called trick) takes the next place in the line; small stuff is ignored. Returns how many
    /// places hold the called trick, and whether every place is taken.
    /// </summary>
    (List<Run> Landed, int Matched, double? At, bool Done) Judge(List<Run> segment, double end)
    {
        var landed = new List<Run>();
        for (var i = 0; i < segment.Count; i++)
        {
            var r = segment[i];
            var next = i + 1 < segment.Count ? segment[i + 1].First : (double?)null;
            if (r.Going) continue; // still on the rail, or bailed off it
            if (next is null && end - r.Last < (TrickName.Parse(r.Name).Rail ? Math.Max(Settle, OffRail) : Settle)) break; // not ridden away (or respawned too soon after)
            landed.Add(r);
        }
        int place = 0, matched = 0;
        double? at = null;
        foreach (var r in landed)
        {
            if (place == line.Count) break;
            var right = line[place].Matches(r.Name);
            if (!right && !TrickName.IsReal(r.Name)) continue;
            if (place == 0) at = r.First;
            if (right) matched++;
            place++;
        }
        return (landed, matched, at, place == line.Count);
    }

    static string Clip(string s) => s.Length <= GameState.MaxTried ? s : s[..(GameState.MaxTried - 1)] + "…";
}
