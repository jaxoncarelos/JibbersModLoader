using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.UI;
using Slvsh;
using Slvsh.Integration;

static class SlvshTests
{
    static Outcome Landed(string trick) => Outcome.Land(trick);

    /// <summary>Ana (host), Ben and Cat; voting off unless asked.</summary>
    static SlvshHost Host(Func<double>? clock = null, int voteSeconds = 0)
    {
        var h = new SlvshHost(1, "Ana", MatchRules.Exact, 45, voteSeconds, clock ?? (() => 0));
        h.Join(2, "Ben");
        h.Join(3, "Cat");
        return h;
    }

    [Test]
    static void AGamePlaysOut()
    {
        var alone = new SlvshHost(1, "Ana", MatchRules.Exact, 45, 0, () => 0);
        Assert.True(!alone.Start(), "can't start alone");
        var h = Host();
        Assert.True(h.Start());
        Assert.Equal((Phase.Set, 1UL), (h.Game.Phase, h.Game.Attempter));
        // Ana sets, Ben matches, Cat misses: Cat gets an S, then Ana sets again.
        h.Result(1, h.Game.Attempt, Landed("Left 180"));
        Assert.Equal((Phase.Match, 2UL, "Left 180"), (h.Game.Phase, h.Game.Attempter, h.Game.Trick));
        h.Result(2, h.Game.Attempt, Landed("left  180"));
        Assert.Equal(3UL, h.Game.Attempter);
        h.Result(3, h.Game.Attempt, Landed("Right 180"));
        Assert.Equal(1, h.Game.Find(3)!.Letters);
        Assert.Equal("Cat did Right 180: S", h.Game.News);
        Assert.Equal((Phase.Set, 1UL), (h.Game.Phase, h.Game.Attempter));
        h.Result(3, 1, Landed("x")); // stale
        Assert.Equal(1UL, h.Game.Attempter);
        // Ana misses her own set: no letter, Ben sets.
        h.Result(1, h.Game.Attempt, Outcome.Miss);
        Assert.Equal(0, h.Game.Find(1)!.Letters);
        Assert.Equal((Phase.Set, 2UL, 2UL), (h.Game.Phase, h.Game.Setter, h.Game.Attempter));
        for (var i = 0; i < 6 && h.Game.Phase != Phase.Over; i++)
        {
            h.Result(2, h.Game.Attempt, Landed("Zero Spin"));
            while (h.Game.Phase == Phase.Match) h.Result(h.Game.Attempter, h.Game.Attempt, Outcome.Miss);
        }
        Assert.Equal(Phase.Over, h.Game.Phase);
        Assert.Equal(2UL, h.Game.Winner);
        Assert.Equal("Ben wins SLVSH!", h.Game.News);
        Assert.Equal(5, h.Game.Find(1)!.Letters);
    }

    [Test]
    static void TheVoteOverrulesTrickDetection()
    {
        var h = Host(voteSeconds: 12);
        h.Start();
        // Ana's set: detection says landed, so it goes to a vote of Ben and Cat.
        h.Result(1, h.Game.Attempt, Landed("Left 360 Mute"));
        Assert.Equal((Phase.Vote, Phase.Set, "Left 360 Mute", true), (h.Game.Phase, h.Game.VotingOn, h.Game.Tried, h.Game.Detected));
        Assert.True(h.Game.Voters.SequenceEqual(new ulong[] { 2, 3 }));
        h.Vote(1, h.Game.Attempt, true); // the attempter can't vote on themselves
        Assert.Equal(0, h.Game.Ballots.Count);
        h.Vote(2, h.Game.Attempt, false);
        h.Vote(2, h.Game.Attempt, true); // changed their mind
        Assert.Equal(1, h.Game.Ballots.Count);
        h.Vote(3, h.Game.Attempt, true);
        Assert.Equal((Phase.Match, "Left 360 Mute", 2UL), (h.Game.Phase, h.Game.Trick, h.Game.Attempter));
        Assert.Equal("Ana set Left 360 Mute (2-0 vote)", h.Game.News);

        // Ben "matches" by name but everyone saw him wash out: the vote says no.
        h.Result(2, h.Game.Attempt, Landed("Left 360 Mute"));
        Assert.True(h.Game.Detected);
        h.Vote(1, h.Game.Attempt, false);
        h.Vote(3, h.Game.Attempt, false);
        Assert.Equal(1, h.Game.Find(2)!.Letters);
        Assert.Equal("Ben did Left 360 Mute: S (2-0 vote)", h.Game.News);

        // Cat's grab wasn't detected right, but the others saw the match.
        h.Result(3, h.Game.Attempt, Landed("Left 360"));
        Assert.True(!h.Game.Detected);
        h.Vote(1, h.Game.Attempt, true);
        h.Vote(2, h.Game.Attempt, true);
        Assert.Equal(0, h.Game.Find(3)!.Letters);
        Assert.Equal((Phase.Set, 1UL), (h.Game.Phase, h.Game.Attempter));
    }

