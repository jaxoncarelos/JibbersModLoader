using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.UI;
using Slvsh;
using Slvsh.Integration;

static class SlvshTests
{
    static Outcome Landed(string trick) => Outcome.Land(trick);

    static readonly CalledTrick Left180 = new() { Type = TrickType.Jump, Spin = "left", Axis = "flat", Degrees = 180 };

    /// <summary>Ana (host), Ben and Cat; voting off unless asked.</summary>
    static SlvshHost Host(Func<double>? clock = null, int voteSeconds = 0)
    {
        var h = new SlvshHost(1, "Ana", 45, voteSeconds, clock ?? (() => 0));
        h.Join(2, "Ben");
        h.Join(3, "Cat");
        return h;
    }

    /// <summary>The setter calls a Left 180.</summary>
    static void Call(SlvshHost h) => h.Called(h.Game.Attempter, h.Game.Attempt, new[] { Left180 });

    [Test]
    static void AGamePlaysOut()
    {
        var alone = new SlvshHost(1, "Ana", 45, 0, () => 0);
        Assert.True(!alone.Start(), "can't start alone");
        var h = Host();
        Assert.True(h.Start());
        Assert.Equal((Phase.Set, 1UL), (h.Game.Phase, h.Game.Attempter));
        h.Result(1, h.Game.Attempt, Landed("Left 180"));
        Assert.Equal(Phase.Set, h.Game.Phase); // nothing called yet: the trick doesn't count
        h.Called(2, h.Game.Attempt, new[] { Left180 }); // only the setter calls
        Assert.Equal(0, h.Game.Call.Count);
        // Ana calls and lands, Ben matches, Cat misses: Cat gets an S, then Ana sets again.
        Call(h);
        Assert.Equal((Phase.Set, "Left 180"), (h.Game.Phase, h.Game.Trick));
        h.Called(1, h.Game.Attempt, new[] { Left180, Left180 }); // once per set
        Assert.Equal(1, h.Game.Call.Count);
        h.Result(1, h.Game.Attempt, Landed("Left 180"));
        Assert.Equal((Phase.Match, 2UL, "Left 180"), (h.Game.Phase, h.Game.Attempter, h.Game.Trick));
        h.Result(2, h.Game.Attempt, Landed("Left 180"));
        Assert.Equal(3UL, h.Game.Attempter);
        h.Result(3, h.Game.Attempt, new Outcome(false, "Right 180"));
        Assert.Equal(1, h.Game.Find(3)!.Letters);
        Assert.Equal("Cat did Right 180: S", h.Game.News);
        Assert.Equal((Phase.Set, 1UL, 0), (h.Game.Phase, h.Game.Attempter, h.Game.Call.Count));
        h.Result(3, 1, Landed("x")); // stale
        Assert.Equal(1UL, h.Game.Attempter);
        // Ana misses her own set: no letter, Ben sets.
        Call(h);
        h.Result(1, h.Game.Attempt, Outcome.Miss);
        Assert.Equal(0, h.Game.Find(1)!.Letters);
        Assert.Equal((Phase.Set, 2UL, 2UL), (h.Game.Phase, h.Game.Setter, h.Game.Attempter));
        for (var i = 0; i < 6 && h.Game.Phase != Phase.Over; i++)
        {
            Call(h);
            h.Result(2, h.Game.Attempt, Landed("Left 180"));
            while (h.Game.Phase == Phase.Match) h.Result(h.Game.Attempter, h.Game.Attempt, Outcome.Miss);
        }
        Assert.Equal(Phase.Over, h.Game.Phase);
        Assert.Equal(2UL, h.Game.Winner);
        Assert.Equal("Ben wins SLVSH!", h.Game.News);
        Assert.Equal(5, h.Game.Find(1)!.Letters);
    }

