using System.Text.Json.Nodes;

namespace Slvsh;

/// <summary>Where a game is. <see cref="Phase.Vote"/> follows every attempt while the others decide if it counted.</summary>
internal enum Phase { Lobby, Set, Match, Vote, Over }

/// <summary>An attempt as judged on the player's own PC: landed or not, and the trick the game named (may be "" for a bail).</summary>
internal sealed record Outcome(bool Landed, string Trick)
{
    public static readonly Outcome Miss = new(false, "");
    public static Outcome Land(string trick) => new(true, trick);
}

internal sealed record Player(ulong Id, string Name, int Letters);

/// <summary>A vote cast on the attempt being voted on.</summary>
internal sealed record Ballot(ulong Voter, bool Yes);

/// <summary>
/// A game of SLVSH as the host sends it to everyone. The host runs <see cref="SlvshHost"/>; the
/// others only show this, report their own attempts and send their votes.
/// </summary>
internal sealed record GameState
{
    public const string Word = "SLVSH";

    public uint Id { get; init; }
    public ulong Host { get; init; }
    public Phase Phase { get; init; }
    /// <summary>Matchers start each try where the setter's line began, at the setter's speed.</summary>
    public bool SameLine { get; init; }
    /// <summary>How long before the set trick the line begins (seconds).</summary>
    public int RunIn { get; init; } = 6;
    public IReadOnlyList<Player> Players { get; init; } = Array.Empty<Player>();
    public ulong Setter { get; init; }
    /// <summary>Who is trying (or being voted on) now; 0 = nobody.</summary>
    public ulong Attempter { get; init; }
    /// <summary>Counts up with every attempt, so results and votes are matched to their attempt.</summary>
    public uint Attempt { get; init; }
    /// <summary>The called line, as words (empty until the setter calls it).</summary>
    public string Trick { get; init; } = "";
    /// <summary>The called line: the tricks to land, in order (empty until the setter calls it).</summary>
    public IReadOnlyList<CalledTrick> Call { get; init; } = Array.Empty<CalledTrick>();
    /// <summary>The longest a line's words or a tried run may be.</summary>
    public const int MaxTrick = 400, MaxTried = 200;
    /// <summary>How long the setter has to call their line.</summary>
    public const int CallSeconds = 90;
    public int SecondsLeft { get; init; }
    public int SecondsTotal { get; init; }
    /// <summary>What just happened ("Ben missed: S-L").</summary>
    public string News { get; init; } = "";
    public ulong Winner { get; init; }

    // The vote on the last attempt (Phase.Vote).
    /// <summary>The phase the attempt was made in: Set or Match.</summary>
    public Phase VotingOn { get; init; }
    /// <summary>The trick the game saw the attempter do ("" if none).</summary>
    public string Tried { get; init; } = "";
    /// <summary>What trick detection thinks: landed the set, or matched it. Breaks ties and decides when nobody votes.</summary>
    public bool Detected { get; init; }
    public IReadOnlyList<ulong> Voters { get; init; } = Array.Empty<ulong>();
    public IReadOnlyList<Ballot> Ballots { get; init; } = Array.Empty<Ballot>();
    public int VoteSecondsLeft { get; init; }
    public int VoteSecondsTotal { get; init; }

    public Player? Find(ulong id) => Players.FirstOrDefault(p => p.Id == id);
    public string NameOf(ulong id) => Find(id)?.Name ?? "Someone";
    public bool Out(Player p) => p.Letters >= Word.Length;
    public IReadOnlyList<ulong> Alive => Players.Where(p => !Out(p)).Select(p => p.Id).ToList();
    public static string Spelled(int letters) => Word[..Math.Clamp(letters, 0, Word.Length)];
    /// <summary>True while <paramref name="id"/>'s turn is live or being voted on: the others watch them.</summary>
    public bool Watching(ulong id) => id != 0 && Attempter == id && Phase is Phase.Set or Phase.Match or Phase.Vote;
    public bool? BallotOf(ulong voter) => Ballots.FirstOrDefault(b => b.Voter == voter)?.Yes;