    [Test]
    static void TiesAndSilenceGoToTrickDetection()
    {
        var now = 0.0;
        var h = Host(() => now, voteSeconds: 10);
        h.Start();
        h.Result(1, h.Game.Attempt, Landed("Back 540"));
        now = 5;
        h.Tick();
        Assert.Equal(5, h.Game.VoteSecondsLeft);
        now = 11;
        h.Tick();
        Assert.Equal("Ana set Back 540 (nobody voted: trick detection decided)", h.Game.News);
        Assert.Equal(Phase.Match, h.Game.Phase);

        h.Result(2, h.Game.Attempt, Landed("Front 540")); // detection: no match
        h.Vote(1, h.Game.Attempt, true);
        h.Vote(3, h.Game.Attempt, false);
        Assert.Equal(1, h.Game.Find(2)!.Letters);
        Assert.True(h.Game.News.EndsWith("(1-1: trick detection decided)"), h.Game.News);
    }

    [Test]
    static void NoVoteWhenThereIsNothingToJudge()
    {
        var now = 0.0;
        var h = Host(() => now, voteSeconds: 10);
        h.Start();
        // A set with no trick at all (bailed) can't become the trick to match.
        h.Result(1, h.Game.Attempt, Outcome.Miss);
        Assert.Equal((Phase.Set, 2UL), (h.Game.Phase, h.Game.Attempter));
        // Running out of time is a miss straight away.
        h.Result(2, h.Game.Attempt, Landed("Left 180"));
        h.Vote(1, h.Game.Attempt, true);
        h.Vote(3, h.Game.Attempt, true);
        Assert.Equal((Phase.Match, 3UL), (h.Game.Phase, h.Game.Attempter));
        now = 100;
        h.Tick();
        Assert.Equal(1, h.Game.Find(3)!.Letters);
        Assert.True(h.Game.News.EndsWith("(out of time)"), h.Game.News);
    }

    [Test]
    static void LeavingDuringTurnsAndVotes()
    {
        var h = Host(voteSeconds: 10);
        h.Start();
        h.Result(1, h.Game.Attempt, Landed("Left 180"));
        h.Vote(2, h.Game.Attempt, true);
        h.Remove(3); // the last voter left: Ben's vote decides
        Assert.Equal((Phase.Match, 2UL), (h.Game.Phase, h.Game.Attempter));
        h.Result(2, h.Game.Attempt, Landed("Left 180"));
        Assert.Equal(Phase.Vote, h.Game.Phase);
        h.Remove(2); // the player being voted on left: only Ana is left, she wins
        Assert.Equal((Phase.Over, 1UL), (h.Game.Phase, h.Game.Winner));
        h.Remove(1); // the host can't remove themselves
        Assert.True(h.Game.Find(1) is not null);
    }

    [Test]
    static void StateRoundTripsAndJunkIsRejected()
    {
        var h = Host(voteSeconds: 10);
        h.Start();
        h.Result(1, h.Game.Attempt, Landed("Switch Left 540"));
        h.Vote(2, h.Game.Attempt, false);
        var copy = GameState.FromJson(JsonNode.Parse(h.Game.ToJson().ToJsonString()))!;
        Assert.True(h.Game.Players.SequenceEqual(copy.Players));
        Assert.True(h.Game.Voters.SequenceEqual(copy.Voters));
        Assert.True(h.Game.Ballots.SequenceEqual(copy.Ballots));
        Assert.Equal(h.Game with { Players = copy.Players, Voters = copy.Voters, Ballots = copy.Ballots }, copy);
        Assert.Equal(false, copy.BallotOf(2));
        Assert.True(GameState.FromJson(JsonNode.Parse("""{"t":"state"}""")) is null);
        Assert.True(GameState.FromJson(JsonNode.Parse("""{"t":"state","id":"x","players":[]}""")) is null);
        var big = h.Game.ToJson();
        big["players"]![0]!["letters"] = 99;
        big["phase"] = "99";
        var read = GameState.FromJson(big)!;
        Assert.Equal(5, read.Players[0].Letters);
        Assert.Equal(Phase.Over, read.Phase);
    }

    [Test]
    static void MatchingRules()
    {
        Assert.True(MatchRules.Matches("Switch Left 540", "switch  left 540", MatchRules.Exact));
        Assert.True(!MatchRules.Matches("Switch Left 540", "Left 540", MatchRules.Exact));
        Assert.True(MatchRules.Matches("Left 360 Indy", "Left 360 Mute", MatchRules.Spins));
        Assert.True(!MatchRules.Matches("Left 360 Indy", "Right 360 Indy", MatchRules.Spins));
        Assert.True(MatchRules.Matches("Zero Spin", "Left 180", MatchRules.Any));
    }