    [Test]
    static void PracticingAlone()
    {
        var h = new SlvshHost(1, "Ana", 45, 12, () => 0, solo: true);
        Assert.True(h.Start(), "solo starts with just you");
        Call(h);
        h.Result(1, h.Game.Attempt, Landed("Left 180"));
        Assert.Equal((Phase.Match, 1UL), (h.Game.Phase, h.Game.Attempter)); // nobody to vote; you match your own line
        h.Result(1, h.Game.Attempt, new Outcome(false, "Right 180"));
        Assert.Equal((Phase.Set, 1, 1UL), (h.Game.Phase, h.Game.Find(1)!.Letters, h.Game.Attempter));
        for (var i = 0; i < 4; i++)
        {
            Call(h);
            h.Result(1, h.Game.Attempt, Landed("Left 180"));
            h.Result(1, h.Game.Attempt, Outcome.Miss);
        }
        Assert.Equal(Phase.Over, h.Game.Phase);
    }

    [Test]
    static void TheVoteOverrulesTrickDetection()
    {
        var h = Host(voteSeconds: 12);
        h.Start();
        // Ana's set: detection says landed, so it goes to a vote of Ben and Cat.
        Call(h);
        h.Result(1, h.Game.Attempt, Landed("Left 180 Mute"));
        Assert.Equal((Phase.Vote, Phase.Set, "Left 180 Mute", true), (h.Game.Phase, h.Game.VotingOn, h.Game.Tried, h.Game.Detected));
        Assert.True(h.Game.Voters.SequenceEqual(new ulong[] { 2, 3 }));
        h.Vote(1, h.Game.Attempt, true); // the attempter can't vote on themselves
        Assert.Equal(0, h.Game.Ballots.Count);
        h.Vote(2, h.Game.Attempt, false);
        h.Vote(2, h.Game.Attempt, true); // changed their mind
        Assert.Equal(1, h.Game.Ballots.Count);
        h.Vote(3, h.Game.Attempt, true);
        Assert.Equal((Phase.Match, "Left 180", 2UL), (h.Game.Phase, h.Game.Trick, h.Game.Attempter));
        Assert.Equal("Ana landed the set (2-0 vote)", h.Game.News);

        // Ben's run matched by name but everyone saw him wash out: the vote says no.
        h.Result(2, h.Game.Attempt, Landed("Left 180"));
        Assert.True(h.Game.Detected);
        h.Vote(1, h.Game.Attempt, false);
        h.Vote(3, h.Game.Attempt, false);
        Assert.Equal(1, h.Game.Find(2)!.Letters);
        Assert.Equal("Ben did Left 180: S (2-0 vote)", h.Game.News);

        // Cat's trick was misread, but the others saw the match.
        h.Result(3, h.Game.Attempt, new Outcome(false, "Left 360"));
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
        Call(h);
        h.Result(1, h.Game.Attempt, Landed("Left 180"));
        now = 5;
        h.Tick();
        Assert.Equal(5, h.Game.VoteSecondsLeft);
        now = 11;
        h.Tick();
        Assert.Equal("Ana landed the set (nobody voted: trick detection decided)", h.Game.News);
        Assert.Equal(Phase.Match, h.Game.Phase);

        h.Result(2, h.Game.Attempt, new Outcome(false, "Front 540")); // detection: no match
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
        // A set with no trick at all (bailed) can't become the line to match.
        Call(h);
        h.Result(1, h.Game.Attempt, Outcome.Miss);
        Assert.Equal((Phase.Set, 2UL), (h.Game.Phase, h.Game.Attempter));
        // Not calling in time loses the set.
        Assert.Equal(GameState.CallSeconds, h.Game.SecondsLeft);
        now = GameState.CallSeconds + 1;
        h.Tick();
        Assert.Equal((Phase.Set, 3UL), (h.Game.Phase, h.Game.Attempter));
        Assert.True(h.Game.News.Contains("(no line called)"), h.Game.News);
        // Calling restarts the clock; running out of time on a match is a miss straight away.
        now = 200;
        Call(h);
        Assert.Equal(45, h.Game.SecondsLeft);
        h.Result(3, h.Game.Attempt, Landed("Left 180"));
        h.Vote(1, h.Game.Attempt, true);
        h.Vote(2, h.Game.Attempt, true);
        Assert.Equal((Phase.Match, 1UL), (h.Game.Phase, h.Game.Attempter));
        now = 300;
        h.Tick();
        Assert.Equal(1, h.Game.Find(1)!.Letters);
        Assert.True(h.Game.News.EndsWith("(out of time)"), h.Game.News);
    }