    static JsonArray Ids(IEnumerable<ulong> ids) => new(ids.Select(i => (JsonNode)JsonValue.Create(i.ToString())!).ToArray());

    public JsonObject ToJson() => new()
    {
        ["t"] = "state", ["id"] = Id, ["host"] = Host.ToString(), ["phase"] = Phase.ToString().ToLowerInvariant(),
        ["setter"] = Setter.ToString(), ["attempter"] = Attempter.ToString(), ["attempt"] = Attempt,
        ["trick"] = Trick, ["call"] = CalledLine.ToJson(Call), ["secs"] = SecondsLeft, ["total"] = SecondsTotal, ["news"] = News, ["winner"] = Winner.ToString(),
        ["players"] = new JsonArray(Players.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id.ToString(), ["name"] = p.Name, ["letters"] = p.Letters }).ToArray()),
        ["on"] = VotingOn.ToString().ToLowerInvariant(), ["tried"] = Tried, ["detected"] = Detected, ["voters"] = Ids(Voters),
        ["ballots"] = new JsonArray(Ballots.Select(b => (JsonNode)new JsonObject { ["v"] = b.Voter.ToString(), ["yes"] = b.Yes }).ToArray()),
        ["vsecs"] = VoteSecondsLeft, ["vtotal"] = VoteSecondsTotal, ["line"] = SameLine, ["runin"] = RunIn,
    };

    /// <summary>Reads a state message; null for anything malformed (it came over the network).</summary>
    public static GameState? FromJson(JsonNode? v)
    {
        try
        {
            if (v?["t"]?.GetValue<string>() != "state") return null;
            static ulong Id(JsonNode? n) => ulong.TryParse(n?.GetValue<string>(), out var x) ? x : 0;
            static Phase PhaseOf(JsonNode? n) => Enum.TryParse<Phase>(n?.GetValue<string>(), true, out var p) && Enum.IsDefined(p) ? p : Phase.Over;
            var players = v["players"]!.AsArray().Take(SlvshHost.MaxPlayers).Select(p => new Player(Id(p!["id"]), Clip(p["name"]?.GetValue<string>() ?? "", 32), Math.Clamp(p["letters"]?.GetValue<int>() ?? 0, 0, Word.Length)))
                .Where(p => p.Id != 0).ToList();
            return new GameState
            {
                Id = v["id"]!.GetValue<uint>(),
                Host = Id(v["host"]),
                Phase = PhaseOf(v["phase"]),
                Players = players,
                Setter = Id(v["setter"]),
                Attempter = Id(v["attempter"]),
                Attempt = v["attempt"]?.GetValue<uint>() ?? 0,
                Trick = Clip(v["trick"]?.GetValue<string>() ?? "", MaxTrick),
                Call = CalledLine.FromJson(v["call"]),
                SecondsLeft = Math.Clamp(v["secs"]?.GetValue<int>() ?? 0, 0, 600),
                SecondsTotal = Math.Clamp(v["total"]?.GetValue<int>() ?? 0, 0, 600),
                News = Clip(v["news"]?.GetValue<string>() ?? "", 120),
                Winner = Id(v["winner"]),
                VotingOn = PhaseOf(v["on"]),
                Tried = Clip(v["tried"]?.GetValue<string>() ?? "", MaxTried),
                Detected = v["detected"]?.GetValue<bool>() == true,
                Voters = (v["voters"]?.AsArray() ?? new JsonArray()).Take(SlvshHost.MaxPlayers).Select(Id).Where(i => i != 0).ToList(),
                Ballots = (v["ballots"]?.AsArray() ?? new JsonArray()).Take(SlvshHost.MaxPlayers).Select(b => new Ballot(Id(b!["v"]), b["yes"]?.GetValue<bool>() == true)).Where(b => b.Voter != 0).ToList(),
                VoteSecondsLeft = Math.Clamp(v["vsecs"]?.GetValue<int>() ?? 0, 0, 120),
                VoteSecondsTotal = Math.Clamp(v["vtotal"]?.GetValue<int>() ?? 0, 0, 120),
                SameLine = v["line"]?.GetValue<bool>() == true,
                RunIn = Math.Clamp(v["runin"]?.GetValue<int>() ?? 6, 2, 15),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// The host's side of a game. Players take turns setting: the setter lands any trick, then everyone
/// else gets one try at the same trick, and a miss earns the next letter of S-L-V-S-H. A setter
/// keeps setting until they miss their own set, which passes the set on (no letter). Spell SLVSH
/// and you're out; the last player left wins.
/// <para>With voting on, every attempt is followed by a vote: everyone else, having watched it,
/// says whether it counted. The majority decides; a tie, or nobody voting, falls back to what trick
/// detection saw.</para>
/// </summary>
internal sealed class SlvshHost
{
    public const int MaxPlayers = 8;
    readonly Func<double> clock;
    readonly List<ulong> followers = new();
    readonly int voteSeconds;
    double deadline, voteDeadline;
    /// <summary>Practice alone: you set, then match your own line, until you spell SLVSH.</summary>
    public bool Solo { get; }
    public GameState Game { get; private set; }
    /// <summary>Set when the state changed and should be sent reliably.</summary>
    public bool Changed { get; set; } = true;
    public double? OverAt { get; private set; }

    /// <param name="voteSeconds">How long a vote lasts; 0 turns voting off (trick detection decides).</param>
    /// <param name="sameLine">Matchers start on the setter's line (see <see cref="GameState.SameLine"/>).</param>
    public SlvshHost(ulong me, string myName, int attemptSeconds, int voteSeconds, Func<double> clock, bool sameLine = false, int runIn = 6, bool solo = false)
    {
        this.clock = clock;
        Solo = solo;
        this.voteSeconds = voteSeconds;
        var id = (uint)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) | 1;
        Game = new GameState
        {
            Id = id, Host = me, Phase = Phase.Lobby, SecondsTotal = attemptSeconds, VoteSecondsTotal = voteSeconds,
            SameLine = sameLine, RunIn = Math.Clamp(runIn, 2, 15),
            Players = new[] { new Player(me, myName, 0) }, News = $"{myName} started SLVSH",
        };
    }

    void Set(GameState next)
    {
        Game = next;
        Changed = true;
    }

    public void Join(ulong id, string name)
    {
        if (Game.Phase != Phase.Lobby || Game.Find(id) is not null || Game.Players.Count >= MaxPlayers) return;
        Set(Game with { Players = Game.Players.Append(new Player(id, name, 0)).ToList(), News = $"{name} joined" });
    }

    public bool Start()
    {
        if (Game.Phase != Phase.Lobby || (Game.Players.Count < 2 && !Solo)) return false;
        Set(Game with { Setter = Game.Players[0].Id, News = $"Game on! {Game.Players[0].Name} sets first." });
        BeginSet();
        return true;
    }

    void BeginAttempt(ulong who)
    {
        deadline = clock() + Game.SecondsTotal;
        Set(Game with { Attempter = who, Attempt = Game.Attempt + 1, SecondsLeft = Game.SecondsTotal, Tried = "", Ballots = Array.Empty<Ballot>(), Voters = Array.Empty<ulong>() });
    }

    void BeginSet()
    {
        followers.Clear();
        Set(Game with { Phase = Phase.Set, Trick = "", Call = Array.Empty<CalledTrick>() });
        BeginAttempt(Game.Setter);
        // First the setter calls their line; the clock for landing it starts once they have.
        deadline = clock() + GameState.CallSeconds;
        Set(Game with { SecondsLeft = GameState.CallSeconds });
    }

    /// <summary>The setter calls the line for their set. Only once per set; the clock restarts for landing it.</summary>
    public void Called(ulong who, uint attempt, IReadOnlyList<CalledTrick> line)
    {
        if (Game.Phase != Phase.Set || who != Game.Attempter || attempt != Game.Attempt || Game.Call.Count > 0) return;
        if (line.Count is 0 or > CalledLine.MaxTricks) return;
        var words = CalledLine.Describe(line);
        if (words.Length > GameState.MaxTrick) return;
        deadline = clock() + Game.SecondsTotal;
        Set(Game with { Call = line, Trick = words, SecondsLeft = Game.SecondsTotal, News = $"{Game.NameOf(who)} called their line" });
    }

    ulong NextAlive(ulong id)
    {
        var order = Game.Players.Select(p => p.Id).ToList();
        var alive = Game.Alive;
        var start = Math.Max(0, order.IndexOf(id));
        for (var i = 1; i <= order.Count; i++)
            if (alive.Contains(order[(start + i) % order.Count])) return order[(start + i) % order.Count];
        return id;
    }

    List<ulong> FollowersOf(ulong setter)
    {
        if (Solo) return new List<ulong> { setter };
        var order = Game.Players.Select(p => p.Id).ToList();
        var alive = Game.Alive;
        var start = Math.Max(0, order.IndexOf(setter));
        return Enumerable.Range(1, order.Count - 1).Select(i => order[(start + i) % order.Count]).Where(alive.Contains).ToList();
    }

    void NextFollower()
    {
        if (Solo ? Game.Alive.Count == 0 : Game.Alive.Count <= 1) { Finish(); return; }
        if (followers.Count == 0) { BeginSet(); return; } // everyone tried: the setter sets again
        var next = followers[0];
        followers.RemoveAt(0);
        Set(Game with { Phase = Phase.Match });
        BeginAttempt(next);
    }

    void Finish()
    {
        var winner = Game.Alive.FirstOrDefault();
        Set(Game with { Phase = Phase.Over, Attempter = 0, Winner = winner, News = winner != 0 ? $"{Game.NameOf(winner)} wins SLVSH!" : "Game over" });
        OverAt = clock();
    }

    /// <summary>
    /// The attempter's own result for attempt <paramref name="attempt"/>; stale or out-of-turn
    /// results are ignored. Starts the vote, or decides at once when voting is off, nobody else can
    /// vote, or a set produced no trick to vote on.
    /// </summary>
    public void Result(ulong who, uint attempt, Outcome outcome)
    {
        if (who != Game.Attempter || attempt != Game.Attempt || Game.Phase is not (Phase.Set or Phase.Match)) return;
        if (Game.Call.Count == 0) return; // nothing called yet: tricks don't count
        // The attempter's PC judged the run against the called line.
        var detected = outcome.Landed;
        var voters = Game.Players.Where(p => p.Id != who).Select(p => p.Id).ToList();
        var nothingToSet = Game.Phase == Phase.Set && outcome.Trick.Length == 0;
        if (voteSeconds <= 0 || voters.Count == 0 || nothingToSet)
        {
            Apply(Game.Phase, detected, outcome.Trick, "");
            return;
        }
        voteDeadline = clock() + voteSeconds;
        var what = Game.Phase == Phase.Set ? (outcome.Landed ? "landed the set" : "tried the set") : outcome.Trick.Length > 0 ? "tried it" : "bailed";
        Set(Game with
        {
            VotingOn = Game.Phase, Phase = Phase.Vote, Tried = outcome.Trick, Detected = detected, Voters = voters,
            Ballots = Array.Empty<Ballot>(), VoteSecondsLeft = voteSeconds, News = $"{Game.NameOf(who)} {what}. Did it count?",
        });
    }

    /// <summary>A vote on the current attempt. Voters may change their mind until the vote closes.</summary>
    public void Vote(ulong voter, uint attempt, bool yes)
    {
        if (Game.Phase != Phase.Vote || attempt != Game.Attempt || !Game.Voters.Contains(voter)) return;
        Set(Game with { Ballots = Game.Ballots.Where(b => b.Voter != voter).Append(new Ballot(voter, yes)).ToList() });
        if (Game.Voters.All(v => Game.Ballots.Any(b => b.Voter == v))) Decide();
    }

    void Decide()
    {
        var yes = Game.Ballots.Count(b => b.Yes);
        var no = Game.Ballots.Count - yes;
        var verdict = yes != no ? yes > no : Game.Detected;
        var tally = yes + no == 0 ? " (nobody voted: trick detection decided)" : yes == no ? $" ({yes}-{no}: trick detection decided)" : $" ({Math.Max(yes, no)}-{Math.Min(yes, no)} vote)";
        Apply(Game.VotingOn, verdict, Game.Tried, tally);
    }

    /// <summary>Applies the verdict on the current attempt and moves the game on.</summary>
    void Apply(Phase on, bool counted, string trick, string tally)
    {
        var who = Game.Attempter;
        var name = Game.NameOf(who);
        Set(Game with { Phase = on });
        if (on == Phase.Set)
        {
            if (counted)
            {
                Set(Game with { News = $"{name} landed the set{tally}" });
                followers.Clear();
                followers.AddRange(FollowersOf(who));
                NextFollower();
            }
            else
            {
                var next = NextAlive(who);
                Set(Game with { Setter = next, News = $"{name} missed the set{tally}. {Game.NameOf(next)} sets now." });
                BeginSet();
            }
            return;
        }
        if (counted) Set(Game with { News = $"{name} matched it!{tally}" });
        else
        {
            var players = Game.Players.Select(p => p.Id == who ? p with { Letters = Math.Min(p.Letters + 1, GameState.Word.Length) } : p).ToList();
            var p = players.First(x => x.Id == who);
            var why = trick.Length > 0 ? $"did {trick}" : "missed";
            var letters = string.Join("-", GameState.Spelled(p.Letters).ToCharArray());
            Set(Game with { Players = players, News = (p.Letters >= GameState.Word.Length ? $"{name} {why}: SLVSH, out!" : $"{name} {why}: {letters}") + tally });
        }
        NextFollower();
    }

    /// <summary>A player left the party or the game.</summary>
    public void Remove(ulong who)
    {
        if (Game.Find(who) is null || who == Game.Host) return;
        var name = Game.NameOf(who);
        var wasSetter = Game.Setter == who;
        var wasTrying = Game.Attempter == who;
        var playing = Game.Phase is Phase.Set or Phase.Match or Phase.Vote;
        var turnPhase = Game.Phase == Phase.Vote ? Game.VotingOn : Game.Phase;
        if (wasSetter && playing) Set(Game with { Setter = NextAlive(who) });
        followers.Remove(who);
        Set(Game with
        {
            Players = Game.Players.Where(p => p.Id != who).ToList(), Voters = Game.Voters.Where(v => v != who).ToList(),
            Ballots = Game.Ballots.Where(b => b.Voter != who).ToList(), News = $"{name} left",
        });
        if (playing && Game.Alive.Count <= 1) Finish();
        else if (wasTrying && turnPhase == Phase.Set) BeginSet();
        else if (wasTrying && turnPhase == Phase.Match) NextFollower();
        else if (Game.Phase == Phase.Vote && Game.Voters.All(v => Game.Ballots.Any(b => b.Voter == v))) Decide();
    }

    /// <summary>Counts down the attempt (running out is a miss) or the vote (closing it).</summary>
    public void Tick()
    {
        if (Game.Phase == Phase.Vote)
        {
            var leftVote = (int)Math.Ceiling(Math.Max(0, voteDeadline - clock()));
            if (leftVote != Game.VoteSecondsLeft) Game = Game with { VoteSecondsLeft = leftVote }; // countdown ticks go unreliable
            if (leftVote <= 0) Decide();
            return;
        }
        if (Game.Phase is not (Phase.Set or Phase.Match)) return;
        var left = (int)Math.Ceiling(Math.Max(0, deadline - clock()));
        if (left != Game.SecondsLeft) Game = Game with { SecondsLeft = left };
        if (left > 0) return;
        // Out of time: a miss with nothing to watch, so no vote.
        Apply(Game.Phase, false, "", Game.Phase == Phase.Set && Game.Call.Count == 0 ? " (no line called)" : " (out of time)");
    }
}