    [Test]
    static void JudgingAttempts()
    {
        var log = new TrickLog();
        var j = new TrickJudge(log, 10);
        log.AddReset(10.5); // respawning before the trick is fine
        log.AddTrick(11, "Left 180", 1);
        Assert.True(j.Step(11.5) is null);
        Assert.Equal("Left 180", j.Current);
        Assert.Equal(Landed("Left 180"), j.Step(13));

        j = new TrickJudge(log, 20);
        log.AddTrick(21, "Left 180 On", 2);
        Assert.True(j.Step(25) is null, "still on the rail");
        log.AddTrick(26, "Left 180 On - Front 270 Out", 2);
        Assert.Equal(Landed("Left 180 On - Front 270 Out"), j.Step(28));

        j = new TrickJudge(log, 30);
        log.AddTrick(31, "Back 360", 3);
        log.AddReset(31.5);
        Assert.Equal(new Outcome(false, "Back 360"), j.Step(32)); // a bail, but the vote still sees the trick

        j = new TrickJudge(log, 40);
        log.AddTrick(41, "Zero Spin", 4);
        log.AddTrick(41.5, "Right 540", 5);
        Assert.Equal(Landed("Zero Spin"), j.Step(41.6));
    }

    [Test]
    static void FindingTheRiderToWatch()
    {
        var riders = new[]
        {
            new RiderCandidate("<b>Ana</b> ", new Vector3(0, 0, 0)),
            new RiderCandidate("", new Vector3(100, 0, 0)),
            new RiderCandidate("Someone Else", new Vector3(200, 0, 0)),
        };
        Assert.Equal(0, RiderMatch.Pick("ana", null, riders));
        Assert.Equal(1, RiderMatch.Pick("Ben", new Vector3(104, 2, 0), riders)); // no nametag: nearest to where Ben says he is
        Assert.Equal(-1, RiderMatch.Pick("Ben", new Vector3(150, 0, 0), riders)); // nobody near: not on this server
        Assert.Equal(-1, RiderMatch.Pick("Ben", null, riders));
    }

    static IEnumerable<(SlvshView View, string Name)> Views()
    {
        var h = Host(voteSeconds: 12);
        yield return (new SlvshView { Me = 1, Game = h.Game }, "Lobby (host)");
        h.Start();
        yield return (new SlvshView { Me = 1, Game = h.Game with { SecondsLeft = 31 } }, "Your set");
        yield return (new SlvshView { Me = 2, Game = h.Game with { SecondsLeft = 31 }, Watching = SpectateStatus.Watching }, "Watching the set");
        h.Result(1, h.Game.Attempt, Landed("Switch Left 540 Indy"));
        h.Vote(3, h.Game.Attempt, true);
        yield return (new SlvshView { Me = 2, Game = h.Game with { VoteSecondsLeft = 8 }, Watching = SpectateStatus.Watching }, "Voting on a set");
        h.Vote(2, h.Game.Attempt, true);
        h.Result(2, h.Game.Attempt, new Outcome(false, "Switch Left 540"));
        h.Vote(3, h.Game.Attempt, false);
        yield return (new SlvshView { Me = 1, Game = h.Game with { VoteSecondsLeft = 5 }, Watching = SpectateStatus.NotHere }, "Voted, other server");
        yield return (new SlvshView { Me = 2, Game = h.Game with { VoteSecondsLeft = 5 } }, "Being voted on");
        h.Vote(1, h.Game.Attempt, false);
        yield return (new SlvshView { Me = 3, Game = h.Game with { SecondsLeft = 7 }, Trying = "Switch Left 540" }, "Your turn to match");
        while (h.Game.Phase != Phase.Over)
        {
            switch (h.Game.Phase)
            {
                case Phase.Set: h.Result(h.Game.Attempter, h.Game.Attempt, h.Game.Attempter == 3 ? Landed("Zero Spin") : Outcome.Miss); break;
                case Phase.Match: h.Result(h.Game.Attempter, h.Game.Attempt, h.Game.Attempter == 3 ? Landed("Zero Spin") : Outcome.Miss); break;
                case Phase.Vote: foreach (var voter in h.Game.Voters) h.Vote(voter, h.Game.Attempt, h.Game.Detected); break;
            }
        }
        yield return (new SlvshView { Me = 3, Game = h.Game }, "Game over");
        yield return (new SlvshView { Me = 2, Invite = ("Ana", 3) }, "Invite");
        yield return (new SlvshView { Me = 2, Hint = "Make or join a party first: press F7 for the Party panel" }, "Hint");
    }

    [Test]
    static void ScoreboardFitsEveryView()
    {
        foreach (var (view, name) in Views())
        {
            var frame = Scoreboard.Build(view);
            Assert.True(frame is not null, name);
        }
        Assert.True(Scoreboard.Build(new SlvshView()) is null, "nothing to show");
        var dir = Environment.GetEnvironmentVariable("SLVSH_PREVIEW");
        if (!string.IsNullOrEmpty(dir)) OverlayPreview.Save(Views().Select(v => (Scoreboard.Build(v.View)!, v.Name)), Path.Combine(dir, "slvsh.html"));
    }
}