    [Test]
    static void LeavingDuringTurnsAndVotes()
    {
        var h = Host(voteSeconds: 10);
        h.Start();
        Call(h);
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
        h.Called(1, h.Game.Attempt, new[] { Left180, new CalledTrick { Type = TrickType.Rail, Stance = "switch", Spin = "left", Degrees = 270, Slide = "5050", Off = "back", OffAxis = "misty", OffCount = 1, OffDegrees = 720 } });
        h.Result(1, h.Game.Attempt, Landed("Left 180"));
        h.Vote(2, h.Game.Attempt, false);
        var copy = GameState.FromJson(JsonNode.Parse(h.Game.ToJson().ToJsonString()))!;
        Assert.True(h.Game.Players.SequenceEqual(copy.Players));
        Assert.True(h.Game.Voters.SequenceEqual(copy.Voters));
        Assert.True(h.Game.Ballots.SequenceEqual(copy.Ballots));
        Assert.True(h.Game.Call.SequenceEqual(copy.Call), "the called line");
        Assert.Equal(h.Game with { Players = copy.Players, Voters = copy.Voters, Ballots = copy.Ballots, Call = copy.Call }, copy);
        Assert.Equal(false, copy.BallotOf(2));
        Assert.True(GameState.FromJson(JsonNode.Parse("""{"t":"state"}""")) is null);
        Assert.True(GameState.FromJson(JsonNode.Parse("""{"t":"state","id":"x","players":[]}""")) is null);
        var big = h.Game.ToJson();
        big["players"]![0]!["letters"] = 99;
        big["phase"] = "99";
        big["call"]![0]!["spin"] = "sideways";
        var read = GameState.FromJson(big)!;
        Assert.Equal(5, read.Players[0].Letters);
        Assert.Equal(Phase.Over, read.Phase);
        Assert.Equal(0, read.Call.Count); // a junk trick drops the whole line
    }

    [Test]
    static void ReadingTheGamesTrickNames()
    {
        var cork = TrickName.Parse("Switch Right Dub Cork 1080 Safety Nosetap").Segments.Single();
        Assert.Equal((true, "right", "cork", 2, 1080), (cork.Switch, cork.Dir, cork.Axis, cork.Count, cork.Degrees));
        var flip = TrickName.Parse("Dub Backflip").Segments.Single();
        Assert.Equal(("backflip", 2, (string?)null), (flip.Axis, flip.Count, flip.Dir));
        var rail = TrickName.Parse("Left 270 On - Switch 5050 - Back Misty 720 Cuban Out");
        Assert.True(rail.Rail);
        Assert.Equal((SegmentKind.On, SegmentKind.Slide, SegmentKind.Out), (rail.Segments[0].Kind, rail.Segments[1].Kind, rail.Segments[2].Kind));
        Assert.Equal(("left", 270), (rail.Segments[0].Dir, rail.Segments[0].Degrees));
        Assert.Equal(("back", "misty", 720), (rail.Segments[2].Dir, rail.Segments[2].Axis, rail.Segments[2].Degrees));
        Assert.Equal("5050", TrickName.SlideKey(rail.Segments[1].Words));
        Assert.Equal("5-0", TrickName.SlideKey("5-0"));
        Assert.True(!TrickName.Parse("Straight Air").Rail);
    }

    [Test]
    static void MatchingCalledTricks()
    {
        CalledTrick Jump(string? stance = null, string? spin = null, string? axis = null, int? count = null, int? deg = null) =>
            new() { Type = TrickType.Jump, Stance = stance, Spin = spin, Axis = axis, Count = count, Degrees = deg };
        var leftTripCork = Jump(spin: "left", axis: "cork", count: 3, deg: 1800);
        Assert.Equal("Left Trip Cork 1800", leftTripCork.Describe());
        Assert.True(leftTripCork.Matches("Left Trip Cork 1800 Japan"), "grabs don't matter");
        Assert.True(leftTripCork.Matches("Switch Left Trip Cork 1800"), "stance is don't care");
        Assert.True(!leftTripCork.Matches("Left Dub Cork 1800"));
        Assert.True(!leftTripCork.Matches("Right Trip Cork 1800"));
        Assert.True(!Jump(stance: "regular").Matches("Switch Left 540"));
        Assert.True(Jump(stance: "switch", spin: "left", axis: "flat", deg: 540).Matches("Switch Left 540 Indy"));
        Assert.True(!Jump(axis: "flat", deg: 540).Matches("Left Cork 540"));
        Assert.True(Jump(deg: 1080).Matches("Right Dub Cork 1080 Safety"), "any way, any rotation");
        Assert.True(Jump(axis: "backflip", count: 2).Matches("Dub Backflip"));
        Assert.True(!Jump(axis: "backflip").Matches("Frontflip Tailtap"));
        Assert.True(Jump(spin: "none", axis: "flat").Matches("Straight Air"));
        Assert.True(!Jump().Matches("Regular On - Slide"), "a jump isn't a rail trick");

        var rail = new CalledTrick { Type = TrickType.Rail, Spin = "left", Degrees = 270, Slide = "slide", Off = "right", OffAxis = "flat", OffDegrees = 270 };
        Assert.Equal("Left 270 On - Slide - Right 270 Out", rail.Describe());
        Assert.True(rail.Matches("Left 270 On - Slide - Right 270 Out"));
        Assert.True(rail.Matches("Left 270 On - Slide - Right 270 Out - To Regular"));
        Assert.True(!rail.Matches("Left 270 On - Frontslide - Right 270 Out"));
        Assert.True(!rail.Matches("Left 270 On - Slide"), "didn't spin off");
        Assert.True(!rail.Matches("Left 270"), "a jump isn't a rail trick");
        var anyOff = rail with { Off = null, Slide = null };
        Assert.True(anyOff.Matches("Left 270 On - 5-0 - Back Misty 720 Out"));
        var straight = new CalledTrick { Type = TrickType.Rail, Stance = "regular", Spin = "none", Slide = "5-0", Off = "none" };
        Assert.True(straight.Matches("Regular On - 5-0 - To Regular"));
        Assert.True(!straight.Matches("Regular On - 5-0 - Front 270 Out"));
    }

    [Test]
    static void JudgingALine()
    {
        var cork = new CalledTrick { Type = TrickType.Jump, Axis = "cork", Count = 2, Degrees = 1080 };
        var rail = new CalledTrick { Type = TrickType.Rail, Spin = "left", Degrees = 270 };
        var line = new[] { rail, cork };

        var log = new TrickLog();
        var j = new TrickJudge(log, 10, line);
        log.AddReset(10.5); // respawning before the first trick is fine
        log.AddTrick(11, "Left 270 On", 1);
        Assert.True(j.Step(14) is null, "still on the rail");
        log.AddTrick(15, "Left 270 On - Slide - Right 270 Out", 1);
        log.AddTrick(20, "Straight Air", 2); // extras in between are fine
        log.AddTrick(25, "Left Dub Cork 1080 Safety", 3);
        Assert.True(j.Step(25.5) is null, "not ridden away yet");
        Assert.Equal(1, j.Matched);
        var landed = j.Step(27);
        Assert.True(landed is { Landed: true }, $"{landed}");
        Assert.Equal(11.0, j.TrickAt);

        // Out of order: the cork before the rail doesn't count.
        j = new TrickJudge(log, 30, line);
        log.AddTrick(31, "Left Dub Cork 1080", 4);
        log.AddTrick(36, "Left 270 On - Slide", 5);
        Assert.True(j.Step(40) is null);
        Assert.Equal(1, j.Matched);
        log.AddReset(41); // respawned before finishing: a miss
        var missed = j.Step(41.5)!;
        Assert.True(!missed.Landed && missed.Trick.Contains("Left Dub Cork 1080"), missed.Trick);

        // Respawning right after the last trick, before riding away, is a miss.
        j = new TrickJudge(log, 50, new[] { cork });
        log.AddTrick(51, "Right Dub Cork 1080", 6);
        log.AddReset(51.5);
        Assert.True(j.Step(52) is { Landed: false });

        // Bailing off a rail (a new trick while it's still going) doesn't count, but the run goes on.
        j = new TrickJudge(log, 60, new[] { rail });
        log.AddTrick(61, "Left 270 On", 7);
        log.AddTrick(62, "Zero Spin", 8);
        log.AddTrick(65, "Left 270 On - 5050 - To Regular", 9);
        Assert.True(j.Step(70) is { Landed: true });
    }

    [Test]
    static void BuildingALineInTheWheel()
    {
        var m = new CallMenu();
        void Pick(string label)
        {
            for (var guard = 0; guard < 5; guard++)
            {
                var i = m.Labels.ToList().IndexOf(label);
                if (i >= 0) { m.Pick(i); return; }
                m.Pick(m.Labels.Count - 1); // "More"
            }
            throw new InvalidOperationException($"no slice {label} at {m.Current}: {string.Join(", ", m.Labels)}");
        }
        Assert.Equal(CallMenu.Step.Stance, m.Current);
        Assert.True(m.Labels.Count <= CallMenu.PerPage);
        // A rail trick: left 270 on, slide, right 270 off.
        Pick("Don't care"); Pick("Rail"); Pick("Left"); Pick("270"); Pick("Slide"); Pick("Right"); Pick("Flat"); Pick("270");
        Assert.Equal(CallMenu.Step.Next, m.Current);
        Assert.Equal("Left 270 On - Slide - Right 270 Out", m.Done[0].Describe());
        // Then a don't-care dub cork 1080, with a wrong turn undone by Back.
        Pick("Add a trick"); Pick("Don't care"); Pick("Jump"); Pick("Don't care"); Pick("Misty");
        Assert.True(m.Picks.SequenceEqual(new[] { "Don't care", "Jump", "Don't care", "Misty" }), string.Join(",", m.Picks));
        m.Back();
        Assert.Equal(CallMenu.Step.Axis, m.Current);
        Pick("Cork"); Pick("Dub"); Pick("1080");
        Assert.Equal("Any way Dub Cork 1080", m.Done[1].Describe());
        // A trip cork 1800 is on the degrees wheel's second page.
        Pick("Add a trick"); Pick("Switch"); Pick("Jump"); Pick("Left"); Pick("Cork"); Pick("Trip"); Pick("1800");
        Assert.Equal("Switch Left Trip Cork 1800", m.Done[2].Describe());
        Pick("Undo last");
        Assert.Equal(2, m.Done.Count);
        Pick("Call it");
        Assert.True(m.Called is { Count: 2 });
        Assert.True(m.Called![1].Matches("Right Dub Cork 1080 Safety"));

        // Backing out of the first part closes it.
        var closed = new CallMenu();
        closed.Back();
        Assert.True(closed.Cancelled);

        // The stick picks slices clockwise from 12 o'clock.
        Assert.Equal(0, CallWheel.SliceAt(0, 1, 4));
        Assert.Equal(1, CallWheel.SliceAt(1, 0, 4));
        Assert.Equal(2, CallWheel.SliceAt(0, -1, 4));
        Assert.Equal(3, CallWheel.SliceAt(-1, 0.1f, 4));
    }

    [Test]
    static void FindingTheRiderToWatch()
    {
        var riders = new[]
        {
            new RiderCandidate("<b>Ana</b> ", new Vector3(0, 0, 0)),
            new RiderCandidate("", new Vector3(100, 0, 0)),
            new RiderCandidate("Someone Else", new Vector3(200, 0, 0), new[] { "Cat", "76561198000000003" }),
        };
        Assert.Equal(2, RiderMatch.Pick(76561198000000003, "Cat", null, riders)); // the game knows their Steam ID
        Assert.Equal(0, RiderMatch.Pick(1, "ana", null, riders));
        Assert.Equal(1, RiderMatch.Pick(2, "Ben", new Vector3(104, 2, 0), riders)); // no name: nearest to where Ben says he is
        Assert.Equal(-1, RiderMatch.Pick(2, "Ben", new Vector3(150, 0, 0), riders)); // nobody near: not on this server
        Assert.Equal(-1, RiderMatch.Pick(2, "Ben", null, riders));
    }

    [Test]
    static void ChasingTheWatchedRider()
    {
        var chase = new ChaseCamera();
        chase.Start(new Vector3(0, 0, 0), new Vector3(0, 5, -20));
        Assert.True(chase.Heading == new Vector3(0, 0, 1), "faces the rider from where the camera was");
        // Riding along +X at 10 m/s for 3 s: the camera ends up behind (-X) and above them.
        var at = new Vector3(0, 0, 0);
        for (var i = 0; i < 180; i++)
        {
            at += new Vector3(10f / 60, 0, 0);
            chase.Step(1f / 60, at);
        }
        Assert.True(chase.Heading.X > 0.95f, $"turned to their heading {chase.Heading}");
        var behind = chase.Eye - at;
        Assert.True(behind.X < -4 && behind.Y > 1.5f, $"behind and above them {behind}");
        // Standing still: the camera keeps its heading rather than swinging around.
        var heading = chase.Heading;
        for (var i = 0; i < 60; i++) chase.Step(1f / 60, at + new Vector3(0, 0, 0.001f * i));
        Assert.True(RiderMatch.Distance(heading, chase.Heading) < 0.05f, "keeps heading when slow");
        // A respawn far away: cut straight there.
        chase.Step(1f / 60, new Vector3(500, 20, 0));
        Assert.True(RiderMatch.Distance(chase.Eye, new Vector3(500, 20, 0)) < 8, "cuts to a respawned rider");
    }

    [Test]
    static void LookingAlongADirection()
    {
        static Vector3 Forward(Quaternion q) => new( // q * (0, 0, 1)
            2 * (q.X * q.Z + q.W * q.Y), 2 * (q.Y * q.Z - q.W * q.X), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        Assert.Equal(Quaternion.Identity, ChaseCamera.Look(new Vector3(0, 0, 1)));
        foreach (var direction in new[] { new Vector3(1, 0, 0), new Vector3(0.3f, -0.5f, -0.8f), new Vector3(-2, 1, 0.5f) })
        {
            var expected = direction * (1 / direction.Magnitude);
            Assert.True(RiderMatch.Distance(Forward(ChaseCamera.Look(direction)), expected) < 1e-4f, $"looks along {direction}");
        }
    }

    static IEnumerable<(SlvshView View, string Name)> Views()
    {
        var h = Host(voteSeconds: 12);
        yield return (new SlvshView { Me = 1, Game = h.Game }, "Lobby (host)");
        h.Start();
        yield return (new SlvshView { Me = 1, Game = h.Game with { SecondsLeft = 71 } }, "Calling your set");
        yield return (new SlvshView { Me = 2, Game = h.Game with { SecondsLeft = 71 } }, "Someone calling");
        h.Called(1, h.Game.Attempt, new[]
        {
            new CalledTrick { Type = TrickType.Rail, Spin = "left", Degrees = 270, Slide = "slide", Off = "right", OffAxis = "flat", OffDegrees = 270 },
            new CalledTrick { Type = TrickType.Jump, Stance = "switch", Spin = "left", Axis = "flat", Degrees = 540 },
        });
        yield return (new SlvshView { Me = 1, Game = h.Game with { SecondsLeft = 31 }, Trying = "Left 270 On - Slide - Right 270 Out" }, "Your set");
        yield return (new SlvshView { Me = 2, Game = h.Game with { SecondsLeft = 31 }, Watching = SpectateStatus.Watching }, "Watching the set");
        h.Result(1, h.Game.Attempt, Landed("Left 270 On - Slide - Right 270 Out  /  Switch Left 540 Indy"));
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
                case Phase.Set:
                    if (h.Game.Call.Count == 0) Call(h);
                    h.Result(h.Game.Attempter, h.Game.Attempt, h.Game.Attempter == 3 ? Landed("Zero Spin") : Outcome.Miss);
                    break;
                case Phase.Match: h.Result(h.Game.Attempter, h.Game.Attempt, h.Game.Attempter == 3 ? Landed("Zero Spin") : Outcome.Miss); break;
                case Phase.Vote: foreach (var voter in h.Game.Voters) h.Vote(voter, h.Game.Attempt, h.Game.Detected); break;
            }
        }
        yield return (new SlvshView { Me = 3, Game = h.Game }, "Game over");
        yield return (new SlvshView { Me = 2, Invite = ("Ana", 3) }, "Invite");
        yield return (new SlvshView { Me = 2, Hint = "Make or join a party first: press Ctrl+M for the Party panel" }, "Hint");
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
        Assert.True(Wheels().Count() == 4, "the wheel draws every step");
        var dir = Environment.GetEnvironmentVariable("SLVSH_PREVIEW");
        if (!string.IsNullOrEmpty(dir)) OverlayPreview.Save(Views().Select(v => (Scoreboard.Build(v.View)!, v.Name)).Concat(Wheels()), Path.Combine(dir, "slvsh.html"));
    }

    /// <summary>The call wheel at a few steps, for the preview.</summary>
    static IEnumerable<(OverlayFrame Frame, string Name)> Wheels()
    {
        var m = new CallMenu();
        yield return (CallWheel.Build(m, -1, "Backspace / B", "T / R-stick"), "Wheel: stance");
        m.Pick(1); m.Pick(0); m.Pick(0); m.Pick(1);
        yield return (CallWheel.Build(m, 2, "Backspace / B", "T / R-stick"), "Wheel: how many");
        m.Pick(2);
        yield return (CallWheel.Build(m, 4, "Backspace / B", "T / R-stick"), "Wheel: degrees");
        m.Pick(6);
        yield return (CallWheel.Build(m, -1, "Backspace / B", "T / R-stick"), "Wheel: next");
    }

    [Test]
    static void LinesTravelSafely()
    {
        var state = Enumerable.Range(0, 900).Select(i => Math.Sin(i) * 40).ToArray();
        var packed = LineStart.Pack(state);
        Assert.True(packed.Length < 60000, "a rider's state fits a message");
        Assert.True(LineStart.Unpack(packed)!.SequenceEqual(state), "round trip");
        Assert.True(LineStart.Unpack("not base64!") is null);
        Assert.True(LineStart.Unpack(LineStart.Pack(new[] { 1.0, double.NaN })) is null, "no NaN positions");
        Assert.True(LineStart.Unpack(LineStart.Pack(new double[LineStart.MaxDoubles + 1])) is null, "nothing oversized");

        var host = new SlvshHost(1, "Ana", 45, 0, () => 0, sameLine: true, runIn: 8);
        var copy = GameState.FromJson(host.Game.ToJson())!;
        Assert.True(copy.SameLine && copy.RunIn == 8, "the host's line rule reaches everyone");
        var junk = host.Game.ToJson();
        junk["runin"] = 999;
        Assert.Equal(15, GameState.FromJson(junk)!.RunIn);
    }
}
